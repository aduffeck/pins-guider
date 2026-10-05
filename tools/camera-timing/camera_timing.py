#!/usr/bin/env python3
# Usage: camera_timing.py [--device NAME] [--host HOST] [--port PORT] [--exposure S] [--waits S,S,...] [--count N]
#                         [--modes bare,pins]
#
# Measures when an INDI camera's exposures really start. For each wait between an image arriving and the next
# exposure request it records how long after the request the image arrives, how long after the previous image, the
# driver's first countdown after the request and the FITS DATE-OBS. An image that arrives sooner than the exposure
# after its request was exposed (partly) before it.
#
# Modes: "bare" sends CCD_EXPOSURE only; "pins" first sends the whole CCD_CONTROLS vector with its current values, as
# PINS' INDICamera.StartExposure does before every exposure (gain); "abort" sends CCD_ABORT_EXPOSURE right after each
# image (does that stop an exposure the camera started by itself?) and counts images that still arrive in the next 0.5 s.
#
# Talks INDI XML directly to indiserver (stdlib only). Stop guiding and anything else using the camera first: the
# script refuses to start while an exposure is running. Output: one CSV line per exposure, then a summary.
import argparse, base64, re, socket, statistics, sys, time
import xml.etree.ElementTree as ET
from datetime import datetime, timezone

ap = argparse.ArgumentParser()
ap.add_argument("--device", default="ZWO CCD ASI120MM-S")
ap.add_argument("--host", default="localhost")
ap.add_argument("--port", type=int, default=7624)
ap.add_argument("--exposure", type=float, default=2.5)
ap.add_argument("--waits", default="0,0.3,0.6,1,2")
ap.add_argument("--count", type=int, default=8)
ap.add_argument("--modes", default="bare,pins")
args = ap.parse_args()
DEV = args.device
WAITS = [float(w) for w in args.waits.split(",")]
MODES = args.modes.split(",")


class Indi:
    def __init__(self):
        self.sock = socket.create_connection((args.host, args.port), timeout=5)
        self.sock.settimeout(0.05)
        self.buf = b""
        self.controls = None          # current CCD_CONTROLS values, in element order
        self.exposure_state = None
        self.blob = None              # (arrival time, FITS header dict) of the last image
        self.done = None              # arrival time of the driver's end-of-exposure warning
        self.countdown = None         # (time, seconds left) of the driver's first countdown after a request
        self.expect_countdown = False
        self.blobs = 0                # images received so far

    def send(self, xml):
        self.sock.sendall(xml.encode())

    def pump(self, until=lambda: False, timeout=0.0):
        end = time.time() + timeout
        while True:
            try:
                data = self.sock.recv(1 << 20)
                if not data:
                    sys.exit("indiserver closed the connection")
                self.buf += data
            except socket.timeout:
                pass
            self.parse()
            if until() or time.time() >= end:
                return until()

    def parse(self):
        while True:
            i = self.buf.find(b"<")
            if i < 0:
                self.buf = b""
                return
            m = re.match(rb"<([A-Za-z]+)", self.buf[i:])
            j = self.buf.find(b">", i)
            if not m or j < 0:
                return
            if self.buf[j - 1:j] == b"/":
                elem, self.buf = self.buf[i:j + 1], self.buf[j + 1:]
            else:
                close = b"</" + m.group(1) + b">"
                k = self.buf.find(close, j)
                if k < 0:
                    return
                elem, self.buf = self.buf[i:k + len(close)], self.buf[k + len(close):]
            self.handle(elem, time.time())

    def handle(self, raw, t):
        if b"focal length is missing" in raw and DEV.encode() in raw:
            self.done = t
        try:
            e = ET.fromstring(raw)
        except ET.ParseError:
            return
        if e.get("device") != DEV:
            return
        name = e.get("name")
        if e.tag in ("defNumberVector", "setNumberVector") and name == "CCD_CONTROLS":
            vals = [(n.get("name"), n.text.strip()) for n in e if n.text]
            if e.tag == "defNumberVector" or self.controls is None:
                self.controls = vals
            else:
                upd = dict(vals)
                self.controls = [(k, upd.get(k, v)) for k, v in self.controls]
        elif e.tag in ("defNumberVector", "setNumberVector") and name == "CCD_EXPOSURE":
            self.exposure_state = e.get("state")
            if self.expect_countdown and e.get("state") == "Busy":
                left = next((float(n.text) for n in e if n.text), None)
                self.countdown = (t, left)
                self.expect_countdown = False
        elif e.tag == "setBLOBVector" and name == "CCD1":
            header = {}
            for b in e:
                if b.text:
                    fits = base64.b64decode(b.text.strip())
                    for c in range(0, min(len(fits), 2880 * 4), 80):
                        card = fits[c:c + 80].decode("ascii", "replace")
                        if card.startswith("END"):
                            break
                        if "=" in card[:10]:
                            header[card[:8].strip()] = card[10:].split("/")[0].strip().strip("'").strip()
            self.blob = (t, header)
            self.blobs += 1


def iso(t):
    return datetime.fromtimestamp(t, timezone.utc).strftime("%H:%M:%S.%f")[:-3]


def date_obs(header):
    v = header.get("DATE-OBS")
    if not v:
        return None
    try:
        return datetime.fromisoformat(v.replace("Z", "")).replace(tzinfo=timezone.utc).timestamp()
    except ValueError:
        return None


indi = Indi()
indi.send(f'<getProperties version="1.7" device="{DEV}"/>')
indi.send(f'<enableBLOB device="{DEV}">Also</enableBLOB>')
indi.pump(lambda: indi.controls is not None and indi.exposure_state is not None, timeout=5)
if indi.controls is None:
    sys.exit(f"no CCD_CONTROLS from {DEV}: is the driver running and the device name right?")
if indi.exposure_state == "Busy":
    sys.exit("an exposure is running: stop guiding (and anything else using the camera) first")


def expose():
    indi.blob = None
    indi.done = None
    indi.countdown = None
    indi.expect_countdown = True
    t = time.time()
    indi.send(f'<newNumberVector device="{DEV}" name="CCD_EXPOSURE"><oneNumber name="CCD_EXPOSURE_VALUE">'
              f'{args.exposure}</oneNumber></newNumberVector>')
    if not indi.pump(lambda: indi.blob is not None, timeout=args.exposure + 15):
        sys.exit("no image within the exposure + 15 s")
    return t


expose()  # warm-up, not counted
rows = []
print("mode,wait_s,request_utc,after_request_s,after_previous_image_s,driver_done_after_request_s,"
      "first_countdown_after_request_s,first_countdown_left_s,date_obs_minus_request_s,images_after_abort")
for mode in MODES:
    for wait in WAITS:
        for _ in range(args.count):
            prev = indi.blob[0]
            stale = ""
            if mode == "abort":
                n0 = indi.blobs
                indi.send(f'<newSwitchVector device="{DEV}" name="CCD_ABORT_EXPOSURE"><oneSwitch name="ABORT">On</oneSwitch></newSwitchVector>')
                indi.pump(timeout=0.5)
                stale = indi.blobs - n0
                if stale:
                    prev = indi.blob[0]
            indi.pump(timeout=wait)
            if mode == "pins":
                nums = "".join(f'<oneNumber name="{k}">{v}</oneNumber>' for k, v in indi.controls)
                indi.send(f'<newNumberVector device="{DEV}" name="CCD_CONTROLS">{nums}</newNumberVector>')
            t = expose()
            arrived, header = indi.blob
            obs = date_obs(header)
            cd = indi.countdown
            row = dict(mode=mode, wait=wait, after=arrived - t, since_prev=arrived - prev,
                       done=(indi.done - t) if indi.done else None,
                       cd_t=(cd[0] - t) if cd else None, cd_left=cd[1] if cd else None,
                       obs=(obs - t) if obs else None)
            rows.append(row)
            f = lambda v: "" if v is None else f"{v:.3f}"
            print(f"{mode},{wait},{iso(t)},{f(row['after'])},{f(row['since_prev'])},{f(row['done'])},"
                  f"{f(row['cd_t'])},{f(row['cd_left'])},{f(row['obs'])},{stale}", flush=True)

print(f"\nsummary ({args.exposure} s exposures; 'early' = image sooner than exposure + 0.1 s after its request)")
print("mode  wait  n  early  median after request  median after previous image  median first countdown left")
for mode in MODES:
    for wait in WAITS:
        g = [r for r in rows if r["mode"] == mode and r["wait"] == wait]
        if not g:
            continue
        early = sum(1 for r in g if r["after"] < args.exposure + 0.1)
        med = lambda k: statistics.median(r[k] for r in g if r[k] is not None) if any(r[k] is not None for r in g) else float("nan")
        print(f"{mode:5} {wait:4.1f} {len(g):2d} {early:5d}  {med('after'):19.2f}  {med('since_prev'):27.2f}  {med('cd_left'):27.2f}")
