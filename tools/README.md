# Tools

* `phd2-parity/` — compares star detection with PHD2's compiled `star.cpp` (see its README).
* `phd2-algo-golden/` — generates golden sequences from PHD2's guide algorithm sources.
* `e2e/` — end-to-end harness: builds a PINS image with the plugin (from a pins checkout that has this repo at
  `NINA.Plugins/pins-guider`), runs it with bridge networking on remapped ports and drives ninaAPI through the
  built-in simulator, the INDI camera/telescope simulators (with a test-only GSC layer), camera ST4 and
  robustness scenarios. `PINS_WT=<pins checkout> ./run-e2e.sh --build`.
* `camera-timing/` — measures when an INDI camera's exposures really start (images that arrive sooner than the exposure
  after the request), with and without PINS' per-exposure `CCD_CONTROLS` update. Runs on the rig against indiserver
  with the guider stopped: `python3 camera_timing.py --device "ZWO CCD ASI120MM-S"`. `results/` holds the ASI120MM-S
  runs of 2026-09-27 (indi_asi_ccd 2.7: about every second exposure starts when the previous image arrives).
* `guide-log-stats/` — per-block statistics of a guide log for live A/B tests of guide settings (RMS, error
  autocorrelation, overshoot after the largest corrections, share of the error after jumps, pulse lengths, Dec
  reversals); blocks start at every settings change or every `--block-min` minutes:
  `python3 guide_log_stats.py PinsGuider_GuideLog_<night>.txt --block-min 20`. Standard library only, so it runs on the rig.
* `smoke/` — smoke test of a full image (TNS native guider REST API, WebSocket, UI screenshots through
  headless Chrome/DevTools): `IMAGE=<pins image> ./smoke.sh`; results go to `OUT_DIR` (default: a new temp dir).
  Browse the container through its bridge IP so the app's ninaAPI port discovery (1888) reaches the test
  container and not a PINS instance on the host.
