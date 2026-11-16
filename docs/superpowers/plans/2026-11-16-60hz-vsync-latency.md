# 60 Hz V-Sync Latency Fix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** With V-Sync On, the newest frame shows at the next refresh instead of queueing behind the previous one. A "Never skip frames" option restores today's behaviour.

**Architecture:** `D3D11Presenter` picks its present call from three flags: V-Sync, never-skip and tearing support. V-Sync On with skipping allowed is `Present(0, None)` on the flip-model swap chain: the newest frame wins at the next refresh, with no tearing. A new `AppSettings.NeverSkipFrames` (`vsync.neverskip`) flows to the presenter the same way `VSync` does. A right-click menu item toggles it. Task 4 is a measuring gate: if the newest-wins call backlogs or tears on the real 60 Hz screen, stop.

**Tech Stack:** C# 9, .NET Framework 4.8, SharpDX 4.2.0 (DXGI flip model), WinForms, xunit 2.4.2 + Xunit.SkippableFact.

**Spec:** `docs/superpowers/specs/2026-11-16-60hz-vsync-latency-design.md`

## Global Constraints

- V-Sync On never tears (must-pass 8): V-Sync On never passes `DXGI_PRESENT_ALLOW_TEARING`.
- Default is skipping allowed: `NeverSkipFrames` defaults to `false`. With it `true`, V-Sync On is exactly today's `Present(1, None)`.
- V-Sync Off is unchanged: `Present(0, ALLOW_TEARING)` when tearing is supported, else `Present(0, None)`.
- Settings key `vsync.neverskip`, values `true`/`false`; junk or a missing key means `false`.
- Menu text is exactly `Never skip frames`, directly after the `V-Sync` item, checked when on, disabled while V-Sync is off. It doesn't go on the toolbar.
- 60 Hz bars (must-pass 9): Present → screen mean ≤ 9.8 ms and p95 ≤ 18.2 ms; arrival → Present p95 ≤ 2.5 ms. 144 and 165 Hz numbers must not get worse.
- Never run the Platform tests, the self-test and the app at the same time (one process per capture device).
- Commits are GPG-signed; never bypass signing. Write commit messages to a file and use `git commit -F <file>`. End each with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Never write the owner's personal details (name, devices, folder paths) into code, docs or commits.
- Other sessions are changing `D3D11Presenter` (live latency, Vortice, precompiled shaders). Keep this change to the present call, one property and the replaced-frame count, so merges stay small.

## Review Focus

1. **Toggling either switch mid-stream:** the next present uses the new mode, with no restart and no dropped device. The presenter reads volatile fields per frame (test in Task 3).
2. **V-Sync Off with never-skip on:** never-skip has no effect, and frames still tear-present (test in Task 3).
3. **Tearing unsupported** (the swap chain fell back to flags None): V-Sync On newest-wins is still `Present(0, None)`, never the tearing flag (test in Task 3).
4. **Old or junk settings file:** a missing `vsync.neverskip` or one set to `maybe` loads as `false` (test in Task 2).
5. **Device loss while newest-wins:** the recreated device keeps the mode, because the flag lives on the presenter, not the device (assert in Task 3).

---

### Task 1: Baseline measurement on the 60 Hz screen (no code)

**Files:**
- Create: the shared notes folder's `60hz-baseline.txt` (outside the repo, so no personal details reach git)

Before any code changes, record today's backlog so Task 4 has something to compare against.

- [ ] **Step 1:** Build Release: `dotnet build Framejet.sln -c Release`. Expected: 0 warnings, 0 errors.
- [ ] **Step 2:** Make a scratch settings folder with a `settings.ini` that places the window on the 60 Hz screen: `window.x/y/width/height` inside that screen's bounds (from `[System.Windows.Forms.Screen]::AllScreens`), plus `window.screen=<its device name>`. Copy `mode=` from the real settings so the default mode is used.
- [ ] **Step 3:** Close Framejet, then run `src\Framejet\bin\Release\net48\Framejet.exe --selftest <scratch>\report.txt --settings <scratch>\settings.ini --seconds 4` with `Start-Process -PassThru` + `WaitForExit`. Expected: the report's `display 60 Hz` on both phases. With today's code, `delay check` likely fails, with a Present → screen mean of around 20–30 ms.
- [ ] **Step 4:** Copy the report's `windowed delay`, `fullscreen delay` and `frames` lines into `60hz-baseline.txt` in the notes folder. If the baseline already passes, tell the owner before going on: the cause isn't what the spec assumes.

### Task 2: The `NeverSkipFrames` setting

**Files:**
- Modify: `src/Framejet.Core/Settings/AppSettings.cs` (after `VSync`)
- Modify: `src/Framejet.Core/Settings/SettingsStore.cs` (Load switch and Save, next to `vsync`)
- Test: `tests/Framejet.Core.Tests/Settings/SettingsStoreTests.cs`

**Interfaces:**
- Produces: `public bool AppSettings.NeverSkipFrames { get; set; }` (default `false`), stored as `vsync.neverskip`.

- [ ] **Step 1: Write the failing tests.** Extend the existing tests:
  - `Missing_file_gives_defaults`: add `Assert.False(s.NeverSkipFrames);`
  - `Round_trips_every_field`: set `NeverSkipFrames = true` in the initializer and add `Assert.True(r.NeverSkipFrames);`
  - `Tolerates_junk`: add `vsync.neverskip=maybe\n` to the file text and `Assert.False(s.NeverSkipFrames);`
- [ ] **Step 2:** Run `dotnet test tests\Framejet.Core.Tests --filter "FullyQualifiedName~SettingsStoreTests"`. Expected: a compile error, because `NeverSkipFrames` is not defined.
- [ ] **Step 3:** Add the property with a summary like `With V-Sync On, every frame waits its turn instead of the newest replacing one not yet shown.` Add `case "vsync.neverskip"` (bool.TryParse, as `vsync`), and in Save, `Line("vsync.neverskip", B(s.NeverSkipFrames));` right after `vsync`.
- [ ] **Step 4:** Run the same filter. Expected: PASS. Then run the whole Core suite: `dotnet test tests\Framejet.Core.Tests`. Expected: all pass.
- [ ] **Step 5:** Commit: `feat(core): add the never-skip-frames setting`.

### Task 3: Newest-frame-wins presenting

**Files:**
- Modify: `src/Framejet.Platform/Video/D3D11Presenter.cs` (the present call at ~line 350, a new property next to `VSync`, the replaced-frame count in `PollStats`)
- Modify: `src/Framejet.Platform/Video/DirectShowVideoBackend.cs` (property next to `VSync` ~line 55; presenter construction ~line 199)
- Modify: `src/Framejet/MainForm.cs:295` (pass `NeverSkipFrames = Settings.NeverSkipFrames` at construction)
- Test: `tests/Framejet.Platform.Tests/Video/D3D11PresenterTests.cs`

**Interfaces:**
- Consumes: `AppSettings.NeverSkipFrames` (Task 2).
- Produces:
  - `public bool D3D11Presenter.NeverSkipFrames { get; set; }`, volatile and settable from any thread like `VSync`.
  - `public bool DirectShowVideoBackend.NeverSkipFrames { get; set; }`, forwarding to the live presenter and to presenters it creates later.
  - `internal static (int SyncInterval, PresentFlags Flags) D3D11Presenter.PresentArgs(bool vsync, bool neverSkip, bool tearingSupported)`. The test project can already see `internal` members if `SimulateDeviceLost` compiles there; check `InternalsVisibleTo`.

- [ ] **Step 1: Write the failing tests.** `PresentArgs` is pure, so its tests are plain `[Theory]`s with no hardware trait:

  | vsync | neverSkip | tearing | expected |
  |---|---|---|---|
  | true | false | true | (0, None) |
  | true | false | false | (0, None) |
  | true | true | true | (1, None) |
  | true | true | false | (1, None) |
  | false | false | true | (0, AllowTearing 0x200) |
  | false | true | true | (0, AllowTearing 0x200) |
  | false | false | false | (0, None) |

  Name it `Picks_the_present_call(bool vsync, bool neverSkip, bool tearing, int interval, int flags)`.

  Also add a hardware test, `[SkippableFact, Trait("Category","Hardware")] Newest_wins_keeps_up_and_survives_toggles_and_device_loss`, using `WithPresenter` and `Feed(slot, f, 200)`:
  - With `MeasureLatency = true` and VSync on, pump 1 s.
  - Set `NeverSkipFrames = true` and pump 0.5 s, then set it back to `false` and pump 0.5 s.
  - Call `SimulateDeviceLost()` and pump 1 s.
  - Assert `DeviceResets == 1` and `Drawn >= 120`, and that `PresentToScreen.Count > 0` (the stats still read in newest-wins mode).
- [ ] **Step 2:** Run `dotnet test tests\Framejet.Platform.Tests --filter "FullyQualifiedName~D3D11PresenterTests"`. Expected: a compile error, because `PresentArgs` and `NeverSkipFrames` are not defined.
- [ ] **Step 3: Implement.**
  - Add `PresentArgs` per the table, with the 0x200 constant already defined as `AllowTearingPresent`.
  - Replace the `if (vsync) … else …` present lines with one call through `PresentArgs(_vsync, _neverSkip, _tearingSupported)`.
  - Keep `timed: !tearing` true for every non-tearing present, because a `Present(0, None)` frame's display time is still after its Present.
  - Add the property with a summary like `With V-Sync On: true waits every frame's turn (Present(1)); false lets the newest frame replace one not yet shown (Present(0) without tearing).`
  - In `DirectShowVideoBackend`, mirror the `VSync` field and property, and set it in the presenter initializer.
- [ ] **Step 4: Count replaced frames while measuring.** In `PollStats`, when `SyncRefreshCount` advances by exactly 1 and `PresentCount` advances by more than 1 since the last sample, add `PresentCount delta − 1` to a new `_replaced` under `_statsLock`. Clear it in `ResetStats`, and add it into `stats.Dropped` in `GetStats`. Only do this while not tearing: tearing presents aren't held for a refresh. Add a one-line comment: this is counted only while measuring, because the 1 ms poll sees every refresh. Outside measuring, nothing polls.
- [ ] **Step 5:** Run the same filter, with no Framejet or self-test running. Expected: PASS (the hardware tests need a working GPU and desktop only, not the card).
- [ ] **Step 6:** `dotnet build Framejet.sln -c Release`. Expected: 0 warnings.
- [ ] **Step 7:** Commit: `feat(video): let the newest frame replace one not yet shown with V-Sync on`.

### Task 4: Measuring gate on the real screens (no code)

Use the Task 1 scratch settings and the Release build from Task 3.

- [ ] **Step 1: 60 Hz, default.** Run the self-test. Expected:
  - `result: PASS`
  - both phases show `display 60 Hz`, Present → screen mean ≤ 9.8 ms and p95 ≤ 18.2 ms
  - `dropped` is small: around the beat of a 59.94 fps source on a 60 Hz screen, not dozens per second
- [ ] **Step 2: tearing check.** Run the app on the 60 Hz screen with the PS5 on. Pan the camera fast in a game, windowed and then fullscreen. Expected: no tear line. This is a visual check: ask the owner to confirm.
- [ ] **Step 3: 60 Hz, never-skip.** Add `vsync.neverskip=true` to the scratch `settings.ini` and re-run. Expected: numbers like Task 1's baseline. This proves the switch restores today's behaviour.
- [ ] **Step 4: 165 Hz and 144 Hz.** Move the scratch window bounds to each of those screens, remove `vsync.neverskip`, and re-run each. Expected: `result: PASS`, with the mean and p95 no worse than the spec's 165 Hz figures (≤ 4.5 / 7.6 ms) and the 144 Hz equivalents (≤ 4.97 / 8.44 ms).
- [ ] **Step 5:** Append every run's delay and frames lines to `60hz-baseline.txt` in the notes folder.
- [ ] **Gate:** if Step 1 or Step 2 fails (a backlog remains, or it tears in direct scanout), stop here and report the numbers to the owner. The spec's fallback, approach 2 (detect and skip), needs its own plan. Tasks 2 and 3's setting and menu plumbing stay. Don't continue to Task 5 without the owner.

### Task 5: Menu item and self-test report

**Files:**
- Modify: `src/Framejet/MainForm.cs` (a new method next to `ToggleVSync` ~line 195)
- Modify: `src/Framejet/VideoMenu.cs` (`Fill`, right after the V-Sync item ~line 51)
- Modify: `src/Framejet/SelfTestRunner.cs` (`Report`: one line after `capture format`)

**Interfaces:**
- Consumes: `AppSettings.NeverSkipFrames` (Task 2), `DirectShowVideoBackend.NeverSkipFrames` (Task 3).
- Produces: `public void MainForm.ToggleNeverSkipFrames()`.

- [ ] **Step 1:** Implement `ToggleNeverSkipFrames()`. It does what `ToggleVSync` does: return if closed, flip the setting, push it to `VideoBackend`, and call `SaveSettings()`. There's no toolbar refresh, because the toolbar has no such button.
- [ ] **Step 2:** In `Fill`, after the V-Sync item, add `var neverSkip = Item("Never skip frames", form.Settings.NeverSkipFrames, form.ToggleNeverSkipFrames); neverSkip.Enabled = form.Settings.VSync; menu.Items.Add(neverSkip);`.
- [ ] **Step 3:** In `Report`, add `Line("frame skipping: " + (_form.Settings.NeverSkipFrames ? "never (every frame waits its turn)" : "newest frame wins"));`. The self-test doesn't override the setting, so the settings file decides, as Task 4 Step 3 relies on.
- [ ] **Step 4:** `dotnet build Framejet.sln -c Release`, then run the self-test once on any screen. Expected: the `menu:` line lists `… | V-Sync | Never skip frames | …`, and the report has the `frame skipping:` line.
- [ ] **Step 5:** Manual check in the app: right-click shows the item checked or unchecked to match the setting. Turn V-Sync off and the item greys out. Toggle the item, close the app, and `settings.ini` has `vsync.neverskip=true`.
- [ ] **Step 6:** Commit: `feat(ui): add Never skip frames to the right-click menu`.

### Task 6: Docs

**Files:**
- Modify: `docs/design.md`:
  - §4.2: add V-Sync On's newest-wins `Present(0)` without tearing, and the never-skip option. Mark it **(owner)**: it's the owner's choice of latency over smoothness, with an opt-out.
  - §4.6: add the menu entry after ✓ V-Sync.
  - §4.8: add never-skip to the remembered settings.
  - §6 must-pass 9: replace the Known limitation paragraph with the measured 60 Hz result from Task 4, and say that never-skip brings the old behaviour back.
- Modify: `CLAUDE.md` (the Frame path step 5 line and the Known limitation line)
- Modify: `README.md` (line 24's bullet and the "Late picture on a 60 Hz screen" section: now fixed by default; "Never skip frames" trades it back for smoothness)

- [ ] **Step 1:** Make the edits using Task 4's numbers. Grep for `queues behind` afterwards. Expected: no hits, apart from text describing never-skip.
- [ ] **Step 2:** `dotnet build Framejet.sln -c Release` and `dotnet test tests\Framejet.Core.Tests`. Expected: 0 warnings, all pass.
- [ ] **Step 3:** Commit: `docs: V-Sync On shows the newest frame at the next refresh`.

---

## Revision after the Task 4 gate failed (2026-11-16)

Task 4 failed. A trace found the real cause, and the spec's "Revision after measuring" section now holds the approved fix: a bounded wait on a frame-latency waitable object, skipping on timeout. Tasks 1, 2 and 3 are done. **Run Tasks 7, 8 and 9, then Task 5, then Task 10.** Task 6 is superseded by Task 10, and Task 3's `Present(0)` path and replaced-frame count are removed by Task 7.

Execution order: 7 → 8 → 9 (gate) → 5 → 10.

### Task 7: Bounded-wait presenting

**Files:**
- Modify: `src/Framejet.Platform/Video/D3D11Presenter.cs`
- Modify: `src/Framejet.Platform/Video/PresenterStats.cs` (add `QueueWait`)
- Modify: `src/Framejet.Platform/Video/DirectShowVideoBackend.cs` (add a `<summary>` to `NeverSkipFrames`, as its neighbours have)
- Test: `tests/Framejet.Platform.Tests/Video/D3D11PresenterTests.cs`

**Interfaces:**
- Consumes: `D3D11Presenter.NeverSkipFrames` and `DirectShowVideoBackend.NeverSkipFrames` (Task 3, unchanged).
- Produces:
  - `internal static PresentPlan D3D11Presenter.Plan(bool vsync, bool neverSkip, bool tearingSupported)`. It replaces `PresentArgs`. `PresentPlan` is an internal readonly struct with `int SyncInterval`, `PresentFlags Flags`, `uint WaitMs` and `bool SkipOnTimeout`.
  - `const uint SkipAfterWaitMs = 4` and `const uint LongWaitMs = 1000`.
  - `PresenterStats.QueueWait` (`TimingStats`): the wait before Present for presented frames, the last 2,000 frames.

- [ ] **Step 1: Write the failing tests.** Replace the `Picks_the_present_call` theory with `Plans_the_present(bool vsync, bool neverSkip, bool tearing, int interval, int flags, int waitMs, bool skip)`:

  | vsync | neverSkip | tearing | interval | flags | waitMs | skip |
  |---|---|---|---|---|---|---|
  | true | false | true | 1 | 0 | 4 | true |
  | true | false | false | 1 | 0 | 4 | true |
  | true | true | true | 1 | 0 | 1000 | false |
  | true | true | false | 1 | 0 | 1000 | false |
  | false | false | true | 0 | 0x200 | 1000 | false |
  | false | true | true | 0 | 0x200 | 1000 | false |
  | false | false | false | 0 | 0 | 1000 | false |

  Keep Task 3's hardware test (toggles plus device loss), and add an assertion that `GetStats().QueueWait.Count > 0`. Add a hardware test, `Waitable_swap_chain_survives_resize`: with `WithPresenter`, feed 150 frames. Resize the form to 960×540 after 0.5 s and back to 1280×720 after 1 s, pump until the feeder ends, and assert `Drawn >= 100` and `DeviceResets == 0`.
- [ ] **Step 2:** Run `dotnet test tests\Framejet.Platform.Tests --filter "FullyQualifiedName~D3D11PresenterTests"`, after the Get-Process check that no Framejet.exe or testhost is running. Expected: compile errors (`Plan`, `QueueWait`).
- [ ] **Step 3: Implement.**
  - **Swap chain:** create it with `DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT` (`(SwapChainFlags)0x40`) OR'd into both the tearing and the fallback flags. `_swapChainFlags` already carries it into `ResizeBuffers`. `_tearingSupported` tests the tearing bit with `&`.
  - **Waitable object:** QueryInterface `SwapChain2` (dispose it), set `MaximumFrameLatency = 1`, and keep `FrameLatencyWaitableObject` in `_latencyWait`. `CloseHandle` it in `ReleaseDevice` before the swap chain goes. If the QueryInterface fails, leave `_latencyWait` zero and present without waiting (log once at Info).
  - **Before Present:** take `plan = Plan(_vsync, _neverSkip, _tearingSupported)`. Record `arrival → now` as ArrivalToPresent first, then `WaitForSingleObject(_latencyWait, plan.WaitMs)`.
    - If the wait times out and `plan.SkipOnTimeout`: `_frameInFlight = false`, `CountSkipped()`, poll the stats if measuring, and return without presenting. Don't call `RecordDrawn` or raise `LumaSampled` for a skipped frame.
    - If it times out without SkipOnTimeout, present anyway.
    - Record the wait in a new `_queueWait` SampleRing (presented frames only). `presentTime` is taken after the wait, right before Present, as today.
  - **RecordDrawn:** split it so ArrivalToPresent is arrival → wait start, while `_drawTimes` keeps the present time.
  - **Remove** `PresentArgs`, `_replaced`, `_lastPresentTearing` and the replaced-frame logic in `PollStats`. `PollStats` returns to its pre-Task-3 form.
  - The render loop already holds `timeBeginPeriod(1)`, so the 4 ms wait has 1 ms granularity.
  - `QueueWait` is cleared in `ResetStats` and copied in `GetStats`. Update the `Dropped` summary in PresenterStats: "replaced by a newer one before drawing, or skipped (paused, no window area, device lost, or the display queue still busy after 4 ms)".
- [ ] **Step 4:** Run the same filter. Expected: all pass. Run it a second time to check the feeder-timing tests aren't flaky. Then `dotnet test tests\Framejet.Core.Tests`, and `dotnet build Framejet.sln -c Release` with 0 warnings.
- [ ] **Step 5:** Commit: `fix(video): skip a frame only when the display queue stays busy for 4 ms`.

### Task 8: Delay bar and queue-wait report

**Files:**
- Modify: `src/Framejet.Core/Video/DelayCheck.cs`
- Modify: `src/Framejet/SelfTestRunner.cs` (`Delays` line)
- Test: `tests/Framejet.Core.Tests/Video/DelayCheckTests.cs`

**Interfaces:**
- Consumes: `PresenterStats.QueueWait` (Task 7).
- Produces: `public const double MeanSlackMs = 4.0` in `DelayCheck`. `SlackMs = 1.5` stays for p95.

- [ ] **Step 1: Write the failing tests.**
  - `Sixty_hertz_latch_deadline_passes`: `Evaluate(T(0.7, 0.9), T(11.6, 17.0), 60).Pass` is true.
  - `Mean_bar_allows_four_ms_over_half_a_refresh`: at 165 Hz (164.85), mean 7.0 passes and mean 7.1 fails with a reason starting `"Present → screen mean 7.10 ms > 7.03 ms"`, each with p95 6.0.
  - Keep the existing tests. If one asserts the old mean limit, update its expected value to the new bar and say so in the report.
- [ ] **Step 2:** Run `dotnet test tests\Framejet.Core.Tests --filter "FullyQualifiedName~DelayCheckTests"`. Expected: the new tests fail.
- [ ] **Step 3:** Change the mean limit to `refresh / 2 + MeanSlackMs`. Comment it: the display needs a frame a few ms before a refresh (measured ~3–4 ms at 60 Hz), so a frame presented in that window shows a refresh later, and that is the display's latch, not Framejet. In `SelfTestRunner.Delays`, append `; queue wait mean={QueueWait.MeanMs:0.00} p95={QueueWait.P95Ms:0.00} ms` after the arrival→Present part.
- [ ] **Step 4:** Run the Core suite and the Release build. Expected: all pass, 0 warnings.
- [ ] **Step 5:** Commit: `test(core): allow for the display's latch deadline in the delay check`.

### Task 9: Measuring gate, bounded wait (no code)

Same method as Task 4: the scratch settings from Task 1, the shared-card checks, and runs counted only on their target screen. Use `--seconds 15`.

- [ ] **Step 1:** Three default runs on 60 Hz. Expected: `result: PASS`, fullscreen Present → screen mean ≤ 12.33 ms and p95 ≤ 18.17 ms, and dropped/arrived ≤ 5%.
- [ ] **Step 2:** One never-skip run on 60 Hz. Expected: dropped = 0. It may FAIL the mean bar if the backlog happens; record it either way.
- [ ] **Step 3:** One default run each on 165 Hz and 144 Hz. Expected: 165 Hz PASS with no skips beyond 1%. 144 Hz is recorded only, because its p95 fails on the original code too (the spec's separate finding).
- [ ] **Step 4:** Append all lines to `60hz-baseline.txt` in the notes folder.
- [ ] **Gate:** if Step 1 misses its bars, stop and report.

### Task 10: Docs (replaces Task 6)

**Files:** `docs/design.md`, `CLAUDE.md`, `README.md`

- [ ] **Step 1: `docs/design.md`.**
  - §4.2: V-Sync On waits up to 4 ms for the display queue (frame-latency waitable object, latency 1), then `Present(1)`, or skips the frame if the queue is still busy. Add Never skip frames, which waits and always presents. Mark it **(owner: latency over smoothness, with an opt-out; the 4 ms wait is Claude's call from measurement)**. The swap chain line gains the waitable flag.
  - §4.6 menu: add `Never skip frames` after ✓ V-Sync.
  - §4.8: add never-skip to the remembered settings.
  - §4.9: the report also gives the queue wait.
  - §6 must-pass 9: the new mean bar, half a refresh + 4 ms, with the latch-deadline reason. Replace the Known limitation with the measured 60 Hz result. Note the skip-then-stall risk (suspected adaptive sync) and the 144 Hz 15 s p95 finding as open items.
  - §8: add a short "From the 2026-11-16 60 Hz trace" list:
    - the 59.940 vs 60.000 Hz beat (~16.7 s)
    - the ~3–4 ms latch deadline at 60 Hz
    - `Present(0)` without tearing doesn't replace a queued frame in direct scanout
    - frame statistics lag in composed mode, so they can't drive skipping
- [ ] **Step 2: `CLAUDE.md`.** In Frame path step 5, add the waitable object and the 4 ms bounded wait. Replace the Known limitation paragraph with one line: at 60 Hz a frame that misses the display's ~4 ms latch shows a refresh later; Framejet skips one frame to stop that from building up, unless Never skip frames is on.
- [ ] **Step 3: `README.md`.** In line 24's bullet and the "Late picture on a 60 Hz screen" section, say the extra delay is now cleared by skipping an occasional frame. "Never skip frames" in the right-click menu trades that back for smoothness.
- [ ] **Step 4:** Grep for `queues behind` and `parked`. Expected: no stale claims. Then the Release build and the Core tests.
- [ ] **Step 5:** Commit: `docs: V-Sync On clears the 60 Hz backlog by skipping a frame`.
