# Security Policy

## Reporting a vulnerability

Use GitLab's **confidential issue** on this project (`Issues → New issue → mark as confidential`),
or email **verma1997@gmail.com** (already public in this repo's commit metadata).

Please include the affected file/function, what an attacker gains, and the minimal reproducer.
There is no bounty. Expect an acknowledgement, and a fix or a reasoned "won't fix" — this is a
one-person project with no support contract.

## Threat model — read this before deploying

**This software is built for a trusted LAN and authenticates nothing.** That is a deliberate
trade-off, and it is the single most important thing to understand about it:

| Surface | What it is | Who can reach it |
|---|---|---|
| Capture (`secvideo_api_capture_*`) | runs in-process on the TV | the app only |
| WLED DRGB output | UDP :21324, **no authentication in the protocol** | anyone on the LAN can also send it, and WLED cannot tell senders apart |
| Control channel (`<controlUrl>`) | HTTP GET of a plain-text token body | anyone who can reach the host |
| Telemetry (`<probeEndpoint>/probe`) | unauthenticated HTTP POST of JSON | anyone who can reach the host |
| TV local API (`127.0.0.1:8001`) | polled for power state | the app only (loopback) |

Out of the box the app sends nothing anywhere: `wledIp`, `controlUrl` and `probeEndpoint` are empty
until you set them, and the app never opens a listening socket on the TV.

Consequences you should accept knowingly:

* **Do not expose the control or telemetry ports to an untrusted network** (no port forwarding, no
  DMZ, no public reverse proxy). They are unauthenticated file-read and file-write endpoints.
* The app's control body includes **`shutdown=1`**, which terminates the service. It is intended for
  the operator, not the network.
* DRGB is **last-packet-wins** and carries no authentication, so anyone on the LAN can drive the
  strip, and WLED cannot distinguish your traffic from theirs.
* The TV's own HTTP API is likewise unauthenticated. If a hostile device is on your LAN the TV, the
  strip and this app are all in scope — segment them (separate SSID/VLAN) rather than assuming any
  of this is safe.

If you need authenticated ambilight control, put the control and telemetry endpoints behind your own
reverse proxy with auth, or bind them to a private interface. See
`tools/stats-receiver.py --bind` / `--max-body` / `--allow-name` for the collector's own limits.

## Never commit credentials

This repository must contain **no** signing certificates, keystores, tokens or passwords —
`.gitignore` covers the usual shapes and this is enforced client-side, because GitLab's server-side
secret push protection is not available on the free tier:

```bash
python3 tools/check-secrets.py --staged     # pre-commit
python3 tools/check-secrets.py --all        # whole tree
```

A Tizen signing profile or `.p12` leaked here would let anyone sign an app for a device that trusts
that certificate. Keep them outside the tree entirely.

Your own `TizenAmbilight/config.json` holds LAN addresses and is gitignored for that reason. Release
TPKs ship only the empty template, `config.example.json`.

## Firmware update path

If this app drives a WLED controller, note that **WLED's HTTP API is unauthenticated by default**:
an attacker on the LAN can both replace the OTA password and (with it) flash arbitrary firmware.
The mitigation is `ota.lock` — which makes the OTA/WiFi configuration only accepted from WLED's
secure store rather than from `/json/cfg`. This is a controller-side setting, not something this
app can enforce.

## Supported versions

The latest release and `master`. The capture ABI this depends on is reverse-engineered and
unversioned, so older builds are not supported artifacts: update before reporting.
