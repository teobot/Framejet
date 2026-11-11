# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Framejet is a native Windows viewer for a USB capture card. It was built for a Razer Ripsaw X carrying a PS5, and it is tuned to add as little latency as possible.

The authoritative behaviour spec is `docs/design.md`. It marks which decisions were the owner's and which were Claude's calls.

## Build and test

The code is C# 9 targeting .NET Framework 4.8 (see `Directory.Build.props`). It builds with the .NET 5 SDK through `Microsoft.NETFramework.ReferenceAssemblies`, so Visual Studio isn't needed.

```powershell
dotnet build Framejet.sln -c Debug           # or -c Release; both build with 0 warnings
dotnet test tests\Framejet.Core.Tests         # pure logic, no hardware, under a second
dotnet test tests\Framejet.Platform.Tests     # real devices: capture card, webcam, audio endpoints, D3D11 windows
dotnet test tests\Framejet.Core.Tests --filter "FullyQualifiedName~VideoSessionTests"
```

- **Platform tests** are `[SkippableFact, Trait("Category", "Hardware")]`. They skip when a device isn't connected, run serially (test parallelization is disabled), and open real devices and windows. They read this PC's devices from `tests\Framejet.Platform.Tests\hardware.local.ini` (git-ignored; start from `hardware.example.ini`). A test whose device isn't set there skips.
- **Self-test** is the end-to-end lag gate. Run `Framejet.exe --selftest report.txt --settings <folder>\settings.ini --seconds 4`.
  - It runs for about 20 s, windowed and then fullscreen. It never saves settings, and it writes its log next to the settings file.
  - Success is exit code 0 and `result: PASS`.
- **Single exe:** `src\Framejet\bin\Release\net48\Framejet.exe` must run when copied alone into an empty folder. `.github\check-single-exe.ps1 src\Framejet\bin\Release\net48`, run in Windows PowerShell, checks that it loads every dll from inside itself.
- **Releases:** publishing a GitHub release runs `.github/workflows/release.yml` on a GitHub Windows runner. It builds with the SDK that `global.json` pins, stamps a `v1.2.3`-style tag into the version, runs the Core tests and the single-exe check, then attaches `Framejet.exe` and the files it ships with (the upload step lists them) to the release. If any step fails, it turns the release back into a draft, so the latest release always has its exe. "Run workflow" on the Actions tab does the same build and keeps the same files on the run instead. Every push to `main` and every pull request runs `.github/workflows/build.yml`: a Release build, the Core tests, the single-exe check and the third-party check.
- **Third-party source:** `third-party/DirectShowLib-source.zip` is the source of the DirectShowLib version that `Framejet.Platform.csproj` references, as the LGPL requires. When that version changes, replace the zip with the matching source and update `THIRD-PARTY-NOTICES.md`. `.github/check-third-party.ps1`, which both workflows run, fails until the notices name the new version.
- **Art:** `art/` holds the SVG sources; `art\render.ps1` rebuilds `src\Framejet\Framejet.ico` and the PNGs with Microsoft Edge headless.

## Hardware rules

- **One process per capture device.** A second open of a UVC capture device preempts the first: the first graph gets `EC_ERRORABORT` with `0xC00D3EA3`.
  - Never run the Platform suite, the self-test and the app at the same time.
  - The app is single-instance, and the self-test refuses to start while Framejet is running.
- **Timing tests depend on the environment.**
  - A fullscreen game in front throttles the test process's timers.
  - A game left as the active window on the primary screen spoils the screen timings. It can hide the test windows, so no timings come back: `Ripsaw_default_mode_draws_with_no_added_delay` fails with "no frames measured", and `Measures_latency_from_frame_statistics` with "no exact Present to screen samples". A borderless-fullscreen game can instead slow them, and `Ripsaw_default_mode_draws_with_no_added_delay` fails with "Present → screen mean 16.85 ms > 4.53 ms". A game open behind other windows is fine.
  - A dark room starves the webcam: no 1080p MJPG frames arrive.
  - Rule these out before chasing code.

## Architecture

There are three projects, layered so that the logic can be tested without hardware.

- **Framejet.Core** uses no Windows media APIs.
  - The state machines `Video/VideoSession` and `Audio/AudioSession` drive abstract `IVideoBackend`/`IAudioBackend`, using an `IScheduler` (UI-thread timers) and an `IDispatcher`.
  - Policy lives in small pure types: `DeviceRules`, `ModeCatalog`, `PlacementRules`, `GraphEventPolicy`, `NoSignalDetector`, `LatencyTrimmer`, `DelayCheck`, `ToolbarAutoHide`. `SettingsCarryOver` keeps its decision in the pure `ShouldCopy`; its `Run` does the file work.
  - Status and warning copy lives in `Ui/UiText`, which the spec quotes verbatim.
  - Tests use fakes, including `FakeScheduler` and the fake backends.
- **Framejet.Platform** holds the real backends:
  - DirectShow capture: `DirectShowVideoBackend`, `GrabberCallback`, `GraphEventWindow`
  - the Direct3D 11 presenter: `D3D11Presenter`, `FrameRenderer`, and `Shaders.hlsl`, which is an embedded resource compiled at runtime
  - WASAPI playthrough: `WasapiAudioBackend`, `PlaythroughChain`, `CaptureIngest`
  - container-ID lookup via cfgmgr32 (`DeviceContainers`), `DisplayMode` and `FileLog`
- **Framejet** is the WinForms shell (WinExe): `MainForm` wires the sessions to the UI, `ToolbarWindow` is the floating, no-activate, auto-hiding toolbar, and it also holds `SelfTestRunner` and `Program`.

### Frame path

1. The capture filter feeds a Smart Tee, then a Sample Grabber, then a Null Renderer. The graph has no reference clock (`SetSyncSource(null)`), so nothing waits on timestamps.
2. `BufferCB` copies each frame into `LatestFrameSlot` and sets the slot's auto-reset event.
3. The presenter runs its own AboveNormal render thread, which takes the newest frame only on a signalled wake. Taking on unsignalled wakes contends with `BufferCB`'s copy under the slot lock.
4. Pixel shaders convert NV12/YUY2/UYVY/RGB32 with BT.709/601 limited range, and Catmull-Rom handles upscaling.
5. Frames go out on a FLIP_DISCARD swap chain with 2 buffers, maximum frame latency 1 and tearing allowed. V-Sync On is `Present(1)`; V-Sync Off is `Present(0, ALLOW_TEARING)`.

Measurement notes:
- `MeasureLatency` is on only in the self-test and hardware tests. It makes the render loop wake every 1 ms to sample DXGI frame statistics.
- Those statistics can undercount vblanks in fullscreen, so the self-test judges its delay bars by the screen's display-mode refresh (`DisplayMode`).

Known limitation: on a 60 Hz screen with V-Sync On, a ~60 fps source often queues behind the previous frame, adding ~30 ms. 144 and 165 Hz screens are unaffected.

### Audio path

- The user never picks the input. It is the WASAPI capture endpoint that shares the video device's container ID.
- Capture is shared-mode and event-driven. It feeds `LatencyTrimmer`, which targets 15 ms and trims above 60 ms.
- Framejet's own channel mapper and resampler come next. The default output can be, for example, 8-channel 96 kHz float.
- `WasapiOut` then plays to the Windows default output, which it follows live, or to a chosen output.

### Resilience (spec §4.7)

- When another app takes the card, `VideoSession` yields and shows "press Rescan". It never retakes the card by itself; Rescan and explicit picks do.
- A device loss within 5 s of a start counts as a failed start and retries after 3 s.
- A running stream with no new frames for 3 s is restarted.
- Audio retries every 3 s after a failure.
- Device-change notifications are debounced by 800 ms and default-output changes by 300 ms.

### Threading

- DirectShow and COM run on the UI (STA) thread, and graph events arrive through a message-only window.
- NAudio posts its events to the UI `SynchronizationContext`.
- Only `BufferCB` and the render thread run elsewhere.
- `DirectShowVideoBackend.Stop` clears every field before it blocks, because the presenter join can pump messages.

### Settings and logs

- Settings go in `%AppData%\Framejet\settings.ini` as key=value lines. Writes are atomic, debounced by 500 ms and flushed on close.
- On first run, `SettingsCarryOver` copies `settings.ini` from `%AppData%\PlayThrough` (the app's earlier name) if `%AppData%\Framejet` doesn't exist yet. There is no copy with `--settings`.
- Logs go in `Framejet.log`, with the previous session kept as `Framejet.old.log`.
- Only an explicit mode pick saves a mode; the automatic default follows the screen the window is on.

### Single exe

- The `EmbedDependencies` target in `src/Framejet/Framejet.csproj` embeds every copy-local dll as `deps.<name>.dll`.
- `Program`'s static constructor hooks `AssemblyResolve` before anything else loads, and `Main` only calls a `[NoInlining] Run`.
- Keep Core, Platform and third-party types out of `Program`'s fields and out of `Main`, or the exe breaks outside its build folder.
- Don't use Costura (it fails on long paths) or an App.config.

## Conventions and traps

- In xunit 2.4.2, `Assert.Equal(float, float, int)` is ambiguous (CS0121). Cast to `double`.
- Device paths start `\\?\` and screen names are `\\.\DISPLAY1`; in a verbatim string that is `@"\\.\DISPLAY1"`. Losing one backslash fails silently, so grep after editing.
- Commits are GPG-signed; never bypass signing. In Windows PowerShell 5.1, pass commit messages with `git commit -F <file>`, because `-m` mangles embedded quotes.

## Personal details

Never write the owner's personal details (name, hardware, devices, folder paths) into code, comments, docs or commit messages. The Razer Ripsaw X and the PS5 are the product's target, not personal details.
