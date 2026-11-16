# Audio delay: measure it, then cut it — design

Status: approved by the owner 2026-11-16. Extends `docs/design.md` §4.5, §4.9 and must-pass 5.

## 1. Intent

Sound is half of playing, and Framejet has never measured how far it trails the picture. `docs/design.md` §4.5 only estimates 50–100 ms. The owner doesn't think they hear a lag but can't tell by ear, so the first job is to **measure**. The second is to **make the delay Framejet adds as small and steady as possible** without ever making the sound worse.

**Success:**
- The owner can run one check and read "sound leaves the PC N ms after the picture".
- Framejet's own share of that drops from an estimated 35–90 ms that drifts to about 10–20 ms that holds steady, shown by before-and-after numbers.

## 2. Goals, non-goals, anti-goals

Goals
- The self-test reports Framejet's own audio delay, next to the picture's.
- A user-run **A/V sync check** measures the full gap as the sound and the picture leave the PC, including any delay inside the capture card.
- The buffer is held steady by tiny rate changes instead of dropping chunks.
- The capture and output buffers are smaller, with Windows' low-latency shared mode used where the driver supports it.

Non-goals
- **An A/V offset control (owner, via this design).** Sound trails the picture, so lining them up could only mean holding the picture back, which is the lag anti-goal.
- **Exclusive-mode output (owner).** It silences Discord, game chat and system sounds on that output, and it fails when another app already holds the device.
- Measuring a headset's or speaker's own delay. Framejet can't change it. A phone slow-motion check can be written up later as a how-to if wanted.
- Any change to how the video path behaves in normal play. Measurement hooks that are active only while the A/V sync check runs are allowed (5.2).

Anti-goals (the owner's anti-goals, applied to sound)
- **Crackles, dropouts or clicks.** A glitch is worse than a few milliseconds of delay. Every cut in this design must fall back on its own when it causes underruns.
- **Drifting sync.** A delay that wanders over a session is worse than a slightly larger steady one.
- **New failure modes.** Audio that played before must still play: same device rules, same retry behaviour (§4.7).

## 3. How it works today

Chain (`WasapiAudioBackend.Session`):
1. `WasapiCapture(device, eventSync: true, 10 ms)`
2. `CaptureIngest`: convert to float, then `BufferedWaveProvider` (500 ms capacity, `ReadFully`)
3. `LatencyTrimmer`
4. `PlaythroughChain`: `WdlResamplingSampleProvider` (only if the rates differ), channel map, volume
5. `WasapiOut(device, Shared, eventSync: true, 20 ms)`

The trimmer does **not** hold 15 ms:
- The buffer starts at whatever level the first callbacks leave.
- If the card's clock runs fast, the buffer grows until it passes 60 ms, then is cut back to 15 ms in one step. That step can click, and the delay climbs again.
- If the card's clock runs slow, the buffer runs dry and underruns play silence.

Estimated delay Framejet adds:

| Part | Estimate |
|---|---|
| Capture (10 ms buffer, engine period) | 10–20 ms |
| Ring buffer | 15–60 ms, sawtooth |
| Output (20 ms buffer, engine period) | 20–30 ms |
| **Total** | **≈35–90 ms** |

The picture adds 5–8 ms (§8 of `docs/design.md`).

## 4. Approaches considered

| | What | Gain | Trade-off | Decision |
|---|---|---|---|---|
| 1 | Steady buffer by rate control, smaller buffers, shared mode, NAudio as-is | 25–60 ms, no drift, no trim clicks | Small, safe | **Picked** |
| 2 | 1 plus Windows low-latency shared mode (`IAudioClient3`, ~2.7–5 ms periods) | about 10 ms more | Own WASAPI interop for the parts NAudio lacks; many drivers don't support it | **Picked, with automatic fallback to 1** |
| 3 | Exclusive-mode output | Lowest | Silences other apps on that output; fails if the device is held | Rejected (owner) |
| — | A/V offset control | — | Can only delay the picture | Rejected |

The owner asked for "the best option for the best result", so the build is 1 + 2. Order of work: measurement first, then a baseline measurement, then 1, then 2. The doc records the numbers at each step.

## 5. Design

### 5.1 Measuring Framejet's own audio delay (self-test)

The backend reports, per session:
- **Capture side:** the capture stream's latency and device period as Windows reports them, plus the buffer size in use.
- **Ring buffer:** the level's mean, p95 and max over the run, plus underruns and trims (or corrections).
- **Output side:** the output stream's latency and device period, plus the mean queued output (padding).
- **Estimated Framejet audio delay:** the sum of capture + ring mean + output, as a mean and a p95.

The self-test report gains a line beside the existing `audio:` line, for example:

```
audio delay: capture 3.0 + buffer 9.8 (p95 12.1) + output 6.2 = 19.0 ms (p95 21.3); mode=low-latency 2.67 ms; underruns=0
```

Pass/fail: the self-test keeps its current gate (exit 1 only for must-pass 9). Audio numbers are reported, not judged, until the baseline is known. After step 4 of §6, must-pass 5 is reworded and gated (see 5.5).

Automated runs stay at volume 0 or muted (`docs/design.md` §7).

### 5.2 The A/V sync check (user-run)

**Entry point (Claude's call):** a right-click menu item, **"Check A/V sync…"**, shown while video and audio both run. It's a menu item rather than a command-line switch because the owner runs it by hand, on the real setup, with sound on.

**Flow:**
1. A short prompt: "Play an A/V sync test video on the PS5 (a flash with a beep), then press Start." Start / Cancel.
2. Framejet listens for 20 s while the status shows "Checking A/V sync… 12 s".
3. A result dialog shows:
   - "Sound leaves the PC **N ms** after the picture (from K flash-beep pairs, spread ±S ms)."
   - The split: "card: X ms · Framejet audio: Y ms · Framejet picture: Z ms".
4. The result also goes to the log.

**How it measures:**
- **Flash:** a jump in mean frame luma above a threshold, timestamped at frame arrival (the QPC time `BufferCB` already takes).
  - Today's luma sampling (`D3D11Presenter.LumaEvery = 15`, on the render thread, timed at draw) is too coarse for this.
  - While the check runs, and only then, sample a cheap luma mean on **every** frame at arrival.
  - Also turn on `MeasureLatency` so the picture's real arrival → screen time is measured live. A fixed 5–8 ms figure would be about 30 ms wrong on a 60 Hz screen with V-Sync On.
  - Both switch off when the check ends. **(Claude's call.)**
- **Beep:** an onset in the captured audio (envelope crossing a threshold after quiet), timestamped at arrival. Correct for its position inside the capture packet, using the packet's device position and QPC time where available.
- **Pairing:** the check doesn't depend on one particular video **(Claude's call)**.
  - Estimate the flash period from the flashes themselves.
  - Pair each flash with the nearest beep within ± half that period, capped at ±500 ms.
  - If the pairs disagree (spread over a quarter of the period), report "ambiguous — the video's flashes are too close together" instead of a number.
  - Typical sync videos flash about once a second, so 20 s gives plenty of pairs.
- **Results:**
  - card gap = the median of (beep arrival − flash arrival).
  - leaves-the-PC gap = card gap + Framejet audio delay (5.1) − Framejet picture delay (arrival → on screen, from the presenter's statistics or the measured 5–8 ms).

**Accuracy target (Claude's call):** ±5 ms. That's enough to tell 20 ms from 60 ms, and well under the roughly 125 ms where people notice.

**Failure copy (Claude's call):**
- No flashes: "No flashes seen — is the sync video playing?"
- No beeps: "No beeps heard — is the PS5's sound on?"
- Fewer than 3 pairs: "Not enough flash-beep pairs; try a longer run."

**Doesn't touch:** settings, volume, devices or the running streams. Cancel or closing the dialog stops it at once.

### 5.3 Steady buffer by rate control (approach 1)

- Replace the drop-based `LatencyTrimmer` with a **`DriftController`** in Core: pure and unit-tested with fakes.
  - Input: the buffer level after each capture packet.
  - Output: a playback rate ratio, held within **±0.5%** (inaudible).
  - Behaviour: a smoothed level (for example an EMA over about 1 s) steers the rate gently toward the target, with no hunting.
- **Apply the ratio** with NAudio's `WdlResampler.SetRates`. NAudio 2.2.1 exposes this; `WdlResamplingSampleProvider` uses fixed rates. The resampler is now **always** in the chain, even when the rates match.
- **Target level:** start at **10 ms**. If underruns happen, raise the target in steps (for example +5 ms, up to 30 ms) for the rest of that session, and log it.
- **Safety net:** keep a hard trim, but only for a jump (for example the level > 80 ms after a system stall). That's the only time samples are dropped, and it's logged.
- **Start-up:** begin playing once the buffer reaches the target, instead of whatever the first callbacks leave.
- **Output buffer:** `WasapiOut` latency 20 → **10 ms** in normal shared mode.
- Must-pass 5's "card clock slightly fast" case is now handled by rate, so the delay no longer saws.

### 5.4 Windows low-latency shared mode (approach 2)

- Where the endpoint's driver supports it (Windows 10+), initialise **both** the capture and output streams with `IAudioClient3.InitializeSharedAudioStream` at the engine's minimum period. That's typically 128 frames at 48 kHz ≈ 2.67 ms.
- NAudio 2.2.1 has no `IAudioClient3`, so this needs our own interop: a small `LowLatencyCapture` / `LowLatencyRender` in Platform that QI's the `IAudioClient` for `IAudioClient3`. Whether to wrap NAudio's classes or write minimal event-driven loops is the builder's call. The chain from §3 stays the same either way.
- **Fallback, per endpoint and automatic:**
  - If `IAudioClient3` is missing or initialisation fails, use normal shared mode (5.3).
  - If low-latency mode produces underruns beyond a small allowance (for example more than 2 in 10 s), restart that stream in normal mode and log it.
  - The fallback is remembered **per endpoint for the rest of the app run**, so audio restarts and retries don't keep trying a bad driver. It isn't saved to settings, so a driver update gets a fresh try next launch. The record lives in Core, next to `AudioSession`, so it can be tested. **(Claude's call.)**
- **Caveat to record:** while any app holds an endpoint in low-latency mode, Windows runs that endpoint's engine at the small period for every app. That's harmless for playback, but it costs a little CPU.

### 5.5 Pass criteria after the change

Must-pass 5 is reworded once the baseline is known. The proposed wording, with figures to be confirmed by measurement:

> A long session with the card's clock slightly fast or slow keeps Framejet's audio delay steady: its p95 never more than 5 ms above its mean, no trims after start-up, and no underruns in 10 minutes.

Must-pass 10 (new), from the self-test, at volume 0:
- **Framejet audio delay p95 ≤ 25 ms in low-latency mode, and ≤ 35 ms in normal mode.** These are placeholders; tighten them to the measured figures + 20%.

The A/V sync check is never a gate: it needs a person and a PS5.

## 6. Order of work

1. Measurement: 5.1, then 5.2.
2. **Baseline:** run the self-test and the A/V sync check on today's code, and record the figures in this doc.
3. Rate control and smaller buffers (5.3). Measure again.
4. Low-latency shared mode (5.4). Measure again. Then set the must-pass figures (5.5).
5. Update `docs/design.md`:
   - §4.5: the new chain, and the measured numbers replacing the 50–100 ms estimate.
   - §4.9: the report line.
   - §6: must-pass 5 and 10.
   - §8: proven facts, such as which endpoints support low-latency mode.

**Who measures (Claude's call):**
- The builder runs the self-test on this PC's card for every step's numbers, and moves on using those numbers alone.
- The owner runs the A/V sync check twice: once at the baseline (step 2) and once at the end. Neither run blocks the build.
- The must-pass figures (5.5) are set from the self-test numbers.

## 7. Constraints

- C# 9, .NET Framework 4.8, NAudio.Wasapi 2.2.1. No new packages without the third-party checks in `CLAUDE.md`.
- The single-exe rules hold (`CLAUDE.md`, Single exe).
- Threading: audio callbacks run off the UI thread; results reach the UI through the existing dispatcher.
- Never open the capture device twice. The sync check uses the running streams.
- No personal details in code, docs or commits.

## 8. Claude's calls (for the owner to check)

- "Check A/V sync…" is a menu item, not a command-line switch.
- The ±0.5% rate limit, the 10 ms starting target and the underrun step-up rule.
- The hard trim stays only as a stall safety net (> 80 ms).
- The ±5 ms accuracy target for the sync check, and its failure messages.
- Audio pass figures are placeholders until the baseline is measured.
- Measurement hooks on the video path (every-frame luma, `MeasureLatency`) run only during the sync check.
- Sync pairing adapts to the video's flash period.
- The low-latency fallback is remembered per endpoint for the app run.
- The builder's self-test numbers drive the steps; the owner's two sync checks don't block.

## 9. Left to the builder

- Class names and placement, beyond `DriftController` living in Core.
- How the low-latency streams are written (wrapping NAudio or minimal loops).
- The flash and beep detection thresholds and the smoothing constants, tuned on the real card.
- The sync check dialog's visuals (dark theme, matching §4.6).
- How tests simulate clock drift (a fake clock feeding `DriftController`).

## 10. Measurements

Self-test, Release build, the Ripsaw X's HDMI input into the Windows default output (a virtual mixer endpoint), volume 0, median of three runs. The figures are Framejet's own share; a virtual mixer or headset adds its own delay on top.

| Step | Mode | Capture | Buffer mean / p95 | Output | Total mean / p95 | Underruns | A/V sync check |
|---|---|---|---|---|---|---|---|
| Baseline (today's chain, 2026-11-16) | normal, 10 ms period | 10.0 | 10.1 / 10.0 | 12.0 | 32.1 / 32.0 ms | 0 | pending (owner) |

Notes on the baseline:
- The live clients were read in all three runs (log: `capture=live client, output=live client`).
- Over a 4 s run the card's clock drift doesn't show: the buffer sat at its 10 ms packet size with no trims. The 15–60 ms sawtooth the design describes builds over minutes, so a long run is needed to see it.
- The capture stream reports no added latency of its own beyond its 10 ms buffer.
