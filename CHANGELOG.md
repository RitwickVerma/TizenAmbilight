# Changelog

All notable changes. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and
versions follow the TPK manifest version. `Ambilight.Version` (reported in telemetry) is the build
identifier.

## [Unreleased] — 1.0.0 / v5

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
