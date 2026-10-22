# AGENTS.md

Not a web app: a headless Tizen .NET service app (C#, `tizen90`) that runs on the TV, plus host-side Python helpers and a host-side .NET test harness. Default branch: `master`. Public repo: never add IPs/hostnames/credentials to any file.

## Run
Build (needs .NET 8 SDK only; `Tizen.NET.Sdk/1.1.9` restored from NuGet, no Tizen Studio). If the host has no SDK, use `podman run --rm -v "$PWD":/src:Z -w /src mcr.microsoft.com/dotnet/sdk:8.0 ...`:
```bash
dotnet msbuild TizenAmbilight/TizenAmbilight.csproj -restore -t:Rebuild -p:Configuration=Release -v:m
```
Output: `TizenAmbilight/bin/Release/tizen90/dev.ritwickverma.TizenAmbilight-1.0.0.tpk` (log prints `is signed with Default Certificates!`; that TPK installs on no retail TV until re-signed).
Settings: `TizenAmbilight/config.json` (gitignored, holds LAN addresses) is bundled as `res/config.json`; without it the committed template `config.example.json` ships instead. The owner's local `config.json` reproduces the pre-v5 defaults; never commit it.

Helpers (host side, plain `python3`, stdlib only):
```bash
python3 tools/control-server.py [--bind ADDR] [--port 8792] [--file /tmp/amb-control]
python3 tools/stats-receiver.py [--port 8791] [--out ./ambstats] [--bind ADDR] [--max-body N] [--allow-name NAME]
python3 tools/check-secrets.py --staged    # pre-commit; --all scans the whole tree
python3 tools/check-apps2samsung.py        # catalog entry find strings vs config.example.json
```
Signing/install/run on the TV (`tizen install`, `tizen run`) needs Tizen Studio CLI and a real device: not available to an agent, (unverified) here. Never install while the app is capturing: send `pause` via the control file first (Pitfall 1).

## Test
- Framework: custom console harness `decoder-selftest/` (`net8.0`, no xUnit/NUnit); exits non-zero on any FAIL. Prints `PASSED n  FAILED m`.
- Test data is generated, then checked in: `gen_testdata.py` (hand-written PNG encoder, no deps) and `gen_jpeg_testdata.py` (needs **Pillow**; Pillow's decode is the reference). Output goes to `decoder-selftest/testdata/`; regenerating at HEAD left `git status` clean.
- The harness compiles the *same* source files the app uses (`PngDecoder`, `JpegDecoder`, `PngSelfTest`, `ZoneEngine`, `WledOutput`, `Json`, `VideoPlaneCapture`, `ConfigFile`, `PowerState`) via `<Compile Include="../TizenAmbilight/src/...">`. Add a new shared file there or the harness will not see it.
- On-device self-check: `PngSelfTest.cs` runs at app start and reports PASS/FAIL in the startup telemetry.
- No mocking framework. The Tizen/P-Invoke layer (`CaptureApi`, `VideoPlaneCapture` capture calls, the HTTP part of `PowerState`, `Preference`) is not unit tested.

Canonical command:
```bash
cd decoder-selftest
python3 gen_testdata.py && python3 gen_jpeg_testdata.py
dotnet run --project DecoderSelfTest.csproj
```
Expected: `PASSED 79   FAILED 0`.

CI: `.gitlab-ci.yml` (lint job + one .NET build-and-test job; runs on the linked shared runner). The GitLab project push-mirrors every branch and tag to GitHub, where `.github/workflows/ci.yml` repeats the checks and `release.yml` publishes a GitHub Release with the TPK on a `vX.Y.Z` tag that must equal the manifest version. Apps2Samsung installs from that GitHub release.

## Source modules
| Path | Responsibility |
|---|---|
| `TizenAmbilight/src/Program.cs` | `App` (ServiceApplication entry), `Ambilight.Run` startup and the main loop. `Ambilight` is a `static partial class` split across the next five files. |
| `TizenAmbilight/src/Config.cs` | `Config` fields, `ReadConfig` defaults/clamps, Preference > config.json > default lookup. |
| `TizenAmbilight/src/ConfigFile.cs` | First-party flat-JSON reader for `res/config.json` (no Tizen deps; tested in the harness). |
| `TizenAmbilight/src/Control.cs` | Live control-token parser (`ApplyControl`). |
| `TizenAmbilight/src/Io.cs` | Network worker threads (control poll, power poll, stats POST), platform/firmware detection. |
| `TizenAmbilight/src/Lifecycle.cs` | Shutdown, log flush, blackout hold. |
| `TizenAmbilight/src/Telemetry.cs` | `Stats` and the report builders. |
| `TizenAmbilight/src/VideoPlaneCapture.cs` | Default backend: `dlopen`s `libvideo-capture`, fills NV12 buffers, YUV to RGB with HDR/gamut handling. |
| `TizenAmbilight/src/CaptureApi.cs` | Legacy file backend: P/Invoke of `dc_request_capture_to_file_sync` with the 296-byte request struct. |
| `TizenAmbilight/src/ZoneEngine.cs` | Perimeter zone layout, per-zone averaging, smoothing/tone `Colors` helpers. |
| `TizenAmbilight/src/WledOutput.cs` | WLED DRGB UDP packet building/sending, blackout hold. |
| `TizenAmbilight/src/PowerState.cs` | Reads the TV's own local power-state API (`powerStateUrl`); fails toward "on". |
| `TizenAmbilight/src/PngDecoder.cs`, `JpegDecoder.cs` | First-party PNG and baseline-JPEG decoders (refuse CMYK/progressive/12-bit). |
| `TizenAmbilight/src/Json.cs` | Minimal first-party JSON writer for telemetry. |
| `TizenAmbilight/src/PngSelfTest.cs` | Embedded 8x8 PNG decode check run on the device. |
| `decoder-selftest/SelfTestMain.cs` | Host harness `Main`; byte-compares decoders, zone layout, EMA, DRGB packet, JSON. |
| `tools/control-server.py` | Read-only HTTP GET server that serves the control-token body from a local file. |
| `tools/stats-receiver.py` | HTTP receiver for telemetry POSTs (`/probe`) and frame dumps (`/file`). |
| `tools/check-secrets.py` | Credential-shape scanner (`--staged` / `--all`), exit 1 on findings. |
| `tools/check-apps2samsung.py` | Verifies `packaging/apps2samsung/*.json` `user_settings` find strings occur exactly once in `config.example.json`. |
| `docs/` | `how-it-works.md`, `configuration.md` (all keys and tokens), `development.md` (build, signing, tests, release). |

## Data flow
- In repo: all source, `tizen-manifest.xml` (appid `dev.ritwickverma.TizenAmbilight` since v5, was `io.gh.hermes.TizenAmbilight`; service app, `on-boot=true`, `auto-restart=false`, privileges `internet` + `network.get`), `decoder-selftest/testdata/`.
- Not in repo: signing certs/profiles (`.gitignore` covers `*.p12 *.pem *.key certs/ profiles.xml` etc.), `TizenAmbilight/config.json`, runtime logs, telemetry output (`--out` dir: `latest.json`, `history.log`, `appframe_Y|C`).
- Runtime path: TV video plane (960x540 NV12) -> N perimeter zones (default = `ledCount`, else 73) -> colour/tone pipeline -> one WLED DRGB UDP datagram per frame (port 21324; byte0 `0x02`, byte1 timeout s, then RGB per LED).
- Control channel: app GETs `controlUrl` every 250 ms; body is comma-separated tokens (`pause`, `run`, `hdr=1`, `backend=1`, `shutdown=1`, ...). Full token and settings list: `docs/configuration.md`. Tokens are **one-shot**; an omitted token keeps its last value.
- Local pause file: `/tmp/amb-control` (default of `tools/control-server.py`).
- Telemetry: app POSTs JSON every `statsIntervalSec` to `<probeEndpoint>/probe`.
- All endpoints are unauthenticated by design (SECURITY.md); do not add listeners that bind to all interfaces by default.
- Deployment: Apps2Samsung (catalog entry in `packaging/apps2samsung/`, re-signs per TV) or manual (sign with your own profile, `tizen install`). Endpoints `wledIp`/`controlUrl`/`probeEndpoint` default to empty = disabled.

## Code conventions
- C#: block-scoped `namespace TizenAmbilight { }` in every src file, `public static class` per concern, XML `///` doc comments that record *why* (several describe past bugs). No analyzers/`.editorconfig`/formatter in repo. Zero NuGet dependencies by design; keep it that way.
- `CS0649` is suppressed in the app csproj (`NoWarn`).
- Bump `Ambilight.Version` (`"v5"`) in `Program.cs` whenever behaviour changes, so telemetry identifies the build.
- Control tokens take numbers only; validate ranges in the `Control.cs` token switch.
- Python: stdlib `argparse` + `http.server`, shebang `python3`; no linter or type checker configured.
- Commits: no enforced style; history uses `type: summary` (`security:`, `gitignore:`) with an explanatory body. Use conventional commits.
- Never commit certs, keystores, tokens; run `python3 tools/check-secrets.py --staged` first.

## Pitfalls
1. `tizen install` SIGKILLs the app; mid-capture this wedges the TV scaler (every later capture returns `rc=-2` until a TV power cycle; README: 3 of 3 deploys). Always set `pause` first (`a57ffb8`; `tools/control-server.py` docstring).
2. Control tokens with non-numeric values are silently dropped (`double.TryParse` then `continue`); NaN/Infinity are rejected explicitly. A string token looks implemented and does nothing (`a57ffb8`, `Program.cs` token parser comment).
3. Capture `rc=-1` right after start is normal "not ready"; `-2` is wedge; `-4` is DRM content (README).
4. A stale frame is not a live frame: a paused video leaves its last plane, so capture keeps succeeding. Stale-signature fade exists for this (`a57ffb8`).
5. Unwritten LEDs keep WLED's own output: write unused LEDs black every frame; a single blackout datagram expires, so blanking re-sends at most once per second (`BlackoutHoldMs` must stay well under the blackout timeout byte) (`a57ffb8`, `WledOutput.cs`, `Program.cs`).
6. `NoteGoodFrame()` was only called on the video-plane path, so with `captureBackend=file` the strip was never blanked after a stall; both backends must call it (`a57ffb8`, `Program.cs` comment).
7. Standby sleep used a process-lifetime counter (`st.PowerSkipped`), so after the first standby every later one slept long; use the per-episode counter (`a57ffb8`, `Program.cs` comment).
8. Zone sampling over the full active height made all zones on a side edge identical; each side zone must keep its own vertical segment (`a57ffb8`, `ZoneEngine.cs`).
9. `Ambilight.Version` stayed `v3` through major behaviour changes and made build ID useless (`a57ffb8`, `Program.cs` comment).
10. Do not apply the PQ transfer per channel (invents saturation); matrix in code domain, tone-map luma, scale RGB by one factor. `gain>1` clips; raise brightness via `gamma` (`a57ffb8`, README "Colour handling").
11. The letterbox row test gives false positives on the reference TV (observed cropping 25% of the picture). Since v5 rows are behind `barRows` (default 0); `bars` still drives pillarbox columns (`docs/how-it-works.md`).
12. The Tizen SDK's default resource glob is Windows-only (`res\**\*`), so the icon never entered the TPK on Linux; the csproj declares `TizenTpkFiles` explicitly (`a57ffb8`, csproj comment).
13. `stats-receiver.py` once accepted arbitrary file basenames and unbounded bodies; it now has an allow-list (400) and body cap (413). `control-server.py` is GET-only and answers writes with 405 (`9ceb0b9`).
14. WLED `ota.lock` only takes the password under key `psk`; `pwd` belongs to the secure store and is silently ignored via `/json/cfg` (`9ceb0b9`).
15. `.gitignore` originally covered certs but not `.env`/`*.cred`/`*.key`/`credentials.json`, so a token could have been committed (`10812e7`).
16. `decoder-selftest` failed to build until `VideoPlaneCapture.cs` was added to its csproj (`4bca230`): every shared file must be listed there.
17. Before v5 the owner's LAN addresses were compiled-in defaults (still in git history before `23c4bdb`); every other install would have sent UDP/HTTP to them. Defaults must stay empty.
18. Startup gated the main loop on the legacy file backend's library even with the video plane selected; gate on the selected backend (`23c4bdb`).
19. Apps2Samsung patches `res/config.json` by exact find/replace on `config.example.json` lines; reformatting those lines breaks every install (`tools/check-apps2samsung.py` guards it). It reads package ids only from `config.xml`, so its "remove old version" option does nothing for a TPK, and it does not stop the running app before reinstalling (Pitfall 1).
