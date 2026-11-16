# Audio Delay Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Measure how far Framejet's sound trails its picture, then make the audio delay Framejet adds small and steady (rate-controlled buffer, smaller buffers, Windows low-latency shared mode with automatic fallback).

**Architecture:**
- Pure decision logic goes in Framejet.Core and is unit-tested with fakes:
  - `AudioDelay` (the delay sum and the report line)
  - `SyncAnalyzer` (flash and beep pairing)
  - `SyncCheck` (the check's countdown and flow)
  - `DriftController` (the playback rate)
  - `LowLatencyRegistry` (the fallback memory)
- Framejet.Platform gains measurement taps, a variable-rate resampler and `IAudioClient3` streams.
- The WinForms shell gains the "Check A/V sync…" menu item and dialog.
- Self-test numbers gate each step.

**Tech Stack:** C# 9, .NET Framework 4.8, NAudio.Wasapi/NAudio.Core 2.2.1 (`WdlResampler.SetRates`), own COM interop for `IAudioClient3`, xUnit 2.4.2 + Xunit.SkippableFact.

**Spec:** `docs/superpowers/specs/2026-11-16-audio-delay-design.md` (read it first). The base behaviour spec is `docs/design.md`.

## Global Constraints

- No new NuGet packages. The single-exe rules in `CLAUDE.md` hold: keep Core, Platform and third-party types out of `Program`'s fields and `Main`.
- Automated audio tests and the self-test run at volume 0 or muted. Never run the Platform suite, the self-test and the app at the same time.
- No A/V offset control and no exclusive-mode output.
- Normal-play video behaviour is unchanged. Video measurement hooks are active only while the sync check runs.
- Rate correction is limited to **±0.5%**. The starting target buffer is **10 ms**. On underruns it rises in **+5 ms** steps, to at most **30 ms**, for that session. The hard trim fires only above **80 ms**.
- Low-latency fallback happens at **more than 2 underruns in 10 s**. It is remembered per endpoint ID for the app run and never saved to settings.
- The sync check listens for **20 s**. Pairs are matched within ± half the flash period, capped at **±500 ms**. Fewer than **3** pairs is a failure. A spread over a quarter of the period is "ambiguous".
- `WasapiOut` normal-mode latency is **10 ms** (was 20).
- UI copy, verbatim (it goes in `Ui/UiText`):
  - Menu: `Check A/V sync…`
  - Prompt: `Play an A/V sync test video on the PS5 (a flash with a beep), then press Start.`
  - Status: `Checking A/V sync… {n} s`
  - Result: `Sound leaves the PC {N} ms after the picture (from {K} flash-beep pairs, spread ±{S} ms).`
  - Split: `card: {X} ms · Framejet audio: {Y} ms · Framejet picture: {Z} ms`
  - Failures: `No flashes seen — is the sync video playing?`, `No beeps heard — is the PS5's sound on?`, `Not enough flash-beep pairs; try a longer run.`, `Ambiguous — the video's flashes are too close together.`
- Commits are GPG-signed, with messages passed via `git commit -F <file>`. They end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. No personal details anywhere.
- Build: `dotnet build Framejet.sln -c Debug` with 0 warnings. Core tests: `dotnet test tests\Framejet.Core.Tests`.

## Review Focus

1. **Output mix format differs from the capture format** (for example 48 kHz stereo in, 96 kHz 8-channel out). The variable-rate resampler and channel map must still produce correct-length, correctly mapped audio. Test in Task 7.
2. **The card or audio stops mid sync check** (unplug, audio failure, Rescan). The check must end with a failure message, not hang or throw. Test in Task 4.
3. **No capture packets for a while** (PS5 asleep, card delivering nothing). Underruns must not raise the target past 30 ms, or trigger a low-latency fallback through no fault of the driver. Tests in Task 6 and Task 9.
4. **The endpoint doesn't support `IAudioClient3`** (Bluetooth, virtual, older drivers). Framejet starts in normal mode with no error shown. Test in Task 10.
5. **A system stall leaves the buffer above 80 ms.** One hard trim brings it back to the target, and it's logged. Test in Task 7.

---

### Task 1: Audio delay figure in Core

**Files:**
- Create: `src/Framejet.Core/Audio/AudioDelay.cs`
- Test: `tests/Framejet.Core.Tests/Audio/AudioDelayTests.cs`

**Interfaces:**
- Produces:
  - `public sealed class AudioDelay { double CaptureMs; double BufferMeanMs; double BufferP95Ms; double OutputMs; string Mode; double PeriodMs; int Underruns; double TotalMeanMs => CaptureMs + BufferMeanMs + OutputMs; double TotalP95Ms => CaptureMs + BufferP95Ms + OutputMs; string ReportLine(); }` (settable properties).
  - `Mode` is `"normal"` or `"low-latency"`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Totals_sum_the_parts()
{ var d = new AudioDelay { CaptureMs = 3, BufferMeanMs = 9.8, BufferP95Ms = 12.1, OutputMs = 6.2 };
  Assert.Equal(19.0, d.TotalMeanMs, 1); Assert.Equal(21.3, d.TotalP95Ms, 1); }

[Fact] public void Report_line_matches_the_spec_shape()
{ var d = new AudioDelay { CaptureMs = 3, BufferMeanMs = 9.8, BufferP95Ms = 12.1, OutputMs = 6.2, Mode = "low-latency", PeriodMs = 2.667, Underruns = 0 };
  Assert.Equal("audio delay: capture 3.0 + buffer 9.8 (p95 12.1) + output 6.2 = 19.0 ms (p95 21.3); mode=low-latency 2.67 ms; underruns=0", d.ReportLine()); }
```

- [ ] **Step 2:** Run `dotnet test tests\Framejet.Core.Tests --filter "FullyQualifiedName~AudioDelayTests"`. Expected: FAIL (type missing).
- [ ] **Step 3:** Implement `AudioDelay`. Format with the invariant culture.
- [ ] **Step 4:** Run the same command. Expected: PASS.
- [ ] **Step 5:** Commit: `feat: add the audio delay figure and its report line`.

### Task 2: Backend reports its audio delay; the self-test prints it

**Files:**
- Modify: `src/Framejet.Platform/Audio/AudioStats.cs` (add `AudioDelay Delay`)
- Modify: `src/Framejet.Platform/Audio/CaptureIngest.cs` (keep buffer-level samples for mean/p95)
- Modify: `src/Framejet.Platform/Audio/WasapiAudioBackend.cs` (`Session` reads latencies, counts underruns)
- Create: `src/Framejet.Platform/Audio/UnderrunCountingProvider.cs`
- Modify: `src/Framejet/SelfTestRunner.cs:254` (print `audio.Delay.ReportLine()` after the `audio:` line, or `audio delay: none` when no audio ran)
- Test: `tests/Framejet.Platform.Tests/Audio/WasapiAudioBackendTests.cs`, `tests/Framejet.Platform.Tests/Audio/CaptureIngestTests.cs`

**Interfaces:**
- Consumes: `AudioDelay` (Task 1).
- Produces:
  - `AudioStats.Delay` (never null; an empty `AudioDelay` when stopped).
  - `internal sealed class UnderrunCountingProvider : IWaveProvider`: wraps the ring buffer and counts reads where `BufferedBytes < count` while running (`int Underruns`).
- Sources for each part:
  - **Capture:** `AudioClient.StreamLatency` plus the capture buffer duration.
  - **Output:** `AudioClient.StreamLatency` plus the mean of `CurrentPadding` sampled on each output read, converted to ms.
  - **Buffer:** the level after each capture packet, as `TimingStats.From`.
  - **Period:** `DefaultDevicePeriod`.

- [ ] **Step 1:** Write the failing test `CaptureIngestTests.Buffer_level_stats_cover_every_packet`. Feed 100 packets that leave the buffer at 10 ms, then 12 ms alternately. Assert `Snapshot(false).Delay.BufferMeanMs` ≈ 11 (±0.5) and `BufferP95Ms` ≈ 12.
- [ ] **Step 2:** Write the failing hardware test `Plays_through_at_gain_zero` additions. Assert `s.Delay.CaptureMs > 0`, `s.Delay.OutputMs > 0`, `s.Delay.Mode == "normal"`, and `s.Delay.TotalMeanMs < 200`.
- [ ] **Step 3:** Run `dotnet test tests\Framejet.Platform.Tests --filter "FullyQualifiedName~CaptureIngestTests|FullyQualifiedName~WasapiAudioBackendTests"`. Expected: FAIL.
- [ ] **Step 4:** Implement it. Don't reset buffer samples on `Snapshot(reset:true)` beyond what the existing stats reset does: reset them together.
- [ ] **Step 5:** Run the tests. Expected: PASS. Build with 0 warnings.
- [ ] **Step 6:** Commit: `feat: measure and report Framejet's own audio delay`.

### Task 3: Sync analysis in Core

**Files:**
- Create: `src/Framejet.Core/Audio/SyncAnalyzer.cs`
- Test: `tests/Framejet.Core.Tests/Audio/SyncAnalyzerTests.cs`

**Interfaces:**
- Produces:
  - `public enum SyncOutcome { Ok, NoFlashes, NoBeeps, TooFewPairs, Ambiguous }`
  - `public sealed class SyncResult { SyncOutcome Outcome; double CardGapMs; double SpreadMs; int Pairs; double PeriodMs; }`
  - `public static class SyncAnalyzer { static SyncResult Analyze(IReadOnlyList<(long Ticks, double Luma)> frames, IReadOnlyList<(long Ticks, float Peak)> audioBlocks, long ticksPerSecond); }`
  - `frames` holds one entry per arrived video frame. `audioBlocks` holds one entry per 1 ms of captured audio, stamped at the block's capture time.

The algorithm, which the tests pin:
- **Flash onsets:**
  - The luma range (max − min over the run) must be ≥ 30, otherwise `NoFlashes`.
  - A flash onset is an upward crossing of (min + max) / 2.
  - Ignore crossings within 100 ms of the previous onset.
- **Beep onsets:**
  - The floor is the 20th-percentile peak.
  - The threshold is max(0.05, 8 × floor).
  - An onset is the first block above the threshold after ≥ 50 ms below it.
  - None means `NoBeeps`.
- **Period:** the median interval between flash onsets. With one flash, use 1000 ms.
- **Pairing:** each flash with the nearest beep within ± min(period / 2, 500 ms). The gap is beep − flash.
- **Result:**
  - Fewer than 3 pairs: `TooFewPairs`.
  - `CardGapMs` is the median gap.
  - `SpreadMs` is (max − min) / 2 of the gaps.
  - A spread over period / 4 is `Ambiguous`.

- [ ] **Step 1:** Write the failing tests, using a synthetic 60 fps luma series (16 dark, 235 on flash frames) and 1 ms audio blocks:
  - `Finds_a_steady_40ms_gap`: flashes every 1000 ms over 20 s, beeps 40 ms later. Expect `Ok`, `CardGapMs` = 40 ±2, `Pairs` ≥ 18.
  - `Negative_gap_when_sound_leads`: beep 30 ms before the flash. Expect −30 ±2.
  - `No_flashes_on_flat_video`: expect `NoFlashes`.
  - `No_beeps_in_silence`: expect `NoBeeps`.
  - `Too_few_pairs_with_two_flashes`: expect `TooFewPairs`.
  - `Fast_flashes_with_mismatched_beeps_are_ambiguous`: flashes every 200 ms, beeps at random offsets within ±100 ms from a fixed seed. Expect `Ambiguous`.
- [ ] **Step 2:** Run `dotnet test tests\Framejet.Core.Tests --filter "FullyQualifiedName~SyncAnalyzerTests"`. Expected: FAIL.
- [ ] **Step 3:** Implement `SyncAnalyzer` as specified above.
- [ ] **Step 4:** Run the tests. Expected: PASS.
- [ ] **Step 5:** Commit: `feat: pair flashes with beeps to measure the card's A/V gap`.

### Task 4: Sync check flow in Core

**Files:**
- Create: `src/Framejet.Core/Audio/SyncCheck.cs`
- Modify: `src/Framejet.Core/Ui/UiText.cs` (all sync-check copy from Global Constraints)
- Test: `tests/Framejet.Core.Tests/Audio/SyncCheckTests.cs`

**Interfaces:**
- Consumes: `SyncAnalyzer`, `SyncResult` (Task 3), `IScheduler`, `FakeScheduler`.
- Produces:
  - `public interface ISyncTaps { void Begin(); (IReadOnlyList<(long Ticks, double Luma)> Frames, IReadOnlyList<(long Ticks, float Peak)> Audio, double AudioDelayMs, double PictureDelayMs) End(); }`. `Begin` turns on the every-frame luma tap, the audio block tap and `MeasureLatency`. `End` turns them off and returns what was gathered.
  - `public sealed class SyncCheck`:
    - constructor: `(ISyncTaps taps, IScheduler scheduler, long ticksPerSecond)`
    - `static readonly TimeSpan Duration = 20 s`
    - `void Start()`
    - `void Cancel()`
    - `void SourceStopped()`: video or audio stopped mid-run. It ends with the message `UiText.SyncSourceStopped` = `"The check stopped: the picture or sound went away."` (Claude's call; add it to the spec's copy).
    - `event Action<int> SecondsLeft`
    - `event Action<string, string> Finished`: the headline and the split line. The split is null on failure.
  - `UiText` methods: `SyncChecking(int seconds)`, `SyncResult(int ms, int pairs, int spread)`, `SyncSplit(int card, int audio, int picture)`, plus the failure constants.
- Leaves-the-PC = `CardGapMs + AudioDelayMs − PictureDelayMs`, rounded to whole ms.

- [ ] **Step 1:** Write the failing tests:
  - `Counts_down_each_second_then_reports`: a fake taps returning Task 3's 40 ms series with audio delay 20 and picture delay 6. Advance the `FakeScheduler` 20 s. Assert `SecondsLeft` went 20…1, and the headline equals `"Sound leaves the PC 54 ms after the picture (from 20 flash-beep pairs, spread ±0 ms)."` (use the actual pair count the series yields). Assert the split is `"card: 40 ms · Framejet audio: 20 ms · Framejet picture: 6 ms"`.
  - `Cancel_turns_taps_off_and_reports_nothing`.
  - `Source_stopped_mid_run_ends_with_message` (Review Focus 2): `End` is called once, and `Finished` gets `UiText.SyncSourceStopped`.
  - `Failure_outcomes_map_to_copy`: one case per `SyncOutcome` other than `Ok`.
- [ ] **Step 2:** Run the `SyncCheckTests` filter. Expected: FAIL.
- [ ] **Step 3:** Implement `SyncCheck` and the `UiText` entries.
- [ ] **Step 4:** Run all Core tests. Expected: PASS.
- [ ] **Step 5:** Commit: `feat: run the A/V sync check as a 20 s countdown`.

### Task 5: Taps in Platform, and the menu item and dialog in the shell

**Files:**
- Modify: `src/Framejet.Platform/Video/GrabberCallback.cs` (an optional per-frame luma tap at arrival)
- Modify: `src/Framejet.Platform/Video/DirectShowVideoBackend.cs` (expose `Action<long,double> ArrivalLumaTap { set; }`)
- Modify: `src/Framejet.Platform/Audio/CaptureIngest.cs` and `WasapiAudioBackend.cs` (expose `Action<long,float> BlockPeakTap { set; }` on the backend)
- Create: `src/Framejet/SyncTaps.cs` (`ISyncTaps` over the two backends: `MeasureLatency` on in `Begin`; in `End`, picture delay = `ArrivalToPresent.MeanMs + PresentToScreen.MeanMs` from `GetStats()`, and audio delay = `GetStats(false).Delay.TotalMeanMs`)
- Create: `src/Framejet/SyncCheckDialog.cs` (dark-theme modal: the prompt, Start/Cancel, the countdown, the result plus split, Close)
- Modify: `src/Framejet/VideoMenu.cs` (the `Check A/V sync…` item after "Rescan devices", enabled only while video and audio both run)
- Modify: `src/Framejet/MainForm.cs` (open the dialog; call `SyncCheck.SourceStopped()` from the video-lost and audio-failed paths; log the result)
- Test: `tests/Framejet.Platform.Tests/Video/` (a new hardware test), `tests/Framejet.Platform.Tests/Audio/WasapiAudioBackendTests.cs`

The tap details:
- **Luma tap:** while set, `BufferCB` computes `LumaSampler.Sample`-equivalent mean luma straight from the `IntPtr`, with the same `Step` and format offsets, after `Publish`, so the frame isn't delayed. It's stamped with the same timestamp passed to `Publish`. When unset it costs one null check.
- **Audio tap:** while set, `CaptureIngest.Process` splits each packet into 1 ms blocks and calls the tap with each block's peak. The stamp is `packetArrivalTicks − (samplesAfterBlockStart / sampleRate) × Stopwatch.Frequency`. Take `packetArrivalTicks` with `Stopwatch.GetTimestamp()` at callback entry.

- [ ] **Step 1:** Write the failing hardware test `Luma_tap_sees_every_frame`. Use the webcam from `hardware.local.ini` for 2 s with the tap set. Assert the tap count is within 2 of `FramesArrived`, and the stamps are strictly increasing.
- [ ] **Step 2:** Write the failing hardware test `Block_tap_reports_1ms_blocks` (microphone at gain 0). Over 2 s, assert the call count is about 2000 (±10%), and the stamps are non-decreasing and within 50 ms of now.
- [ ] **Step 3:** Run both. Expected: FAIL.
- [ ] **Step 4:** Implement the taps.
- [ ] **Step 5:** Run both. Expected: PASS.
- [ ] **Step 6:** Implement `SyncTaps`, `SyncCheckDialog`, the menu item and the `MainForm` wiring.
- [ ] **Step 7:** Check by hand: launch the Debug build with the card, open the menu, run the check with no sync video, and see `No flashes seen — is the sync video playing?`. Unplug mid-check and see the source-stopped message.
- [ ] **Step 8:** Build with 0 warnings and run all Core tests. Commit: `feat: add the Check A/V sync menu item`.

### Task 6: Baseline measurement

**Files:**
- Modify: `docs/superpowers/specs/2026-11-16-audio-delay-design.md` (add a `## 10. Measurements` table: step, mode, capture, buffer mean/p95, output, total mean/p95, underruns, A/V sync result)

- [ ] **Step 1:** Close Framejet. Build Release, then run `Framejet.exe --selftest base.txt --settings <scratch>\settings.ini --seconds 4` three times. Expected: exit 0 and an `audio delay:` line in each.
- [ ] **Step 2:** Record the median of the three runs in the table as "baseline (today's chain)".
- [ ] **Step 3:** Ask the owner to run "Check A/V sync…" with a PS5 sync video and send the result. Record it when it arrives. Don't block on it.
- [ ] **Step 4:** Commit: `docs: record the audio delay baseline`.

### Task 7: Drift controller in Core

**Files:**
- Create: `src/Framejet.Core/Audio/DriftController.cs`
- Delete: `src/Framejet.Core/Audio/LatencyTrimmer.cs`, `tests/Framejet.Core.Tests/Audio/LatencyTrimmerTests.cs` (after Task 8 stops using it; delete in Task 8's commit)
- Test: `tests/Framejet.Core.Tests/Audio/DriftControllerTests.cs`

**Interfaces:**
- Produces: `public sealed class DriftController`:
  - constants: `static readonly TimeSpan StartTarget = 10 ms, Step = 5 ms, MaxTarget = 30 ms, HardTrimAbove = 80 ms`; `const double MaxCorrection = 0.005`
  - `TimeSpan Target { get; }`
  - `double Ratio { get; }`: >1 consumes faster.
  - `double Update(double levelMs, double elapsedMs)`: returns `Ratio`.
  - `int BytesToTrim(int bufferedBytes, int bytesPerSecond, int blockAlign)`: non-zero only above `HardTrimAbove`, and trims to `Target`.
  - `void Underrun()`: raises `Target` by `Step`, capped at `MaxTarget`.
  - `int Trims { get; }`

The control law (pinned by the tests):

```
ema += (levelMs - ema) * min(1, elapsedMs / 1000)        // ~1 s smoothing; ema starts at the first level
Ratio = 1 + clamp((ema - TargetMs) / 20.0 * MaxCorrection, -MaxCorrection, +MaxCorrection)
```

- [ ] **Step 1:** Write the failing tests:
  - `Holds_ratio_at_one_on_target`
  - `Speeds_up_when_buffer_is_above_target`: level 30 ms, ratio 1.005 after 5 s of 10 ms updates.
  - `Correction_never_exceeds_half_a_percent`: level 500 → 1.005, level 0 → 0.995.
  - `Simulated_fast_card_settles_near_target`: plant a +0.1% card clock. Consume at `Ratio`, produce at 1.001, and integrate the level for 10 min of 10 ms steps. Assert the final level is within 5 ms of the target, the p95 − mean ≤ 5 ms after the first 10 s, and `Trims == 0`.
  - `Simulated_slow_card_settles_near_target`: the same at −0.1%.
  - `Underruns_raise_target_in_steps_up_to_30ms` (Review Focus 3): call `Underrun()` 10 times, target = 30.
  - `Hard_trim_only_above_80ms` (Review Focus 5): 79 ms → 0; 81 ms → trims to the target, block-aligned, and `Trims == 1`.
- [ ] **Step 2:** Run the `DriftControllerTests` filter. Expected: FAIL.
- [ ] **Step 3:** Implement `DriftController`.
- [ ] **Step 4:** Run the tests. Expected: PASS.
- [ ] **Step 5:** Commit: `feat: add a drift controller that steers playback rate`.

### Task 8: Rate-controlled chain, prefill and smaller output buffer

**Files:**
- Create: `src/Framejet.Platform/Audio/VariableRateResampler.cs` (an `ISampleProvider` over NAudio `WdlResampler`; `double Ratio { set; }` calls `SetRates(inRate * ratio, outRate)`)
- Modify: `src/Framejet.Platform/Audio/PlaythroughChain.cs` (`Build` always inserts `VariableRateResampler` and returns it via an `out` parameter, replacing `WdlResamplingSampleProvider`)
- Modify: `src/Framejet.Platform/Audio/CaptureIngest.cs`:
  - use `DriftController` instead of `LatencyTrimmer`: `Update` on each packet, `BytesToTrim`, log a trim.
  - prefill: the ring reads silence until the level first reaches `Target`.
- Modify: `src/Framejet.Platform/Audio/WasapiAudioBackend.cs`:
  - `WasapiOut` latency 20 → 10.
  - wire `UnderrunCountingProvider` → `DriftController.Underrun()`.
  - push `Ratio` to the resampler from the capture thread, with a volatile field read on the output thread.
- Delete: `LatencyTrimmer.cs` and its tests.
- Modify: `docs/design.md` §4.5 (the chain and drift control; keep the measured numbers for Task 12)
- Test: `tests/Framejet.Platform.Tests/Audio/PlaythroughChainTests.cs`, `CaptureIngestTests.cs`, `WasapiAudioBackendTests.cs`

- [ ] **Step 1:** Write the failing tests:
  - `PlaythroughChainTests.Variable_resampler_changes_consumption` (Review Focus 1): 48 kHz stereo in, 96 kHz 8-channel out. At ratio 1.005, reading 1 s of output consumes 1.005 s ±0.1% of input. Channels 0/1 carry L/R and channels 2–7 are silent.
  - `CaptureIngestTests.Prefill_holds_silence_until_target`.
  - `CaptureIngestTests.Stall_is_trimmed_once` (Review Focus 5): add 120 ms at once. One trim, and the level ends at 10 ms.
  - Hardware: `Plays_through_at_gain_zero` now asserts `s.Delay.BufferP95Ms <= 25` and `s.Trims == 0` over 5 s.
- [ ] **Step 2:** Run them. Expected: FAIL.
- [ ] **Step 3:** Implement it.
- [ ] **Step 4:** Run the Core and Platform audio tests. Expected: PASS. Build with 0 warnings.
- [ ] **Step 5:** Commit: `feat: hold the audio buffer steady by rate instead of trimming`.

### Task 9: Measure after approach 1

- [ ] **Step 1:** Repeat Task 6 step 1. Expected: exit 0, buffer p95 ≤ 25 ms, underruns 0.
- [ ] **Step 2:** Record the row "rate control + 10 ms output". If underruns > 0, note the target the session settled at.
- [ ] **Step 3:** Commit: `docs: record audio delay after rate control`.

### Task 10: Low-latency fallback memory in Core

**Files:**
- Create: `src/Framejet.Core/Audio/LowLatencyRegistry.cs`
- Test: `tests/Framejet.Core.Tests/Audio/LowLatencyRegistryTests.cs`

**Interfaces:**
- Produces: `public sealed class LowLatencyRegistry`:
  - `bool ShouldTry(string endpointId)`
  - `void Unsupported(string endpointId)`: initialisation failed or no `IAudioClient3`.
  - `bool Underrun(string endpointId, TimeSpan now)`: true when this underrun makes more than 2 within 10 s, and the endpoint is then marked fallen back.
  - `event Action<string> FellBack`
- One instance is owned by `MainForm` and passed to `WasapiAudioBackend`'s constructor. Underruns while the capture side delivers no packets (no packet in the last 100 ms) must not be passed in (Review Focus 3, enforced in Task 11).

- [ ] **Step 1:** Write the failing tests:
  - `Tries_unknown_endpoints`
  - `Unsupported_endpoint_is_not_tried_again` (Review Focus 4)
  - `Two_underruns_in_10s_are_allowed`
  - `Third_underrun_within_10s_falls_back_once`
  - `Underruns_spread_over_time_never_fall_back` (one every 6 s for a minute)
  - `Fallback_is_per_endpoint`
- [ ] **Step 2:** Run the `LowLatencyRegistryTests` filter. Expected: FAIL.
- [ ] **Step 3:** Implement it.
- [ ] **Step 4:** Run the tests. Expected: PASS.
- [ ] **Step 5:** Commit: `feat: remember which endpoints can't run low-latency audio`.

### Task 11: Low-latency shared streams

**Files:**
- Create: `src/Framejet.Platform/Audio/Interop/AudioClient3.cs`. COM declarations:
  - `IMMDeviceEnumerator` (`GetDevice`) and `IMMDevice` (`Activate`)
  - `IAudioClient3` (IID `7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42`), declaring every `IAudioClient` and `IAudioClient2` method in vtable order before `GetSharedModeEnginePeriod`, `GetCurrentSharedModeEnginePeriod` and `InitializeSharedAudioStream`
  - `IAudioCaptureClient` and `IAudioRenderClient`
- Create: `src/Framejet.Platform/Audio/LowLatencyCapture.cs`:
  - `static LowLatencyCapture TryOpen(string endpointId, out string whyNot)`
  - `WaveFormat WaveFormat`, `double PeriodMs`, `double StreamLatencyMs`
  - `event Action<byte[], int> DataAvailable`, `event Action<Exception> Stopped`
  - `Start()`, `Dispose()`
  - an event-driven loop on its own AboveNormal thread
- Create: `src/Framejet.Platform/Audio/LowLatencyRender.cs`:
  - `static LowLatencyRender TryOpen(string endpointId, out string whyNot)`
  - `WaveFormat MixFormat`, `double PeriodMs`, `double StreamLatencyMs`
  - `Init(IWaveProvider)`, `int CurrentPaddingFrames`
  - `event Action<Exception> Stopped`
  - `Play()`, `Dispose()`
- Modify: `src/Framejet.Platform/Audio/WasapiAudioBackend.cs`:
  - per endpoint, try low-latency when `registry.ShouldTry(id)`; otherwise, or on `TryOpen` failure (`registry.Unsupported`, logged once), use the NAudio streams.
  - on `registry.Underrun(...) == true`, restart the session in normal mode on the UI path via the existing `Failed` → retry flow, with no user-visible warning, just a log line.
  - `AudioDelay.Mode` and `PeriodMs` reflect what is running.
- Modify: `src/Framejet/MainForm.cs` (own a `LowLatencyRegistry` and pass it in)
- Test: `tests/Framejet.Platform.Tests/Audio/LowLatencyTests.cs`

The initialisation sequence:
1. `GetSharedModeEnginePeriod(mix, out def, out fund, out min, out max)`
2. `InitializeSharedAudioStream(AUDCLNT_STREAMFLAGS_EVENTCALLBACK, min, mix, null)`
3. `SetEventHandle`

The render side pre-rolls one period of silence before `Start`.

- [ ] **Step 1:** Write the failing hardware tests:
  - `Render_opens_low_latency_on_default_output_or_says_why`: either `PeriodMs < 10`, or `whyNot` is non-empty and the backend runs in normal mode.
  - `Plays_through_low_latency_at_gain_zero`: Ripsaw or microphone; if supported, `s.Delay.Mode == "low-latency"`, `BytesCaptured > 0`, and no failure in 5 s.
  - `Unsupported_endpoint_runs_normal_mode` (Review Focus 4): pass a registry with that endpoint marked `Unsupported`, and assert `Mode == "normal"`.
- [ ] **Step 2:** Run the `LowLatencyTests` filter. Expected: FAIL.
- [ ] **Step 3:** Implement the interop and the two streams.
- [ ] **Step 4:** Integrate them into the backend.
- [ ] **Step 5:** Run all Platform audio tests, then the Core tests. Expected: PASS. Build with 0 warnings.
- [ ] **Step 6:** Commit: `feat: use Windows low-latency shared audio where supported`.

### Task 12: Final measurement, must-pass gates and docs

**Files:**
- Modify: `src/Framejet/SelfTestRunner.cs`:
  - must-pass 10: fail with `"audio delay p95 {x} ms > {limit} ms"` when audio ran and `TotalP95Ms` is over the limit for its `Mode`.
  - skip the gate, writing `audio delay: none`, when no audio ran.
- Modify: `docs/design.md`:
  - §4.5: the new chain and the measured figures replacing the 50–100 ms estimate.
  - §4.9: the report line.
  - §6: must-pass 5 reworded per spec 5.5, plus the new must-pass 10 with the measured limits.
  - §8: which endpoints support low-latency mode, and the measured periods.
- Modify: `CLAUDE.md` (the audio path paragraph: drift controller, low-latency mode, sync check)
- Modify: the spec's §10 table (final row)

- [ ] **Step 1:** Run the self-test three times (as in Task 6) and record the row "low-latency".
- [ ] **Step 2:** Set the limits to the measured p95 + 20%, per mode, rounded up to whole ms. Use the spec's placeholders (25 low-latency, 35 normal) if those are tighter.
- [ ] **Step 3:** Implement the gate. Run the self-test. Expected: exit 0, `result: PASS`.
- [ ] **Step 4:** Update the docs.
- [ ] **Step 5:** Ask the owner for the final "Check A/V sync…" run, and record it when it arrives.
- [ ] **Step 6:** Commit: `feat: gate the self-test on audio delay and document the measured figures`.
