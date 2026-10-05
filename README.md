# pins-guider

Native autoguider for [PINS](https://github.com/nitr57/pins) with a Touch-N-Stars UI —
PHD2-level precision, INDI guide cameras, multi-star guiding. See [docs/DESIGN.md](docs/DESIGN.md).

## Layout

| Path | Content |
|---|---|
| `src/PinsGuider.Engine` | Host-agnostic guiding engine (pure C#, no NINA dependency) |
| `src/PinsGuider.Plugin` | NINA/PINS plugin: the `PinsNativeGuider` guider, INDI camera and pulse outputs, settings, storage |
| `tests/PinsGuider.Engine.Tests` | NUnit tests, synthetic star fields, closed-loop simulator tests |
| `tests/PinsGuider.Plugin.Tests` | NUnit tests of the plugin (built inside a pins checkout) |
| `tools/phd2-parity` | Harness comparing star measurement against PHD2's own `star.cpp` |
| `tools/phd2-algo-golden` | Generator of golden guide-algorithm sequences from PHD2's sources |
| `tools/e2e` | End-to-end tests of a PINS image with the plugin, against the INDI simulators |
| `tools/smoke` | Smoke test of a full PINS image: native guider API, WebSocket, UI screenshots |
| `docs` | Design ([DESIGN.md](docs/DESIGN.md)), usage, Coach, flight recorder and algorithm notes |

## Build & test

No local .NET SDK needed — `./build.sh` runs inside `mcr.microsoft.com/dotnet/sdk:10.0`:

```sh
./build.sh                      # dotnet test PinsGuider.slnx, without the Slow, Benchmark and Performance categories
./build.sh test-all             # all tests but the [Explicit] benchmarks (about 20 minutes)
./build.sh build PinsGuider.slnx
```

The plugin (`src/PinsGuider.Plugin`, `tests/PinsGuider.Plugin.Tests`) builds only inside a PINS checkout, with this
repo at `NINA.Plugins/pins-guider`.

## License

MPL-2.0. Portions ported from PHD2 are additionally BSD-3-Clause; see
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
