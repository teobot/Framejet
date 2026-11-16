# Playback Health Log Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** In dev builds, log a `hiccup:` line for every bad second, a per-minute summary and the player's context, and keep 5 sessions of logs up to 8 MB each.

**Architecture:** A pure `HealthMonitor` in Core judges one counter sample per second. `MainForm` feeds it from new cheap counter reads on the presenter and the audio backend, on a 1 s UI timer that runs only in dev builds. `FileLog` gains retention and size options.

**Tech Stack:** C# 9, .NET Framework 4.8, WinForms, xunit 2.4.2.

**Spec:** `docs/superpowers/specs/2026-11-16-playback-health-log-design.md`

## Global Constraints

- Dev builds only: `AppVersion.TryParse(Application.ProductVersion, out v) && v.IsDev`, and never in the self-test. Release behaviour is byte-for-byte unchanged.
- No `GetStats()` per second (allocates ~150 KB); counters only. Nothing new on the render or capture threads.
- Thresholds: arrival under 0.9 × mode fps × elapsed; dropped over 0.1 × arrived; tick gap over 2 s.
- Dev log: keep 4 old logs (`Framejet.old.log`, `Framejet.old2.log`, `Framejet.old3.log`, `Framejet.old4.log`), 8 MB (8,388,608 bytes) per log.
- No personal details (names, devices, paths) in code, docs or commits. Signed commits via `git commit -F`.

## Review Focus

1. Minimised window: frames still arrive but are skipped. No drop hiccups while paused (test in Task 1).
2. Backend restart (stall restart, mode change, device unplugged): counters fall to 0. No negative deltas, no huge false totals (test in Task 1).
3. First sample after start: nothing to compare against, so it must not be judged (test in Task 1).
4. No audio device: audio counters stay 0 and audio is not judged (test in Task 1).
5. Retention with a locked old log: logging still starts (the existing `A_locked_old_log_does_not_stop_logging` must keep passing in Task 3).

---

### Task 1: HealthMonitor (Core)

**Files:**
- Create: `src/Framejet.Core/Diagnostics/HealthMonitor.cs`
- Test: `tests/Framejet.Core.Tests/Diagnostics/HealthMonitorTests.cs`

**Interfaces:**
- Produces:
  - `struct HealthSample { double Seconds; bool VideoRunning; bool Paused; double ModeFps; long Arrived, Drawn, Dropped; int DeviceResets; double Fps; bool AudioRunning; int AudioTrims, AudioUnderruns; }`. The counters are cumulative; `Seconds` is a monotonic clock.
  - `sealed class HealthMinute { double Seconds; long Arrived, Drawn, Dropped; double LowestFps /* 0 when no steady second */; int Hiccups, DeviceResets, AudioTrims, AudioUnderruns, SlowTicks; }`
  - `sealed class HealthMonitor { string Sample(HealthSample s) /* "hiccup ..." or null */; HealthMinute TakeMinute(); }`

- [ ] **Step 1: Write the failing tests.** Helper `Steady(double t, long arrived, long drawn, long dropped)` at 60 fps, running, audio running.
  - `First_sample_is_not_judged`: `Sample(Steady(0, 0, 0, 0))` is null.
  - `A_normal_second_is_quiet`: 0 then (1.0, 60, 59, 1) → null.
  - `Too_few_frames_from_the_card_is_a_hiccup`: (1.0, 50, 50, 0) → contains `"card sent too few frames"` and `"arrived 50 (expected 60)"`.
  - `Dropping_over_a_tenth_is_a_hiccup`: (1.0, 60, 52, 8) → contains `"dropped frames"`; 6 of 60 → null.
  - `A_device_reset_is_a_hiccup`; `An_audio_trim_is_a_hiccup`; `An_audio_underrun_is_a_hiccup`.
  - `A_slow_tick_is_a_hiccup_scaled_by_elapsed`: (2.5, 150, 150, 0) → contains `"slow tick 2.5 s"` but not `"too few"`.
  - `Paused_seconds_are_not_judged_for_frames`: both samples `Paused = true`, 0 drawn and 60 dropped → null.
  - `A_counter_that_falls_counts_from_zero`: (1, 60, 60, 0), then (2, 30, 30, 0) (restart) → arrival judged on 30, and the minute's `Arrived` is 90, not negative.
  - `Audio_not_running_is_not_judged`: `AudioRunning = false`, trims rising → null.
  - `TakeMinute_sums_and_resets`: 3 normal seconds and one hiccup → `Arrived` 240, `Hiccups` 1, `LowestFps` the lowest `Fps` seen; a second `TakeMinute` is all zeros.
- [ ] **Step 2: Run** `dotnet test tests\Framejet.Core.Tests --filter "FullyQualifiedName~HealthMonitorTests"`. Expected: fails to compile.
- [ ] **Step 3: Implement.** The previous sample decides "steady": frames are judged only when both samples are running, not paused, with the same `ModeFps > 0`; audio is judged only when both are running. `delta(prev, cur) = cur >= prev ? cur - prev : cur`. Line: `hiccup (<reasons joined by ", ">): <elapsed:0.00> s, arrived A (expected E), drawn D, dropped X, F fps; audio trims T, underruns U`, invariant culture.
- [ ] **Step 4: Run the tests.** Expected: all pass.
- [ ] **Step 5: Commit.**

### Task 2: Cheap counters (Platform)

**Files:**
- Create: `src/Framejet.Platform/Video/PresenterCounters.cs` and `src/Framejet.Platform/Audio/AudioCounters.cs`
- Modify: `D3D11Presenter.cs`, `DirectShowVideoBackend.cs` and `WasapiAudioBackend.cs`

**Interfaces:**
- Produces:
  - `struct PresenterCounters { long Arrived, Drawn, Dropped; int DeviceResets; double Fps; }`
  - `D3D11Presenter.GetCounters()` and `DirectShowVideoBackend.GetCounters()` (default when there's no presenter).
  - `struct AudioCounters { bool Running; int Trims, Underruns; }` and `WasapiAudioBackend.GetCounters()`.

- [ ] **Step 1: Implement.**
  - `GetCounters` takes `_statsLock` and reads the same fields `GetStats` uses, with no arrays.
  - The audio session's counters read `_ingest`'s trimmer total and `_underruns.Underruns`. Expose `CaptureIngest.TrimsTotal` under its lock.
- [ ] **Step 2: Run** `dotnet build Framejet.sln -c Debug`. Expected: 0 warnings.
- [ ] **Step 3: Commit.**

### Task 3: FileLog retention and size options

**Files:**
- Modify: `src/Framejet.Platform/Os/FileLog.cs`
- Test: `tests/Framejet.Platform.Tests/Os/FileLogTests.cs`

**Interfaces:**
- Produces:
  - `FileLog(string path, int keepOld = 1, int maxBytes = MaxBytes)`.
  - `static string OldLogPath(string path, int n = 1)`: n = 1 gives `Framejet.old.log`; n > 1 gives `Framejet.old{n}.log`.

- [ ] **Step 1: Write the failing tests.**
  - `Keeping_four_old_logs_keeps_five_sessions`: sessions 1–6 with `keepOld: 4`. `old4` holds "second", `old` holds "fifth", and the log holds "sixth".
  - `The_size_limit_is_an_option`: `maxBytes: 200`, then write 10 lines of 50 chars. The file is at most about 260 bytes and ends with `log full`.
- [ ] **Step 2: Run** `dotnet test tests\Framejet.Platform.Tests --filter "FullyQualifiedName~FileLogTests"`. Expected: compile failure.
- [ ] **Step 3: Implement.**
  - Rotate: delete `old{keepOld}`, move `old{n}` to `old{n+1}` from high to low, then move the log to `old`. Each step is best effort.
  - Replace the `MaxBytes` uses with a `_maxBytes` field.
- [ ] **Step 4: Run the tests.** Expected: all FileLog tests pass, the existing ones included.
- [ ] **Step 5: Commit.**

### Task 4: MainForm wiring, context lines, docs

**Files:**
- Modify: `src/Framejet/MainForm.cs`, `docs/design.md` (logs bullet) and `CLAUDE.md` (Settings and logs)

**Interfaces:**
- Consumes: Tasks 1–3.

- [ ] **Step 1: Implement.**
  - `readonly bool _devBuild` is set before the `FileLog` is created. Dev builds use `new FileLog(path, keepOld: 4, maxBytes: 8 * 1024 * 1024)`.
  - Add a 1000 ms `_healthTimer`, started in `StartDevices` when `_devBuild`. It runs `SampleHealth()`, which builds a `HealthSample` from `Video.State == Running`, `Video.Mode?.Fps`, `WindowState == Minimized`, both counter reads and the Stopwatch seconds, then logs any line it returns.
  - Stop and dispose the timer on close.
  - In dev builds, `LogStats` logs `stats (minute): video arrived=… drawn=… dropped=… (x.x%) lowest fps=… hiccups=… resets=… slow ticks=…; arrival→present p95=… ms queue wait p95=… ms; audio buffer=… max=… trims=… underruns=…; memory=… MB gc=g0/g1/g2`. It uses `TakeMinute()` plus the existing `GetStats` and `GetStats(reset: true)` calls. Release builds keep the old line.
  - Add `void Note(string text)`, which logs only when `_devBuild`. Call it on:
    - fullscreen on and off (`OnFullscreenChanged`);
    - minimise and restore (`OnResize`, on change only);
    - the V-Sync, Never skip and Scaling changes;
    - the window's screen changing (`LocationChanged` and `SizeChanged`), with the device name, bounds and `DisplayMode.CurrentRefreshHz` Hz. Also log the screen once at start.
  - Docs: one sentence each saying that dev builds also log per-second hiccups, per-minute health and context, and keep 5 sessions of up to 8 MB.
- [ ] **Step 2: Build Release.** Run `dotnet build Framejet.sln -c Release`. Expected: 0 warnings. Then run the Core tests: all pass.
- [ ] **Step 3: Live check.** With no other Framejet or test running, run the Release exe with `--settings` pointing at a temp folder for 70 s, then close it. The log should have a screen line, a `stats (minute)` line, and no hiccups when there's a steady signal.
- [ ] **Step 4: Self-test.** Run `Framejet.exe --selftest report.txt --settings <temp>\settings.ini --seconds 4`. Expected: exit 0 and `result: PASS`.
- [ ] **Step 5: Commit.** Then open the PR.
