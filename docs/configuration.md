# Configuration

TizenAmbilight reads its settings from two places:

1. **`config.json`**, bundled into the TPK at build time (or filled in by the Apps2Samsung install
   form). It is read once, at startup.
2. The **live control channel**, an optional URL the app polls every 250 ms. It changes settings while
   the app runs, without a rebuild or reinstall.

Precedence at startup: Tizen app preference (none are set today) > `config.json` > built-in default.
The startup log and every telemetry report record which config file was loaded (`config_source`).

## config.json

Copy `TizenAmbilight/config.example.json` to `TizenAmbilight/config.json`, edit it, and build.
`config.json` is gitignored, so your LAN addresses never reach git. A minimal file looks like this:

```json
{
  "wledIp": "192.168.1.50",
  "ledCount": 96,
  "mapOffset": 40
}
```

Rules:

* The file is one flat JSON object; values are strings, numbers or booleans.
* `true`/`false`, `1`/`0` and `"1"`/`"0"` all work for on/off settings.
* Keys that start with `_` are ignored, so `"_comment": "..."` works as a comment.
* A malformed file is ignored **as a whole** (not half-applied) and reported as `config_source:
  "error: ..."` in the startup log. Every setting then falls back to its default.

### Getting the LED mapping right

Zones run **clockwise from the screen's top-left corner, as seen from the front**. Two settings place
your strip on that ring:

* **`mapOffset`**: where the strip's **first LED** sits, counted in LEDs clockwise from the top-left
  corner. If the strip starts at the top-left corner, it is `0`. If it starts at the bottom-left
  corner of a 23 + 13 + 24 + 13 layout, it is `23 + 13 + 24 = 60`.
* **`mapReverse`**: `1` if the strip runs counter-clockwise (seen from the front) from its first LED.

To check the result, send `test=1` on the control channel. The strip then shows top = red,
right = green, bottom = blue, left = yellow. Adjust `map=` and `rev=` live until the colours line up,
then copy the values into `config.json`.

### All settings

| key | default | meaning |
|---|---|---|
| **Strip** | | |
| `wledIp` | *(empty)* | WLED controller IPv4 address. **Empty means nothing is sent.** |
| `wledPort` | `21324` | WLED UDP realtime port |
| `wledTimeout` | `1` | seconds WLED stays in live mode after the last frame (DRGB timeout byte) |
| `ledCount` | `73` | LEDs on the strip, max 490 (one DRGB datagram). Set this. |
| `zoneCount` | = `ledCount` | screen zones. Fewer than `ledCount` leaves the extra LEDs black. |
| `mapOffset` / `mapReverse` | `0` / `0` | where the strip starts and which way it runs (see above) |
| `outputEnabled` | `true` | master switch for UDP output |
| **Colour and tone** | | |
| `hdr` | `1` | decode the plane as PQ (HDR). Set `0` only if your TV delivers SDR. |
| `gamut` | `1` | convert BT.2020 to BT.709 for the LEDs |
| `peak` | `203` | nits that map to full scale (100–4000) |
| `adaptiveGamma` | `1` | scene-adaptive tone curve |
| `gammaMin` / `gammaMax` / `adaptTau` | `1.15` / `1.6` / `2.0` | its range and time constant (s) |
| `gamma` | `1.5` | manual curve, used when `adaptiveGamma` is `0`. This is the brightness dial. |
| `gain` | `1.0` | straight multiplier. Above 1 it clips; prefer `gamma`. |
| `black` | `12` | black crush: zone levels at or below this go to 0 |
| `saturation` | `1.0` | chroma scale; preserves hue |
| `whiteR` / `whiteG` / `whiteB` | `1` / `1` / `1` | per-channel white balance |
| **Motion** | | |
| `alpha` | `0.25` | EMA smoothing for small changes (lower = smoother) |
| `alphaFast` / `alphaCut` | `0.75` / `18` | faster smoothing used when the frame delta exceeds `alphaCut` (scene cuts) |
| `spatial` | `0.50` | blend between neighbouring zones |
| `punch` / `punchFrames` | `0.15` / `5` | brief brightness overshoot on a cut |
| `floor` | `0` | minimum zone level. 0 keeps true blacks. Must exceed `black` to have any effect. |
| `staleMs` / `staleLevel` | `6000` / `0.35` | fade to this level after the picture has been frozen this long |
| `targetFps` | `25` | frame-rate cap (0 = uncapped) |
| **Sampling** | | |
| `bandFrac` | `0.08` | depth of the sampled border band, as a fraction of the frame |
| `zoneSample` | `3` | pixel stride inside a zone (1 = every pixel) |
| `bars` | `1` | black-bar detection (pillarbox columns) |
| `barRows` | `0` | also detect letterbox rows (false positives on the reference TV) |
| `barThresh` / `barHold` | `18` / `8` | bar luma threshold; frames before adopting a new crop |
| **Helpers and diagnostics** | | |
| `controlUrl` | *(empty)* | live control channel URL, e.g. `http://192.168.1.10:8792/control` |
| `probeEndpoint` | *(empty)* | telemetry collector base URL, e.g. `http://192.168.1.10:8791` |
| `statsIntervalSec` | `5` | telemetry interval |
| `zoneHexAll` | `0` | report every zone's colour in telemetry, not a 12-zone sample |
| `powerStateUrl` | `http://127.0.0.1:8001/api/v2/` | the TV's own power-state API |
| `captureBackend` | `video_plane` | `video_plane` or `file` (legacy screenshot path) |
| `requestWidth` / `requestHeight` / `compressType` / `jpegQuality` | `640` / `360` / `0` / `90` | legacy file backend only |

`alpha` is applied **per frame**, not per unit of time, so its effective smoothing changes with the
frame rate.

---

## Live control channel

When `controlUrl` is set, the app GETs it every 250 ms and applies the body: comma-separated tokens.
`tools/control-server.py` serves that body from a local file:

```bash
python3 tools/control-server.py --bind 192.168.1.10      # this computer's LAN address
echo "run,test=1" > /tmp/amb-control                       # show the edge test pattern
echo "run,map=40,rev=0,test=0" > /tmp/amb-control
echo pause > /tmp/amb-control                              # stop all capture calls
```

**Tokens are one-shot assignments.** A token that is absent keeps its last value for the life of the
process. Write a body containing everything you want to keep, and read the state back from telemetry
rather than trusting the body.

**Values are numbers only.** The parser runs a numeric `TryParse` and skips anything that fails, so a
string-valued token is dropped silently. NaN and Infinity are rejected explicitly.

| token | range | effect |
|---|---|---|
| `pause` / `run` | | stop / resume making capture calls (the app stays alive) |
| `test` | 0/1 | four-edge colour test instead of the picture |
| `map` / `rev` / `leds` | −4096–4096 / 0/1 / 4–490 | `mapOffset` / `mapReverse` / `ledCount` |
| `alpha` / `afast` / `acut` | 0.02–1 / 0.02–1 / 0–200 | smoothing |
| `gamma` / `gain` / `black` | 1–4 / 0.1–8 / 0–200 | tone curve |
| `ag` / `gmin` / `gmax` / `atau` | 0/1 / 0.5–3 / 0.5–4 / 0.2–60 | adaptive gamma |
| `sat` / `hdr` / `gamut` / `peak` | 0–4 / 0/1 / 0/1 / 100–4000 | colour |
| `spatial` / `punch` / `floor` | 0–1 / 0–1 / 0–255 | ambience |
| `stale` / `stalevel` | 0–600000 ms / 0–1 | stale-picture fade |
| `bars` / `brows` / `bthr` | 0/1 / 0/1 / 4–80 | black-bar detection |
| `zs` / `fps` | 1–8 / 0–120 | zone stride, frame cap |
| `cw` / `ch` | 64–960 / 64–540 | video-plane request size |
| `backend` | 0/1 | 0 = video plane, 1 = legacy file backend |
| `zhex` | 0/1 | report every zone in telemetry |
| `dump` | 1 | send the next frame's raw planes to the collector |
| `shutdown` | 1 | run the shutdown path (blackout, log flush) and exit |

A sticky `pause` is possible: if the control host dies while the body says `pause`, the app stays
paused. That is deliberate. Resuming on a timeout could start a capture while an install is about to
kill the app, and that wedges the scaler, which is worse than a dark strip.

See [SECURITY.md](../SECURITY.md) before exposing either helper: both are unauthenticated by design.
