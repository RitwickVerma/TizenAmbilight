# How it works

This is the engineering write-up behind TizenAmbilight: which capture path it uses and why, how the
colours are computed, and the safety mechanisms around the strip. For setup, see the
[README](../README.md).

```
video plane (960x540 NV12, in memory)
  -> N perimeter zones, per-zone YUV average        (subsampled x3)
  -> colour matrix once per zone                    (BT.2020, PQ code domain when hdr=1)
  -> gamut map BT.2020 -> BT.709                    (soft: desaturate toward luminance)
  -> saturation -> adaptive EMA -> perimeter ring blur -> stale fade
  -> tone map (black-level crush + scene-adaptive gamma)
  -> WLED DRGB over UDP :21324
```

## Contents

- [Why the video plane](#why-the-video-plane)
- [Colour handling](#colour-handling)
- [Zones, sampling and mapping](#zones-sampling-and-mapping)
- [Output](#output)
- [Liveness, standby and safety](#liveness-standby-and-safety)
- [How this differs from other Tizen ambilights](#how-this-differs-from-other-tizen-ambilights)

---

## Why the video plane

The obvious path is Samsung's `dc_request_capture_to_file_sync`. It works and captures real PNGs, but
it returns the **screen/UI layer**. That layer goes static during playback, so an ambilight built on it
goes dark the moment a film starts. It is kept as a non-default fallback backend.

The plane that follows a playing film is the video plane. It is reachable through
`secvideo_api_capture_video_main(w, h, param)` in `libvideo-capture.so.0.1.0`, which fills
**caller-owned buffers** with a Y plane plus interleaved chroma (NV12) at 960×540 and returns `0` on
success. There is no file, no encoder and no decoder anywhere on the path.

Both entry points are reached through `dlopen`/`dlsym` (`libdl.so.2`). That means no link-time
dependency on Samsung libraries and no NuGet packages, and **no `/dev` node is ever opened**.

### Return codes

| rc | meaning |
|---|---|
| `0` | success |
| `-1` | "not ready". Clears by itself; a short burst right after app start is normal. |
| `-2` | scaler failure. The capture subsystem is wedged until the TV is power-cycled (see [Updating safely](../README.md#updating-safely)). |
| `-4` | DRM-protected content. The plane is withheld, so the strip goes dark. |

### The capture parameter struct

The `secvideo_capture_param` layout was taken from Samsung's own decompiled
`Tizen.TV.System.ContentAnalysis.dll`. It was then confirmed on the device by reading back the fields
the library fills in (`ret_y_pitch = 960`, `ret_c_pitch = 960`, `ret_interlaced = 0`):

| off | type | field |
|---|---|---|
| +0x00 | u32 | `uYSize` |
| +0x04 | u32 | `uCSize` |
| +0x08 | int | `ret_width` (out) |
| +0x0c | int | `ret_height` (out) |
| +0x10 | ptr | `pYAddr` (our buffer) |
| +0x14 | ptr | `pCAddr` (our buffer) |
| +0x18 | int | `no_lock_no_copy` |
| +0x1c | u32 | `color_format` |
| +0x20 | u32 | `protected_on` |
| +0x24 | u32 | `capt_3dmod` |
| +0x28 | u32 | `ret_interlaced` (out) |
| +0x2c | u32 | `ret_y_pitch` (out) |
| +0x30 | u32 | `ret_c_pitch` (out) |

The chroma layout was settled on the device: NV12 interleaved, pitch 960, non-interlaced. This was
confirmed two independent ways: by autocorrelation of a dumped plane (period-2 byte alternation), and
by the library's own `ret_c_pitch`.

### Legacy file backend (non-default)

`dc_request_capture_to_file_sync(_dc_capt_req_data*)` from `libdisplay-capture-api.so.0.0` takes a
296-byte request struct:

| off | type | field | value used |
|---|---|---|---|
| +0x00 | u32 | `app_type` (≤6) | 4 |
| +0x04 | u32 | `capture_mode` (≤4) | 0, **the working mode**; 1 and 2 return `rc=1` and write nothing |
| +0x08 | u32 | `compress_type` | 0 = PNG, 1 = JPEG |
| +0x0c | u32 | `jpeg_quality` | 90 |
| +0x10 | char* | `dir_path` (`access(W_OK)` checked) | the app's own data dir |
| +0x14 | char* | `file_name` | any |
| +0x18 | int | `width` (in/out) | requested; the library clamps it (floor 480×270) |
| +0x1c | int | `height` (in/out) | |
| +0x20 | int | `ret_width` (out) | |
| +0x24 | int | `ret_height` (out) | |
| +0x28 | char[256] | `ret_path` (out) | authoritative |

`rc == 0`, a non-empty `ret_path` and a file on disk together mean a usable frame. `rc == 1` with an
empty `ret_path` means there is nothing on screen (standby, or an unchanging UI); it does not mean the
API is broken. Malformed input returns a clean `-1`, so a bad struct cannot crash the TV.

The app decodes those files with its own first-party PNG decoder. A baseline JPEG decoder exists and
is tested, but it is not wired into the loop.

---

## Colour handling

The plane carries **PQ (SMPTE ST 2084) HDR** codes. Decoding them as SDR throws away the entire upper
half of the signal. The rest of the chain follows from that:

* **Matrix in the PQ code domain** (BT.2020 non-constant-luminance, full range), then **tone-map the
  luma** and scale R/G/B by that single factor. Applying the transfer **per channel** widens the gaps
  between channels and *invents* saturation. That was measured at +18% mean saturation, with a visible
  hue shift on exactly the most saturated zones.
* **Gamut.** HDR content uses BT.2020 primaries, while the LEDs are effectively BT.709/sRGB. Feeding
  2020 triples straight through renders them *less* saturated; converting them widened the measured
  chroma spread by 63%. Out-of-gamut colours are soft-mapped (desaturated toward their own luminance by
  the smallest amount that brings them back into gamut) rather than clamped, because clamping would
  crush a channel and distort the hue.
* **Adaptive gamma.** A single static curve cannot serve both a film whose picture sits at
  0.09–0.28 nits and bright game content. The tone curve is driven by the frame's **median** PQ code,
  not the mean, because a lamp or a specular highlight biases a mean badly. It is EMA-smoothed with a
  2 s time constant and quantised, so the lookup table is not rebuilt every frame.
* **Brightness belongs in `gamma`, not `gain`.** `gain > 1` multiplies the top of the range and clips
  it; `gamma` lifts the low and mid range and cannot clip.
* **`saturation`** scales chroma magnitude. That preserves hue and cannot create colour in a neutral
  zone. It is **not** a tint, and it does not repair a wrong transfer function.

Measured accuracy on coloured content: hue error 0.3–2.2°, and an output/input peak ratio of 0.97–1.00
per edge, once each zone's sampling box sits inside the painted band.

---

## Zones, sampling and mapping

Zones run clockwise from the frame's top-left corner, as seen from the front. With the default 73
zones:

```
top 23 (L->R)  ->  right 13 (T->B)  ->  bottom 24 (R->L)  ->  left 13 (B->T)
```

For any other zone count the same proportions are derived from the frame's edge lengths. Each zone
averages a **border band** 8% of the frame dimension thick, measured inward from the edge (77×43 px at
960×540). The image centre is never sampled.

Two details matter more than they look:

* **Every zone on the side edges keeps its own vertical segment.** If each zone instead samples the
  full active height, all the zones on an edge return the *same* colour by construction: 22 of 73 LEDs
  duplicate a neighbour, and it costs 3.7× more.
* **Subsampling stride** (`zoneSample`, default 3) steps through both axes. It cuts the work by 9× at
  1.4% mean error against an every-pixel reference. Zone extraction, not capture, is the frame-rate
  ceiling.

Mapping is **1:1**: zone *i* drives one LED, rotated by `mapOffset` and optionally reversed. LEDs
beyond the zone count are explicitly written `(0,0,0)` every frame. Spreading zones proportionally
across a longer strip accumulates drift and breaks the corner alignment. Unwritten LEDs keep showing
WLED's own segment output, which is why they must be written black rather than skipped.

### Black bars

Pillarbox (column) detection is on by default and finds real 4:3 content. Letterbox (row) detection is
off by default (`barRows`): on the reference TV, the library scales the content to fill the requested
box, so the plane carries no letterbox rows. The row test could only produce false positives there,
and it was observed cropping 25% of the picture. Adopting a crop needs `barHold` stable frames, and
giving it back needs six times as many, so a bright cut inside a letterboxed film does not make the
strip twitch.

---

## Output

WLED realtime **DRGB** over UDP (default port 21324):

```
byte 0     0x02            DRGB
byte 1     timeout seconds
byte 2..   3 bytes/LED     R,G,B per LED
```

One datagram per frame, so each frame is atomic. DRGB caps a datagram at 490 LEDs, and the app caps
`ledCount` to match. Longer strips would need WLED's DNRGB protocol, which is not implemented.

**DRGB is last-packet-wins, so exactly one sender may own the strip.** Two senders produce visible
flicker between them. If you add another sender, have it pause this app through the control channel
while it streams.

All the blank-the-strip paths (no frame, standby, shutdown) go through one shared hold. It **re-sends**
the blackout at most once per second, with a 4 s timeout byte. A single blackout datagram expires, and
WLED then reverts to its own stored colour, which is how an ambilight ends up glowing orange at idle.

---

## Liveness, standby and safety

* **Power state is probed on the TV itself.** The app reads `http://127.0.0.1:8001/api/v2/`, which is
  reachable from inside a sideloaded app's sandbox, and caches the answer for 1 s. In standby the
  capture call is skipped entirely, because a blind loop hammers an API that is known to burst and then
  stop. The probe **fails toward trying**: if it cannot answer, the app assumes the panel is on and
  keeps capturing, so a broken probe can never make it treat an awake TV as off.
* **A stale frame is not a live frame.** A closed or paused video leaves its last decoded frame in the
  plane, so capture keeps succeeding while the picture is frozen. A cheap Y-plane signature detects
  that and fades the strip out. It ramps up fast and down slowly, so resuming never feels laggy.
* **Capture success is not evidence that the pipeline works.** Every safety mechanism reports its own
  counters in telemetry. Several of them exist because a silent failure was indistinguishable from a
  healthy run.
* **Firmware changes are loud.** The capture ABI is unversioned. The app records the platform version
  at every start and logs a warning when it changes, because an OS update can silently change the
  struct layout.
* **Crashes.** A per-frame try/catch and a global unhandled-exception handler black out the strip and
  flush the log instead of dying mid-packet. There is no auto-restart: `auto-restart` is off in the
  manifest.

---

## How this differs from other Tizen ambilights

| | TizenAmbilight | [HyperTizen](https://github.com/reisxd/HyperTizen) | [TizenGlow](https://github.com/DuckieOnQuacks/TizenGlow) |
|---|---|---|---|
| What is read | the full decoded video plane, 960×540 | a handful of sample points (`libvideoenhance`) | 6–14 sample points (`libvideoenhance`) |
| Output | WLED DRGB, direct | Hyperion / HyperHDR | WLED DDP or HyperHDR |
| HDR | PQ decode + BT.2020→709 gamut map | n/a | not documented |
| UI | none (config file, optional control URL) | TizenBrew module | TizenBrew module, calibration screen |
| Tested on | one 2025 TV (Tizen 9) | Tizen 6.5+ | Tizen 6.5+ |

Those projects support far more TVs today. TizenAmbilight trades that reach for reading the whole
picture.
