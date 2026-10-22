# TizenAmbilight

[![pipeline](https://gitlab.com/ritwick7/TizenAmbilight/badges/master/pipeline.svg)](https://gitlab.com/ritwick7/TizenAmbilight/-/pipelines)
[![license: MIT](https://img.shields.io/badge/license-MIT-blue)](LICENSE)
[![site](https://img.shields.io/badge/site-ritwickverma.dev-ff6f00)](https://www.ritwickverma.dev/)
[![tip](https://img.shields.io/badge/tip-buy_covfefe_for_Ritwick-e91e63)](https://buycovfefefor.ritwickverma.dev/)

**A real-frame ambilight for Samsung Tizen TVs.** One small headless app runs *on the TV*, reads the
decoded video picture straight out of memory, and drives a [WLED](https://kno.wled.ge/) LED strip over
your Wi-Fi.

* **No extra hardware.** No capture card, HDMI splitter or Raspberry Pi; just the TV and a WLED strip.
* **No middleware.** No Hyperion or HyperHDR server in between. One process goes from capture to colour
  maths to UDP, at about 25 fps.
* **The whole picture.** It averages the full 960×540 video plane into perimeter zones, rather than a
  handful of sample points.
* **HDR-aware.** It decodes PQ (HDR10) properly, maps BT.2020 colours into what LEDs can show, and adapts
  the tone curve to the scene.

> [!WARNING]
> **Status: early, and tested on one TV.** It was developed on a 2025 Samsung S84F (Tizen 9). It relies
> on an undocumented Samsung library that may be missing or different on other models and firmware.
> If you try it on another TV, please [report what happened](#device-reports), whether it worked or not.

---

## Contents

- [Will it work on my TV?](#will-it-work-on-my-tv)
- [What you need](#what-you-need)
- [Install](#install)
- [Configuration](#configuration)
- [Updating safely](#updating-safely)
- [Troubleshooting](#troubleshooting)
- [FAQ](#faq)
- [How it works](#how-it-works)
- [Contributing and device reports](#contributing)
- [Security](#security) · [Credits](#credits) · [License](#license)

## Will it work on my TV?

| Model year | Tizen | Status |
|---|---|---|
| 2025 | 9.0 | ✅ Works on the S84F (OLED, `QN77S84FAEXZA`) |
| 2026 | 10.0 | ❔ Untested |
| 2022–2024 | 6.5–8.0 | ❔ Untested. The app's runtime should install; whether the capture library exists is unknown. |
| 2021 and older | ≤ 6.0 | ❌ Probably not, without porting work (older .NET runtime, different capture APIs) |

The app checks for the capture library at startup and reports `captureApi=ok` or `UNAVAILABLE` in its
[telemetry](docs/development.md#diagnostics-telemetry-and-frame-dumps). It never guesses.

DRM-protected content (many streaming apps) withholds the video plane. While such content plays, the
strip goes dark instead of showing wrong colours.

## What you need

* A Samsung Tizen TV with **Developer Mode** enabled. Open **Apps**, type `1 2 3 4 5` on the remote,
  switch Developer Mode on and enter your computer's IP, then unplug the TV for about 60 s.
* A **WLED** controller (ESP32/ESP8266) driving an LED strip around the TV, on the same network.
  Up to 490 LEDs.
* **Recommended in WLED:** give the controller a fixed IP (a DHCP reservation), and enable
  *Settings → Sync Interfaces → Realtime → "Force max brightness"* (or set the brightness to 255). WLED's master
  brightness also scales realtime data, so a controller at the default of 128 shows the ambilight at
  50%.

## Install

### Option A: Apps2Samsung (easiest, no build) — *pending listing*

[Apps2Samsung](https://github.com/Apps2Samsung/Apps2Samsung) signs apps for your specific TV and
installs them. Once TizenAmbilight is listed in its community catalog, its install form asks for:

| field | example |
|---|---|
| WLED IP address | `192.168.1.50` |
| Number of LEDs | `96` |
| Where the strip's first LED sits, counted clockwise from the screen's top-left corner | `0` |
| Strip runs counter-clockwise? | `0` |

Not sure about the last two? Use `0` and `0`, then see [Getting the LED mapping right](docs/configuration.md#getting-the-led-mapping-right).

### Option B: build from source

```bash
git clone https://github.com/RitwickVerma/TizenAmbilight.git && cd TizenAmbilight
cp TizenAmbilight/config.example.json TizenAmbilight/config.json      # set wledIp, ledCount, ...
dotnet msbuild TizenAmbilight/TizenAmbilight.csproj -restore -t:Rebuild -p:Configuration=Release
```

Then sign the TPK for your TV with your own Samsung certificate and install it with the Tizen CLI. The
steps are in [docs/development.md](docs/development.md#sign-and-install-with-your-own-certificate).

### Starting it

TizenAmbilight is a background service with no icon or screen. It is set to start when the TV boots
(`on-boot`), so after installing, **power-cycle the TV** (unplug it, or hold the power button). Some
other sideloaded projects report that `on-boot` is ignored on their firmware. If the strip stays dark
after a cold boot, see [Troubleshooting](#troubleshooting).

## Configuration

Settings live in `config.json`, which is bundled into the app when it is built or installed. The
essentials:

```json
{
  "wledIp": "192.168.1.50",
  "ledCount": 96,
  "mapOffset": 0,
  "mapReverse": 0
}
```

Everything else (brightness curve, smoothing, HDR handling, bar detection, ...) has a tuned default.
[docs/configuration.md](docs/configuration.md) lists every setting. It also covers the optional
**live control channel**, which lets you tune settings and line the strip up while you watch, without
reinstalling.

## Updating safely

> [!CAUTION]
> Installing over a running copy kills it instantly. If that happens in the middle of a capture call,
> the TV's video scaler can get stuck. The strip then stays dark until the TV is **unplugged for a
> minute**. Nothing is damaged, but it is confusing.

* If you run the [control channel](docs/configuration.md#live-control-channel), send `pause` before
  installing and `run` afterwards. A paused app makes no capture calls, so the install is safe.
* Otherwise: install, then unplug the TV for 60 s and plug it back in. That clears a stuck scaler and
  starts the new version.
* Upgrading from a build older than v5 (package id `io.gh.hermes.TizenAmbilight`)? The package id has
  changed, so uninstall the old app first. Otherwise two copies fight over the strip.

## Troubleshooting

| symptom | likely cause and fix |
|---|---|
| Strip never lights up | `wledIp` not set, or not reachable from the TV. Check that WLED shows *"Live"* / a realtime source. Power-cycle the TV once after installing. |
| Strip was working, now stays dark after an update | A stuck scaler (capture `rc=-2`). Unplug the TV for 60 s. |
| Strip goes dark during some apps | DRM-protected content (`rc=-4`). This is expected. |
| Everything is dim | WLED brightness below 255 (it scales realtime data). For a brighter look, raise `gamma`, not `gain`. |
| Colours appear on the wrong side | `mapOffset` / `mapReverse`. Use the test pattern ([guide](docs/configuration.md#getting-the-led-mapping-right)). |
| Flicker between two looks | Something else also streams to WLED (Hyperion, the WLED app, sync). Only one sender can own the strip. |
| Strip glows WLED's own colour (often orange) | The app is not running, so WLED fell back to its own preset after live mode timed out. In standby the app holds the strip black. Set WLED's boot preset to off, and check that the app started. |

For anything else, set `probeEndpoint`, run `tools/stats-receiver.py`, and look at `latest.json`. It
shows the capture status, frame rate, power state and the last log lines
([diagnostics](docs/development.md#diagnostics-telemetry-and-frame-dumps)).

## FAQ

**Why is there no signed `.tpk` to download?**
Samsung only installs packages signed with a certificate that lists *your* TV's unique ID (DUID), so a
TPK signed for one TV installs on no other. Apps2Samsung solves this by signing on your machine for
your TV. Releases therefore ship unsigned, default-certificate TPKs for it to sign.

**Does it work with HDMI inputs and consoles?**
Untested so far. The video plane carries what the TV decodes for display, so it is plausible, but it
has only been verified with built-in apps. Reports welcome.

**Does it work on Tizen 5 / 2019–2020 TVs?**
Not as built. Those TVs ship an older .NET runtime and expose different capture libraries. See
[Will it work on my TV?](#will-it-work-on-my-tv)

**Does it need the internet or any account?**
No. It talks to your WLED controller and, if you configure them, two optional helpers on your LAN.
Nothing is sent anywhere by default.

**How is this different from HyperTizen or TizenGlow?**
Those sample a few points through a different Samsung library and support many more TVs. This one
reads the whole decoded frame and talks to WLED directly. There is a
[comparison](docs/how-it-works.md#how-this-differs-from-other-tizen-ambilights).

## How it works

```
video plane (960x540, in memory) -> perimeter zones -> HDR decode + gamut map
  -> smoothing + scene-adaptive tone curve -> one WLED DRGB datagram per frame
```

The capture ABI was recovered by static analysis of the TV's own libraries and anchored to return values
measured on the device. [docs/how-it-works.md](docs/how-it-works.md) has the full story: why the video
plane rather than a screenshot, the struct layouts, the colour pipeline, and the safety mechanisms
(standby detection, stale-frame fade, blackout hold).

## Contributing

Issues and merge/pull requests are welcome; see [CONTRIBUTING.md](CONTRIBUTING.md). Development is on
[GitLab](https://gitlab.com/ritwick7/TizenAmbilight), mirrored to
[GitHub](https://github.com/RitwickVerma/TizenAmbilight).

### Device reports

The most useful contribution right now is a report from another TV: model, year, firmware, and what
happened. Open an issue with the **Device report** template. Even "it did nothing" helps.

## Security

Everything here assumes a trusted home network: the WLED protocol, the control channel and the
telemetry endpoint are unauthenticated by design. Read [SECURITY.md](SECURITY.md) before exposing
anything, and use it to report vulnerabilities.

## Credits

The ambilight idea, the DRGB protocol and much hard-won practical detail come from
[Hyperion](https://github.com/hyperion-project/hyperion.ng), [HyperHDR](https://github.com/awawa-dev/HyperHDR)
and [WLED](https://github.com/wled/WLED). The Tizen-side groundwork comes from
[HyperTizen](https://github.com/reisxd/HyperTizen) and [TizenGlow](https://github.com/DuckieOnQuacks/TizenGlow).
[Apps2Samsung](https://github.com/Apps2Samsung/Apps2Samsung) makes sideloading practical.

Built AI-assisted: the code was written with Claude, and the design and every on-device verification
are the author's.

## License

[MIT](LICENSE). Not affiliated with or endorsed by Samsung.
