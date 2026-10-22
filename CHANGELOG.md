# Changelog

All notable changes. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and
versions follow the TPK manifest version. `Ambilight.Version` (reported in telemetry) is the build
identifier.

## [Unreleased] — 1.0.0 / v6

### Added (v6)
- **DRM fallback.** When the video plane returns `rc=-4` (copy protection) three times in a row, the app
  switches to the TV's own RGB-measure block (`libvideoenhance` `ppi_ve_*`, the interface Samsung's Hue
  Sync support uses) and hands back to the plane on the first `rc=0`. It reads spot colours, never a frame
  of protected video. Measured limits on the one tested TV (S84F, Tizen 9): 2 measure slots, about
  100-116 readings per second, per-spot refresh 12-15 Hz at 8 spots, 8-10 Hz at 12, 4-5 Hz at 24
  (default), about 1.4 Hz at 73 (measured at a 17 ms settle; the shipped default is the TV-reported 20 ms,
  which measured 80-99 readings/s and 3.3-4.1 Hz per spot at 24 spots). The ~25 fps the strip is rendered at is interpolation between those
  updates, not new measurements. Letterbox bars are detected and cropped (`ppibars`). Hand-back to the
  plane is implemented and was seen to trigger once on a pre-release build (a static-scene `rc=0` while a
  streaming app was on screen); the real end of protected playback has **not** been observed; colour fidelity against the plane is
  **unmeasured**. See `docs/how-it-works.md#drm-fallback`.
- Settings `ppiZones` (default 24) and `ppiSettleMs` (default: the TV-reported value); `ppiZones` is also an
  optional field in the Apps2Samsung catalog entry. Control tokens `backend=2`, `pz`, `psleep`, `ppibars`
  and the read-only diagnostics `ppigrid`, `pfresh`. Telemetry gains `backend_active`, `ppi_*` counters and
  a `ppi` block.
- README: an Install section that says plainly why no pre-signed `.tpk` exists (the signing certificate
  embeds one TV's DUID), a Compatibility section (tested on the S84F / Tizen 9 only, what the TV must
  provide, how to check from the log and telemetry) and a "DRM content" section.

### Changed (v6)
- The fallback's slot count and settle time come from `ppi_ve_get_rgb_measure_condition` (clamped), never
  from constants measured on one TV. If `libvideoenhance.so` or a symbol is missing, or the condition is
  implausible, the app stays on the plane, logs why, and makes no further `ppi_ve` call.
- A config with no `wledIp` now reports `IDLE: no wledIp configured` in telemetry (it previously claimed
  datagrams were being sent).
- The release workflow's artifact is named `...-default-cert.tpk` and its notes say it is a default-signed
  build for re-signing, not an installable package.

## v5 (MR !3)

### Added
- `config.json` settings file, bundled into the TPK (`res/config.json`). Copy
  `config.example.json`, edit it, and build. Precedence: app preference > `config.json` > default.
- Apps2Samsung catalog entry (`packaging/apps2samsung/`). Its install form fills in the WLED IP, LED
  count and strip mapping, so no build is needed.
- `peak` is now a settings key (it was token-only), and there is a new `barRows` / `brows` switch for
  letterbox row detection.
- CI on GitLab and GitHub. Tagged releases publish the TPK plus `SHA256SUMS` as a GitHub Release.
- Host tests for zone→LED mapping, the config parser and power-state parsing (79 checks).
- Documentation: a new README, `docs/` (how it works, configuration, development), CONTRIBUTING,
  issue templates.

### Changed
- **Breaking:** the package id is now `dev.ritwickverma.TizenAmbilight` (was
  `io.gh.hermes.TizenAmbilight`). Uninstall the old app first.
- **Breaking:** no built-in WLED, telemetry or control addresses. A fresh install sends nothing until
  `wledIp` is set, and empty endpoints are skipped quietly instead of erroring.
- `ledCount` and `zoneCount` default to each other, and both are capped at 490 (the DRGB limit; was 512).
  `mapOffset` now defaults to 0.
- Letterbox **row** detection is off by default. Pillarbox column detection stays on.
- `Program.cs` is split into focused partial-class files. There is no logic change from the split.

### Fixed
- Startup required the *legacy* capture library even when the default video-plane backend was
  selected and ready. On firmware without `libdisplay-capture-api` the app would have idled forever.
- `ConfigFile.Load` now resets earlier values (found by the new tests).

## v4 — 2026-10

First public source release: video-plane capture, HDR/gamut colour pipeline, adaptive tone curve,
WLED DRGB output, control channel and telemetry. Verified on a 2025 Samsung S84F (Tizen 9).
