# TizenAmbilight

A **real-frame ambilight for Samsung Tizen TVs** — implemented as a single headless Tizen .NET
service app that runs *on the TV*.

No capture card. No HDMI splitter. No Hyperion/HyperHDR/HyperTizen hop. The app reads the TV's own
decoded **video plane** out of memory, reduces it to perimeter zone colours, and drives a WLED strip
directly over UDP. One process, one hop, ~20–25 fps.

Developed and running on a 77" Samsung S84F (`QN77S84FAEXZA`, Tizen 9). Target framework `tizen90`.

```
video plane (960x540, in memory)
  -> 73 perimeter zones, per-zone YUV average        (subsampled x3)
  -> colour matrix once per zone                     (BT.2020, PQ code domain when hdr=1)
  -> gamut map BT.2020 -> BT.709                     (soft: desaturate toward luminance)
  -> saturation -> adaptive EMA -> perimeter ring blur -> stale fade
  -> tone map (black-level crush + scene-adaptive gamma)
  -> WLED DRGB over UDP :21324
```

---

## Why the video plane

The obvious path — Samsung's `dc_request_capture_to_file_sync` — works, and captures real PNGs, but
it returns the **screen/UI layer**. That layer goes static during playback, so an ambilight built on
it goes dark the moment a film starts. It is kept here as a non-default fallback backend.

The plane that follows a playing film is the video plane, reachable via
`secvideo_api_capture_video_main(w, h, param)` from `libvideo-capture.so.0.1.0`: it fills
**caller-owned buffers** with a Y plane plus interleaved chroma (NV12), 960×540, returning `0` on
success. No file, no encoder, no decoder anywhere on the path.

Both entry points are reached through `dlopen`/`dlsym` (`libdl.so.2`) — no link-time dependency on
Samsung libraries, no NuGet packages, and **no `/dev` node is ever opened**.

Return codes: `0` success · `-1` "not ready", self-clears (a short burst after app start is normal)
· `-2` scaler failure — wedge, needs a TV power cycle · `-4` DRM-protected content.

### Legacy file backend (non-default)

`dc_request_capture_to_file_sync(_dc_capt_req_data*)` from `libdisplay-capture-api.so.0.0`, request
struct 296 bytes:

| off | type | field | value used |
|---|---|---|---|
| +0x00 | u32 | `app_type` (≤6) | 4 |
| +0x04 | u32 | `capture_mode` (≤4) | 0 — **the working mode**; 1 and 2 return `rc=1` and write nothing |
| +0x08 | u32 | `compress_type` | 0 = PNG, 1 = JPEG |
| +0x0c | u32 | `jpeg_quality` | 90 |
| +0x10 | char* | `dir_path` (`access(W_OK)` checked) | the app's own data dir |
| +0x14 | char* | `file_name` | any |
| +0x18 | int | `width` (in/out) | requested; the library clamps (floors at 480×270) |
| +0x1c | int | `height` (in/out) | |
| +0x20 | int | `ret_width` (OUT) | |
| +0x24 | int | `ret_height` (OUT) | |
| +0x28 | char[256] | `ret_path` (OUT) | authoritative |

`rc == 0` + non-empty `ret_path` + a file on disk ⇒ a usable frame. `rc == 1` with an empty
`ret_path` means there is nothing on screen (standby / an unchanging UI) — not a broken API.
Malformed input returns a clean `-1`, so a bad struct cannot crash the TV.

---

## Colour handling

The plane carries **PQ (SMPTE ST 2084) HDR** codes. Decoding them as SDR throws away the entire upper
half of the signal. The chain that follows from that:

* **Matrix in the PQ code domain** (BT.2020 non-constant-luminance, full range), then **tone-map the
  luma** and scale R/G/B by that single factor. Applying the transfer **per channel** widens the gaps
  between channels and *invents* saturation — measured at +18% mean saturation with a visible hue
  shift on exactly the most saturated zones.
* **Gamut:** HDR content is BT.2020-primed while the LEDs are effectively 709/sRGB. Feeding 2020
  triples straight through renders them *less* saturated. Converting measured **+63% chroma spread**,
  and out-of-gamut colours are soft-mapped (desaturated toward their own luminance by the smallest
  amount that brings them back) rather than clamped, which would crush a channel and distort hue.
* **Adaptive gamma:** a single static curve cannot serve both a film whose picture sits at 0.09–0.28
  nits and bright game content. The tone curve is driven by the frame's **median** PQ code (not the
  mean — a lamp or a specular highlight biases a mean badly), EMA-smoothed with a 2 s tau and
  quantised so the LUT is not rebuilt every frame.
* **Brightness belongs in `gamma`, not `gain`.** `gain > 1` multiplies the top of the range and clips;
  `gamma` lifts the low/mid range and provably cannot clip.
* `saturation` scales chroma magnitude, which preserves hue and cannot create colour in a neutral
  zone. It is **not** a tint, and it is not a repair for a wrong transfer function.

Measured accuracy on coloured content: hue error 0.3–2.2°, output/input peak ratio 0.97–1.00 per edge
once each zone's sampling box sits inside the painted band.

---

## Zone layout, sampling and mapping

73 zones, clockwise from the frame's top-left, matching where the strip's LEDs physically sit:

```
top 23 (L->R)  ->  right 13 (T->B)  ->  bottom 24 (R->L)  ->  left 13 (B->T)
```

Each zone averages a **border band** 8% of the frame dimension thick, inward from the edge (77×43 px
at 960×540); the image centre is never sampled. A zone total other than 73 derives the same
proportions from the frame's edge lengths.

Two things that matter more than they look:

* **Every zone on the side edges keeps its own vertical segment.** Collapsing them (sampling the full
  active height per zone) makes all 13 zones on an edge return the *same* colour by construction —
  22 of 73 LEDs duplicating a neighbour, at 3.7× the cost. It is also 3.7× faster when fixed.
* **Subsampling stride** (`zoneSample`, default 3) steps both axes and cuts the work by `9×` at 1.4%
  mean error against an every-pixel reference. Zone extraction, not capture, is the frame-rate ceiling.

Mapping is **1:1** — 73 zones to LEDs 0–72, with the remaining LEDs written `(0,0,0)` explicitly every
frame. Spreading zones proportionally across a longer strip accumulates drift and breaks corner
alignment. Unwritten LEDs keep showing WLED's own segment output, which is why they must be written
black rather than skipped.

---

## Output

WLED realtime **DRGB** over UDP (default port 21324):

```
byte 0     0x02            DRGB
byte 1     timeout seconds
byte 2..   3 bytes/LED     R,G,B per LED
```

One datagram per frame (≤490 LEDs), so a frame is atomic. The timeout byte is raised to 4 s so a few
lost packets still leave the strip latched.

**DRGB is last-packet-wins: exactly one sender may own the strip.** Two senders produce visible
flicker between them; if you add another, have it drive this app's control channel to `pause` while
it streams.

Blank-the-strip paths (no frame, standby, shutdown) all go through one shared hold that **re-sends**
at most once per second — a single blackout datagram expires and WLED reverts to its own stored
colour, which is how an ambilight ends up glowing orange at idle.

---

## Liveness, standby and safety

* **Power state is probed on the TV itself** (`http://127.0.0.1:8001/api/v2/`, reachable from inside
  a sideloaded app's sandbox), cached 1 s. In standby the capture call is skipped entirely, because a
  blind loop hammers an API that is known to burst-and-stop.
  The probe **fails toward trying**: if it cannot answer, the app assumes the panel is on and keeps
  capturing, so a broken probe can never leave it believing an awake TV is off.
* **A stale frame is not a live frame.** A closed or paused video leaves its last decoded frame in the
  plane, so capture keeps succeeding while the picture is frozen. A cheap Y-plane signature detects
  that and fades the strip out — ramping up fast and down slow, so resuming never feels laggy.
* **Capture success is not evidence the pipeline is working.** Every safety mechanism reports its own
  counters in telemetry; several of them exist specifically because a silent failure was
  indistinguishable from a healthy one.

---

## Live control channel

The app polls `<controlUrl>` every 250 ms and applies a comma-separated body. These are **one-shot
assignments**: a token that is absent keeps its last value for the life of the process. Write an
explicit body containing everything you intend to keep, and read the state back from telemetry rather
than trusting the body.

```
pause | run | test=0/1 | map=N | rev=0/1 | leds=N
alpha= afast= acut= gain= gamma= sat= black= spatial= floor= stale= stalevel=
    punch= hdr=0/1 gamut=0/1 zs=1..8 peak=100..4000 fps=0..120
ag=0/1 gmin= gmax= atau=   bars=0/1 bthr= zhex=0/1 dump=1
backend=0/1 (0 = video plane, 1 = legacy file backend)   shutdown=1
```

**Values are numbers only.** The parser runs a numeric `TryParse` and skips anything that fails, so a
string-valued token is silently dropped before it reaches the switch — it looks implemented and does
nothing. NaN/Infinity are rejected explicitly, because a NaN cast to `int` wraps into a legal-looking
but wrong mapping offset.

`tools/control-server.py` serves this endpoint from a file:

```bash
python3 tools/control-server.py            # GET returns /tmp/amb-control, or "run" if absent
echo "run,test=0,hdr=1,..." > /tmp/amb-control
echo pause > /tmp/amb-control              # see deploying, below
```

---

## Telemetry

Every ~5 s the app POSTs JSON to `<probeEndpoint>/probe`. `tools/stats-receiver.py` collects it:

```bash
python3 tools/stats-receiver.py --port 8791 --out ./ambstats
```

It keeps `latest.json` (newest report verbatim), `history.log`, and the raw planes from a
`dump=1` frame dump. Render one with:

```bash
cat appframe_Y appframe_C > /tmp/f.nv12
ffmpeg -f rawvideo -pix_fmt nv12 -s 960x540 -i /tmp/f.nv12 -y /tmp/f.png
```

Reports carry `decode` (`hdr=… gamut=… zoneSample=… peak=… targetFps=…`), `adaptive`
(`medianCode=… sceneNits=… gammaEff=…`), `tone`, `tuning`, `smoothing`, `mapping`, `bars`, `io`,
`zone_sample_hex`, capture/failure counters and `loop_exceptions`.

Two hard rules are baked into how this is written: **a telemetry key must describe what it actually
holds** (a field named for a file size once reported an in-memory size, and was trusted), and
**anything invisible to the telemetry may as well not exist** — a fix must be verifiable, not
asserted. `log_tail` rides along with every report for that reason.

---

## Preferences

Set on the device; a live token overrides without a rebuild.

| key | default | meaning |
|---|---|---|
| `outputEnabled` | **true** | master switch for the UDP output. **Set false before reusing this app where another ambilight already drives the strip.** |
| `captureBackend` | `video_plane` | `video_plane` or `file` |
| `wledIp` / `wledPort` / `wledTimeout` | `192.168.86.18` / 21324 / 4 | strip target |
| `probeEndpoint` / `statsIntervalSec` | `http://192.168.86.101:8791` / 5 | telemetry target |
| `controlUrl` | `http://192.168.86.101:8792/control` | live control channel |
| `powerStateUrl` | `http://127.0.0.1:8001/api/v2/` | the TV's own power state |
| `zoneCount` / `bandFrac` | 73 / 0.08 | perimeter zones and border band depth |
| `mapOffset` / `mapReverse` / `ledCount` | 58 / 0 / 75 | this strip's wiring |
| `hdr` / `gamut` | 1 / 1 | PQ decode, BT.2020→709 |
| `peak` (`HdrPeakNits`) | 203 | nits that map to full scale (HDR diffuse white) |
| `adaptiveGamma` / `gammaMin` / `gammaMax` / `adaptTau` | 1 / 1.15 / 1.6 / 2.0 | scene-adaptive tone curve |
| `gamma` / `gain` / `black` | 1.5 / 1.0 / 12 | manual fallback curve, and black level |
| `saturation` / `whiteR` / `whiteG` / `whiteB` | 1.0 / 1 / 1 / 1 | chroma scale; per-channel white balance |
| `alpha` / `alphaFast` / `alphaCut` | 0.25 / 0.75 / 18 | adaptive EMA smoothing |
| `spatial` / `punch` / `punchFrames` | 0.50 / 0.15 / 5 | ring blur; cut punch |
| `floor` / `staleMs` / `staleLevel` | 0 / 6000 / 0.35 | ambient floor; stale fade |
| `targetFps` / `zoneSample` | 25 / 3 | rate cap; zone subsample stride |
| `bars` / `barThresh` / `barHold` | 1 / 18 / 8 | letterbox/pillarbox detection — **see limitations** |
| `zoneHexAll` | 0 | report all zones instead of a 12-zone sample |

`alpha` is applied **per frame**, not per unit time, so its effective smoothing changes with fps.
A cap on the frame **rate** is the largest saving available at zero perceptible cost: ambient light
reads as continuous at ~25 fps, and capping removes whole capture calls rather than shaving
arithmetic.

---

## Build

```bash
cd TizenAmbilight
dotnet restore TizenAmbilight.csproj
dotnet msbuild TizenAmbilight.csproj -t:Rebuild -v:m -p:Configuration=Debug -nologo -clp:Summary
```

Requires the .NET SDK **plus the Tizen .NET SDK** — the `Tizen.NET.Sdk` targets and the `tizen90`
reference assemblies come from Tizen Studio, not from NuGet alone. If `dotnet` is not on your `PATH`,
`export DOTNET_ROOT=<sdk dir>` and prepend it.

Produces `bin/Debug/tizen90/TizenAmbilight.dll` and
`bin/Debug/tizen90/io.gh.hermes.TizenAmbilight-1.0.0.tpk`. Build logs the line
`is signed with Default Certificates!` — that build installs on nothing you own; re-sign it.

Target `tizen90`, `Tizen.NET.Sdk/1.1.9`, **zero NuGet dependencies** — the PNG decoder, baseline JPEG
decoder, JSON writer and capture P/Invoke are all first-party code.

Signing is deliberately **not** configured in the project, and **no certificates, keystores or
passwords belong in this repo** (see `.gitignore`). A plain `dotnet build` signs with Tizen's default
certificates, which a retail TV rejects — re-sign with your own profile, then
`tizen install -n <signed.tpk> -t <MODEL>`.

### ⚠️ Never install over a capturing app

`tizen install` SIGKILLs the running app. If it is mid-capture this can leave the scaler allocated and
**wedge the TV's capture subsystem** — every later capture returns `rc=-2` until the TV is power-cycled
(measured 3 times out of 3 deploys). Pause first:

```bash
echo pause > /tmp/amb-control     # control-server.py must be running
# tizen install ...; tizen run ...
echo "run,<your full token body>" > /tmp/amb-control
```

This is why the pause token exists at all.

---

## Tests

```bash
cd decoder-selftest
python3 gen_testdata.py        # PNGs written by hand, one per scanline filter type, with expected RGBA
python3 gen_jpeg_testdata.py   # real Pillow-encoded JPEGs, Pillow's decode kept as the reference
dotnet run --project DecoderSelfTest.csproj
```

The host harness compiles the same decoders the app uses and byte-compares: the PNG decoder is
verified on 4:4:4 / 4:2:2 / 4:2:0 / grayscale / restart-marker / odd-dimension JPEGs against
**libjpeg-turbo** (worst per-channel error 3/255, mean ≤0.13), and both decoders are checked to
**reject** what they do not support (CMYK, progressive, 12-bit, truncation) rather than decode it
wrongly. It also checks the zone layout, the EMA arithmetic, the DRGB packet and the JSON writer.

The same PNG decoder self-checks **on the device**: `PngSelfTest.cs` embeds a known 8×8 PNG plus its
expected decode and reports `PASS`/`FAIL` in the startup report.

---

## Honest limitations

* **The capture ABI is reverse-engineered, against one firmware.** The struct offsets and plane layout
  are not versioned by Samsung and are not documented for third-party apps. The app clamps the
  returned dimensions and pitch and bounds-checks every read, and reports `captureApi=ok|UNAVAILABLE`
  so a changed ABI shows up as a visible state rather than a sheared picture — but a firmware update
  is a real risk, and this was developed and tested on **one** model.
* **The video plane is pre-tone-map.** A faithful mapping of a dark HDR film produces a dark strip;
  that is the source being dark, not a bug. The adaptive gamma is the mitigation.
* **Bar detection ships on but should be off on at least one panel.** The plane carries no letterbox
  rows (the library scales content to fill the requested box), so a row test can only produce false
  positives — observed cropping 25% of the picture and re-firing every few seconds. The column axis
  is a different case and did detect real 4:3 pillarbox columns. Because one flag drives both axes,
  disabling it also disables the genuine detection; they need separate switches.
* **No auto-restart on crash.** The Tizen `on-boot` flag covers boot only; a runtime crash leaves the
  service down until a reboot. Mitigated by a per-frame try/catch and a global handler that blacks the
  strip and flushes the log instead of dying mid-packet.
* **A sticky `pause` is possible** if the control host dies while the body says `pause`. This fails
  safe on purpose: resuming on a timeout would risk installing over a live capture and wedging the
  scaler, which is strictly worse than a dark strip.
* The strip's own brightness ceiling is the strip's, not the app's. On WLED, note that the **master
  brightness does scale realtime data** unless "force max brightness" is enabled — a controller that
  boots to a default of 128 will render the whole ambilight at 50%.

---

## Credits

The ambilight idea, the DRGB protocol and a great deal of hard-won practical detail come from
[Hyperion](https://github.com/hyperion-project/hyperion.ng) and
[HyperHDR](https://github.com/awawa-dev/HyperHDR) (out-of-process, grabber-driven), and from
[HyperTizen](https://github.com/reisxd/HyperTizen) for the Tizen-side approach. Note that a Tizen
compositor capturer sees only the TV's own layers — HDMI inputs and DRM content arrive black, which
is what motivated going after the video plane in-process here.

The capture ABI was recovered by static analysis of the TV's own libraries, anchored to runtime
return values from a sideloaded probe app.

## License

MIT — see [LICENSE](LICENSE).
