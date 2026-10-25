# Development

Building from source, signing and installing yourself, tests, diagnostics, and releases.

## Contents

- [Build](#build)
- [Sign and install with your own certificate](#sign-and-install-with-your-own-certificate)
- [Tests](#tests)
- [Diagnostics: telemetry and frame dumps](#diagnostics-telemetry-and-frame-dumps)
- [Project layout](#project-layout)
- [Releasing](#releasing)

---

## Build

You need the .NET 8 SDK. The `Tizen.NET.Sdk` build targets are restored from NuGet, so Tizen Studio is
**not** needed just to build.

```bash
cp TizenAmbilight/config.example.json TizenAmbilight/config.json   # then edit it
dotnet msbuild TizenAmbilight/TizenAmbilight.csproj -restore -t:Rebuild -p:Configuration=Release
```

The output is `TizenAmbilight/bin/Release/tizen90/dev.ritwickverma.TizenAmbilight-<version>.tpk`.
The build logs `is signed with Default Certificates!`. A retail TV rejects that signature, so the
package has to be re-signed for your TV (next section).

No .NET SDK installed? Build in a container:

```bash
podman run --rm -v "$PWD":/src:Z -w /src mcr.microsoft.com/dotnet/sdk:8.0 \
  dotnet msbuild TizenAmbilight/TizenAmbilight.csproj -restore -t:Rebuild -p:Configuration=Release
```

The project has **zero NuGet dependencies** by design. The PNG and baseline-JPEG decoders, the JSON
reader and writer, and the capture P/Invoke are all first-party code. Keep it that way.

## Sign and install with your own certificate

Samsung TVs only install packages signed with a **distributor certificate that lists the TV's DUID**.
A TPK signed for one TV therefore installs on no other TV, and a "signed release" that installs
everywhere is impossible outside the Samsung store. That is why a release only attaches a build made
with the Tizen SDK's default certificate (a retail TV rejects it): it is input for Apps2Samsung, which
re-signs it for each user's TV, or for you to re-sign below. Nothing in this repo is signed for any
particular TV, and no signing material is committed.

To do it yourself:

1. Install [Tizen Studio](https://developer.samsung.com/smarttv/develop/getting-started/setting-up-sdk/installing-tv-sdk.html)
   (the CLI is enough) with the *Samsung Certificate Extension*.
2. On the TV: **Apps**, type `1 2 3 4 5` on the remote, turn **Developer Mode** on, enter your computer's
   IP, then **cold-boot** the TV (unplug it for about 60 s). The debug bridge only starts at boot.
3. `sdb connect <tv-ip>`, then create a Samsung certificate profile in Certificate Manager. Choose the
   connected TV so its DUID goes into the distributor certificate, and choose the **`partner`**
   privilege level. With a `public` certificate the app installs and starts, but Tizen refuses to load
   the capture libraries (`libtzcapturec.so: Operation not permitted` in the startup log), so no frame
   is ever captured. Reported on a 2025 Q7F (GitHub issue #1); the reference TV is signed partner.
4. Re-sign and install:

   ```bash
   cd TizenAmbilight/bin/Release/tizen90
   tizen package -t tpk -s <your-profile> -- dev.ritwickverma.TizenAmbilight-1.0.0.tpk
   tizen install -n dev.ritwickverma.TizenAmbilight-1.0.0.tpk -t <device-name>
   ```

   Read [Updating safely](../README.md#updating-safely) before installing over a running copy.

Signing material (`*.p12`, `profiles.xml`, `author-signature.xml`, ...) is gitignored and must never be
committed. Run `python3 tools/check-secrets.py --staged` before each commit, or install it as a
pre-commit hook:

```bash
printf '#!/bin/sh\nexec python3 tools/check-secrets.py --staged\n' > .git/hooks/pre-commit
chmod +x .git/hooks/pre-commit
```

## Tests

`decoder-selftest/` is a plain `net8.0` console harness. It compiles the **same source files** as the
app (decoders, zone engine, WLED packet builder, JSON, config parser, power-state parser, colour maths)
and checks them on the host:

```bash
dotnet run --project decoder-selftest/DecoderSelfTest.csproj     # ends "PASSED n   FAILED 0"
```

* PNG decoding is byte-compared against hand-written PNGs, one per scanline filter type.
* JPEG decoding is compared against real JPEGs encoded by Pillow, with **libjpeg-turbo's** decode as the
  reference, across 4:4:4 / 4:2:2 / 4:2:0, grayscale, restart markers and odd dimensions (worst
  per-channel error 3/255). Both decoders must **refuse** what they do not support (CMYK, progressive,
  12-bit, truncated input) rather than decode it wrongly.
* Zone layout, EMA arithmetic, DRGB packets and LED mapping, `config.json` parsing and power-state
  extraction are covered as well.

The test data is generated, then checked in. To regenerate it (the JPEG cases need Pillow):

```bash
cd decoder-selftest && python3 gen_testdata.py && python3 gen_jpeg_testdata.py
```

A new shared source file must be added to `DecoderSelfTest.csproj` with `<Compile Include=...>`, or the
harness will not see it. The Tizen and P/Invoke layer (`VideoPlaneCapture` capture calls, `CaptureApi`,
`Preference`) cannot run off the TV and is not unit-tested. On the device, `PngSelfTest.cs` checks the
PNG decoder at every start and reports PASS/FAIL in the startup telemetry.

## Diagnostics: telemetry and frame dumps

Set `probeEndpoint` in `config.json` and run the collector on a computer on the same LAN:

```bash
python3 tools/stats-receiver.py --bind 192.168.1.10 --port 8791 --out ./ambstats
```

Every `statsIntervalSec` the app POSTs a JSON report to `/probe`. The collector keeps `latest.json`
(the newest report, verbatim) and `history.log` (one line per report). Reports carry the frame rate and
per-stage timings, capture status and failure counters, power state, the effective tone curve
(`adaptive`, `tone`), `decode`, `bars`, `mapping`, a zone colour sample, `tv` (model, platform,
`captureApi=ok|UNAVAILABLE`), and `log_tail`.

Two rules shaped it: **a telemetry key must describe what it actually holds**, and **anything invisible
to telemetry may as well not exist**. A fix has to be verifiable, not just asserted.

To see exactly what feeds the strip, send `dump=1` on the control channel and render the planes:

```bash
cat ambstats/appframe_Y ambstats/appframe_C > /tmp/f.nv12
ffmpeg -f rawvideo -pix_fmt nv12 -s 960x540 -i /tmp/f.nv12 -y /tmp/f.png
```

## Project layout

| path | what |
|---|---|
| `TizenAmbilight/src/Program.cs` | entry point, startup, the main loop |
| `TizenAmbilight/src/Config.cs`, `ConfigFile.cs` | settings resolution; `config.json` reader |
| `TizenAmbilight/src/Control.cs` | live control-token parser |
| `TizenAmbilight/src/Io.cs` | network worker threads, platform/firmware detection |
| `TizenAmbilight/src/Lifecycle.cs` | shutdown and the blackout paths |
| `TizenAmbilight/src/Telemetry.cs` | run statistics and reports |
| `TizenAmbilight/src/VideoPlaneCapture.cs` | default backend: `libvideo-capture`, YUV→RGB, HDR, gamut |
| `TizenAmbilight/src/CaptureApi.cs` | legacy file backend: `dc_request_capture_to_file_sync` |
| `TizenAmbilight/src/ZoneEngine.cs` | zone layout, per-zone averaging, smoothing helpers |
| `TizenAmbilight/src/WledOutput.cs` | DRGB packet building, mapping, tone LUT, UDP send |
| `TizenAmbilight/src/PowerState.cs` | TV local-API power probe |
| `TizenAmbilight/src/PngDecoder.cs`, `JpegDecoder.cs`, `Json.cs`, `PngSelfTest.cs` | first-party codecs and checks |
| `decoder-selftest/` | host test harness and its generated test data |
| `tools/` | control server, telemetry collector, secret scanner, catalog-entry check |
| `packaging/apps2samsung/` | the tizen-community-packages catalog entry |

C# conventions: block-scoped `namespace TizenAmbilight { }`, one `static` class per concern, and `///`
comments that record *why* (several document past bugs and must stay accurate). Bump
`Ambilight.Version` in `Program.cs` whenever behaviour changes, so telemetry identifies the build.

## Releasing

1. Update `CHANGELOG.md` and bump `version` in `TizenAmbilight/tizen-manifest.xml`.
2. Merge to `master`. CI (`.gitlab-ci.yml`, mirrored as `.github/workflows/ci.yml`) runs the checks,
   the harness and a Release build.
3. Tag `vX.Y.Z` (it must equal the manifest version) and push the tag. The GitLab project push-mirrors
   to GitHub, where `.github/workflows/release.yml` builds the TPK and publishes a GitHub Release with
   the `.tpk` and `SHA256SUMS`. That release is what Apps2Samsung installs.

The catalog entry in `packaging/apps2samsung/` patches `res/config.json` from the installer's form, by
exact find/replace on lines of `config.example.json`. `tools/check-apps2samsung.py` (run in CI) fails if
the two drift apart. The entry is listed in
[tizen-community-packages](https://github.com/Apps2Samsung/tizen-community-packages)
(`packages/RitwickVerma__TizenAmbilight.json`, merged in PR #132). A new release needs no catalog change:
Apps2Samsung picks up the latest GitHub Release. A change to the entry itself (new form field, new
label) needs a pull request there as well as here, and the two copies must stay identical.
