# Framejet — design

This is the design Framejet was built from. Code comments cite its section numbers, such as "Spec 4.3".

Marked **(owner)** where the owner decided or approved; **(Claude's call)** where Claude decided and the owner has not objected.

## 1. What and why

**Framejet** is a lightweight native Windows app that does only one job: show a capture card's picture and play its sound, with selectable resolution and frame rate. It was built for playing a PS5 on a PC through a Razer Ripsaw X capture card: PS5 → HDMI → Ripsaw X → USB → PC. The card has no HDMI passthrough, so the PC screen *is* the TV. Before Framejet, the way to view and play was to open OBS just for that.

**Success:** the player opens Framejet, it finds the card, and they're playing, and it never gives them a reason to go back to OBS.

**Anti-goals.** Any of these makes it a failure even if it technically works (the owner's words): **lag, bugs, bad framerate or resolution.**

## 2. Goals and non-goals

Goals
- Lowest practical delay from card to screen and to speakers.
- Rides out real-life device mess without being touched (section 4.7).
- Uses the card's real modes at their true frame rates, with a good default; measured fps is visible.
- Light: one small exe, opens in about a second, low CPU (Claude's target).
- Playable on any of the reference setup's three screens (owner).

Non-goals
- Recording, streaming, scenes, overlays, filters, screenshots (Claude's guess, not contradicted).
- Mic input or mixing (the owner: "I don't really care about a mic").
- Changing a monitor's refresh rate (the owner declined).
- Turning variable refresh rate (VRR) on or off: it's a GPU driver setting, normally **off** on the reference PC. Framejet neither can nor tries to change it.
- HDR, capture-card picture controls, installer, auto-update, starting with Windows.
- Auto-opening webcams.

## 3. The reference setup (measured 2026-11-09)

**Screens.** All at 100% scaling, with HDR off.

| Connection | Current mode | Refresh rates at that resolution |
|---|---|---|
| DisplayPort (primary) | 2560×1440 @ 164.85 Hz | 60/100/120/144/165 |
| HDMI | 1920×1080 @ 60 Hz | 50/59/60 |
| DisplayPort | 1920×1080 @ 144 Hz | 60/100/120/144 |

**Video devices:** the Razer Ripsaw X, a USB webcam and two virtual cameras.

**Ripsaw X capture modes** (no MJPG or H264; "@N" is the fastest rate for that size, and discrete lower rates come from `IAMVideoControl.GetFrameRateList`):
- NV12: 640×480@60, 720×480@60, 720×576@50, 1280×720@60, 1920×1080@120, 2560×1440@60, 3840×2160@30
- YUY2: 640×480@60, 720×480@60, 720×576@50, 1280×720@60, 1920×1080@60, 2560×1440@50

**Webcam:** a typical USB webcam (used for tests without the card), with MJPG up to 1920×1080 at 30 fps, YUY2 up to 640×480 at 30, and no audio endpoint.

**Audio inputs:** the card's HDMI input, plus headset, virtual and streaming mics.

**Audio outputs:** a headset, speakers and a monitor's audio, switched between often, plus virtual endpoints, one of which is the Windows default.

**Windows device containers** (`DEVPKEY_Device_ContainerId`): the Ripsaw's camera and "HDMI (Razer Ripsaw X)" share one container. The webcam has its own container and no audio endpoint. Virtual mics carry the "no container" ID `{00000000-0000-0000-FFFF-FFFFFFFFFFFF}`.

**Toolchain:** Windows 11 (10.0.26200), long paths enabled. .NET SDKs 3.1.426 and 5.0.408 only; .NET Framework 4.8 built in. Git 2.52; commits are GPG-signed, never bypass signing. Present: qedit.dll (Sample Grabber, Null Renderer), quartz.dll, d3dcompiler_47.dll.

## 4. Decisions

### 4.1 Platform
- C# 9, WinForms, .NET Framework 4.8 (nothing to install), built with the .NET 5 SDK plus Microsoft.NETFramework.ReferenceAssemblies 1.0.3. The build is AnyCPU with Prefer32Bit off, so it runs as a 64-bit process on 64-bit Windows. A single `Framejet.exe` with its dependencies embedded **(owner)**.
- Per-monitor DPI aware (PerMonitorV2 in the manifest), so the picture is never bitmap-stretched on any screen **(Claude's call)**.
- Single instance: launching it again brings the running window forward instead of fighting over the card **(Claude's call)**.

### 4.2 Video (the owner: Direct3D 11 instead of VMR9, with a V-Sync toggle that defaults to On)
- **Capture:** DirectShowLib 1.0.0. Graph: capture filter → Smart Tee (inserted by the graph builder) → Sample Grabber (BufferCB) → Null Renderer. The graph has **no reference clock** (`IMediaFilter.SetSyncSource(null)`), so frames are delivered the moment they arrive. The grabber asks for the mode's raw format (NV12 or YUY2). For MJPG modes it asks for RGB32 and DirectShow inserts the MJPEG Decompressor.
- **Drawing:** Direct3D 11 through SharpDX 4.2.0 **(Claude's call: unmaintained since 2019, but proven on the reference PC with net48, and Direct3D 11 itself is frozen)**. A render thread takes only the **newest** frame; an older frame not yet drawn is dropped and counted. It uploads the frame to dynamic textures (NV12 as R8 + R8G8 planes, YUY2 as half-width RGBA, RGB32 as BGRA), converts YUV to RGB in a pixel shader, scales into a letterboxed viewport and presents. Framejet itself queues no frames.
- **Handoff (Claude's call):** keep the spike's two copies (the spike was a throwaway Direct3D 11 prototype, measured in section 8): BufferCB copies into a managed buffer, then the render thread copies into the mapped texture. That took 1.27 ms p95 from arrival to Present at 1440p NV12, so it's fast enough. Don't add threading complexity to save a copy.
- **Swap chain:** flip model (FLIP_DISCARD), 2 buffers, maximum frame latency 1.
- **V-Sync On (default):** `Present(1)`. No tearing; each frame waits for the next refresh.
- **V-Sync Off:** the swap chain is created with ALLOW_TEARING and presents with `Present(0, ALLOW_TEARING)`. Frames show instantly but tear, unless VRR is on, in which case they are instant and tear-free.
- **Colour (Claude's call):** BT.709 limited range for modes of 720 lines or more, BT.601 below.
- **Scaling (Claude's call):** sharper than plain bilinear when upscaling (for example 1080p on the 1440p screen). The exact filter is left to the builder.
- **GPU device lost** (driver update or reset): recreate the Direct3D device and swap chain without restarting capture **(Claude's call)**.
- **Minimised (Claude's call):** capture and audio keep running; drawing is skipped.
- **Fullscreen:** a borderless window covering the monitor the window is on, never exclusive mode. It re-fits if moved to another monitor. The swap chain resizes on size, DPI and monitor changes. Resizing the window is free; letterboxing handles any shape.

### 4.3 Modes
- **List (owner):** one entry per size and frame rate. For each entry, use the best format in this order: NV12 > YUY2 > UYVY > other raw > MJPG > H264. Hide entries below 23.9 fps or 480 lines unless nothing else exists. Labels look like "2560×1440 · 60 fps"; showing the format is the builder's call.
- **Saved key (owner):** "WxH@fps/FORMAT", for example "2560x1440@60/NV12". Resolve in this order: exact match, then the same size and rate in another format, then the default.
- **Default (Claude's call, approved by the owner; a change from the owner's original 1080p60):** the biggest mode at about 60 fps (59–61) that fits within the screen the window opens on.
  - The Ripsaw X on the 1440p screen: 2560×1440@60 NV12, pixel for pixel. On the 1080p screens: 1920×1080@60 NV12.
  - With no mode near 60 fps: the biggest mode at 50 fps or more, else the fastest mode at the biggest size.
  - 120 fps modes are listed but never the default.
  - The user's pick always wins and is remembered.
  - The automatic choice is not saved; a mode you pick is.
- If a mode fails to start, the status says why and Framejet goes back to the last working mode **(Claude's call)**.

### 4.4 Devices
- **Video (owner):** the saved pick wins, matched by device path, then by name if the card moved USB port. Otherwise auto-open only names that look like capture cards (case-insensitive substring): ripsaw, capture, hdmi, cam link, elgato, avermedia, live gamer, usb video, game. Never auto-open a webcam. If nothing matches, show "No capture card found" and wait for one.
- **Audio input (the owner: no picker, "as long as the audio matches and works with the video"):**
  - Automatically use the recording endpoint in the **same Windows device container** as the video device, ignoring the "no container" ID.
  - Fallback: a recording device whose name contains the video device's name or its distinctive words. Brand words like "Razer" don't count.
  - No match means no audio, and the status says so. Never pick a random mic.
  - A manual override exists only in the right-click menu ("Audio input ▸ Automatic / …") **(Claude's call)**.
  - Changing or replugging the video device re-resolves the audio input.
- **Audio output (owner):** either "Default output", which follows Windows default changes live, or a chosen device. If the chosen device disappears, fall back to the default and say so.

### 4.5 Audio (owner)
- NAudio.Wasapi 2.2.1. The chain: shared, event-driven `WasapiCapture(device, true, 10)` → convert WAVEFORMATEXTENSIBLE float to a plain IeeeFloat `WaveFormat` → `BufferedWaveProvider` → volume → shared, event-driven `WasapiOut(device, AudioClientShareMode.Shared, true, 20)`. Resample if the capture and output rates differ; how is the builder's call.
- **LatencyTrimmer:** hold the buffer near 15 ms. When it passes 60 ms (the card's clock running slightly fast), trim back to the target. Underruns play silence without growing the delay.
- **Volume** follows a squared curve. **Mute** toggles with M.
- **Expectation:** sound will trail the picture by an estimated 50–100 ms (the Windows audio engine, audio software in the chain, a wireless headset). That's under the roughly 125 ms where people notice. Not measured; there is no A/V offset control.

### 4.6 Window and controls (the owner picked a layout with the toolbar floating over the picture)
- Dark theme. In a window, the picture fills the client area, letterboxed.
- The **toolbar floats over the bottom of the picture** and fades out 2 s after the mouse stops moving. Moving the mouse over the window brings it back. It stays up while hovered or while a dropdown or menu is open. A new warning brings it up for 5 s **(Claude's call)**.
- Toolbar, left to right: video device ▾ · mode ▾ · rescan ⟳ | audio output ▾ · mute · volume | V-Sync toggle · fullscreen | status text (e.g. "1440p · 59.9 fps", or a warning).
- **How it's built (Claude's call):** the toolbar is its own borderless, owned, layered window positioned over the bottom of the video, because flip-model Direct3D content can cover ordinary child controls placed on top of it. Verify early that dropdowns and the fade work, and that the picture returns to direct scanout once the toolbar hides.
- **Fullscreen:** picture only, with no toolbar. The cursor hides after 2 s idle. F11, Alt+Enter or double-click toggles; Esc leaves fullscreen; M mutes. The right-click menu works everywhere. Shortcuts work whichever Framejet window has focus.
- **Right-click menu:** Framejet 1.0.0 (the version; not clickable) · Fullscreen (F11) · Mute (M) · ✓ V-Sync · Video device ▸ · Mode ▸ · Audio output ▸ · Audio input (Automatic) ▸ · Rescan devices · Exit.
- **Messages:**
  - With no picture, centred in the picture area: "Waiting for Razer Ripsaw X… plug it in and it starts by itself", or "Couldn't start Razer Ripsaw X — retrying".
  - In the status text: "No picture — is the PS5 on?" after about 3 s of frames that are almost all darker than video black (at least 95% of sampled luma at or below 10), which is what the Ripsaw sends with no HDMI signal: black with a small logo. Ordinary black at video black (Y=16) during play does not count **(Claude's call, from the spike and the owner's PS5-off capture)**; "No audio from the card"; audio device errors, as "Audio error: {message} — retrying" while Framejet tries the audio again every 3 s by itself.

### 4.7 Resilience (owner)
- Listen for device changes: WM_DEVICECHANGE for video, and `IMMNotificationClient` registered through `MMDeviceEnumerator.RegisterEndpointNotificationCallback` for audio. Refresh 800 ms after the last change. If the running video device is still present, **don't restart the video**: an unrelated USB plug must never interrupt play.
- Another app opening the card (for example OBS): Windows hands the card to whichever app opened it last. Launching Framejet takes the card. If another app takes it while Framejet runs, Framejet lets go and shows "{name} is in use by another app — press Rescan to take it back"; it never grabs the card back by itself. Rescan, or picking a device or mode, takes it back. Start failures for other reasons retry every 3 s. **(Claude's call, 2026-11-09, after the build found that a second open preempts the first.)**
- Card unplugged, or DirectShow EC_DEVICE_LOST via `IMediaEventEx.SetNotifyWindow`: show "Waiting for …" and resume when it returns, including on a different USB port.
- "Default output" follows a Windows default-device switch mid-game.
- Keep the display and system awake while video runs (`SetThreadExecutionState`), because the controller is on the PS5 and the PC sees no input. Release it when video stops or the app exits.

### 4.8 Settings (owner)
- `%AppData%\Framejet\settings.ini`, key=value, tolerant of junk lines and unknown keys. Written atomically **(Claude's call)**.
- On first run, if `%AppData%\Framejet` doesn't exist and `%AppData%\PlayThrough\settings.ini` (the app's earlier name) does, that file is copied across, and the old folder is never changed.
- Remembers: video device (path and name), mode key, audio output, audio input override, volume, mute, V-Sync, window bounds and maximised state, and whether it was fullscreen and on which screen.
- Restore the window position only if it lands on a connected screen; otherwise centre on the primary. Reopen fullscreen if it was closed fullscreen and that screen is still connected **(Claude's call)**.
- Keep a small log of device events and errors at `%AppData%\Framejet\Framejet.log`, the last two sessions (`Framejet.log` and `Framejet.old.log`) **(Claude's call)**.

### 4.9 Self-test (owner, extended for Direct3D)
`Framejet.exe --selftest out.txt` runs the real window for a few seconds (windowed, then fullscreen), writes a UTF-8 report and exits with code 0 for a pass. It exits with code 1 if must-pass 9's delay check fails **(Claude's call)**. The report covers:
- devices found, and why each was chosen or skipped
- the negotiated mode
- frames arrived, drawn and dropped, and the measured fps
- arrival-to-screen time from DXGI frame statistics
- presentation mode (direct/overlay or composed)
- picture brightness mean and variation (the frame itself is never saved)
- audio bytes, peak and buffer level
- errors

### 4.10 Packaging (owner)
- A single `Framejet.exe`. An MSBuild target with `AfterTargets="ResolveAssemblyReferences"` turns `ReferenceCopyLocalPaths` .dlls into `EmbeddedResource` items named "deps.<name>.dll".
- `AppDomain.AssemblyResolve` is hooked in Program's static constructor, and `Main` calls a `[MethodImpl(MethodImplOptions.NoInlining)] Run()`.
- No Costura.Fody: it fails on long paths.
- Shaders may be precompiled at build time to drop the runtime compiler (builder's call).

## 5. Architecture (the owner's draft, updated for Direct3D 11)
Some of these names are the draft's; the Architecture section of `CLAUDE.md` has the code's current names.
- **Core** (pure, xUnit-tested, no devices):
  - modes: VideoMode, PixelFormats, ModeCatalog (Build, PickDefault(screen), Resolve(key))
  - DeviceRules (LooksLikeCaptureCard, PickVideo, MatchAudioInput by container, then name)
  - settings: Settings, SettingsStore(path), WindowPlacement
  - LatencyTrimmer, VolumeCurve, NoSignalDetector, ToolbarAutoHide
  - VideoSession over an IVideoBackend fake (start, busy-retry, lost, return, mode switch)
  - AudioSession over an IAudioBackend fake, posting to the UI through a dispatcher
- **Platform:** DirectShowCapture, D3D11Presenter, DeviceContainers (cfgmgr32 and MMDevice properties), WasapiAudioBackend with the playthrough chain, AudioDeviceWatcher, DeviceChangeWatcher, KeepAwake.
- **UI:** MainForm (thin), VideoSurface, ToolbarWindow, dark theme, context menu, self-test runner.
- **Hardware tests:** Xunit.SkippableFact with `[Trait("Category","Hardware")]`, skipped when the device is missing. DirectShow and Direct3D tests run on an STA thread with a real Form.

## 6. Must-pass cases
1. An unrelated USB plug doesn't restart the video.
2. OBS holds the card when Framejet launches: Framejet takes it over at once. OBS takes the card while Framejet runs: Framejet steps aside with the Rescan message and doesn't fight OBS for it; Rescan takes it back.
3. The card is unplugged and replugged, including on another USB port.
4. The default audio output is switched mid-game.
5. A long session with the card's clock slightly fast keeps the audio delay at or under 60 ms.
6. The floating toolbar over Direct3D appears, fades and its dropdowns work, and the picture is back to direct scanout when it's hidden.
7. Windowed and fullscreen both work on each of the three screens (165, 60 and 144 Hz).
8. V-Sync On never tears; V-Sync Off never stalls.
9. **No added delay (Claude's call, reworded after review).** Measured at the default mode with V-Sync On, on any screen:
   - our code: frame arrival → Present, p95 ≤ 2.5 ms.
   - the display: Present → on screen, mean ≤ half a refresh + 1.5 ms and p95 ≤ one refresh + 1.5 ms. In other words, each frame goes out at the first refresh after Present, and none waits an extra refresh.

   At 165 Hz that means a mean of 4.5 ms or less and a p95 of 7.6 ms or less. The spike met both at the 1440p60 NV12 default:
   - arrival → Present: 0.94 ms mean, 1.27 ms p95
   - Present → screen: 3.91 ms mean, 6.83 ms p95
   - end to end: 4.85 ms mean, 7.73 ms p95

   The bar measures whether Framejet adds delay, not when the next refresh happens to land. If a mode ever misses it by a fraction of a millisecond, investigate, but keep the 1440p default: the owner approved it, and resolution is one of the anti-goals too.

   **Known limitation:** on a 60 Hz screen with V-Sync On, a ~60 fps source often queues behind the previous frame, adding ~30 ms. 144 and 165 Hz screens are unaffected. The fix is parked.

## 7. Testing
- When the card isn't there, a webcam stands in for video and a microphone **at volume 0** (no feedback) for audio. Both are set in `tests/Framejet.Platform.Tests/hardware.local.ini`, a git-ignored file started from `hardware.example.ini`. Automated audio tests always run at volume 0 or muted.
- A WinExe's console output doesn't reach PowerShell. Write reports to files, launch with `Start-Process -PassThru` + `WaitForExit`, and read them with `Get-Content -Encoding UTF8`.

## 8. Proven facts (don't re-discover)

**From the earlier prototype**
- DirectShowLib 1.0.0 is assembly 2.1.0.1599, and the class is `CaptureGraphBuilder2`.
- `IAMStreamConfig.GetStreamCaps(int, out AMMediaType, IntPtr)`.
- `IAMVideoControl.GetFrameRateList(pin, capIndex, Size, out int, out IntPtr)` gives the discrete frame rates. UVC caps list one entry per size. `EventCode.DeviceLost` exists.
- Graph order that works:
  1. `AddSourceFilterForMoniker`
  2. capture pin `IAMStreamConfig.SetFormat`, with `AvgTimePerFrame` set in VIDEOINFOHEADER/VIDEOINFOHEADER2
  3. add the renderer
  4. `RenderStream(PinCategory.Capture, MediaType.Video, src, …)`
  5. `IMediaFilter.SetSyncSource(null)`
  6. `Run`, plus `IMediaEventEx.SetNotifyWindow` for device-lost
- The graph builder inserts a Smart Tee, and MJPG adds the MJPEG Decompressor. A typical webcam lists about 200 raw modes, so collapsing them is required.
- NAudio.Wasapi 2.2.1:
  - constructors: `WasapiCapture(MMDevice, bool eventSync, int bufferMs)` and `WasapiOut(MMDevice, AudioClientShareMode, bool eventSync, int latencyMs)`
  - `Init(ISampleProvider)` is an extension in `NAudio.Wave.WaveExtensionMethods`
  - `IMMNotificationClient` is in `NAudio.CoreAudioApi.Interfaces`
  - shared capture returns WAVEFORMATEXTENSIBLE float 48 kHz stereo; convert it to a plain IeeeFloat format before use
  - Playthrough from a USB microphone to a virtual output worked.
- Costura.Fody fails on long paths, and the embedding approach in 4.10 works.
- Microsoft.NETFramework.ReferenceAssemblies 1.0.3 is needed for net48 with the .NET 5 SDK.
- A DPI-aware manifest is needed so the renderer and WinForms agree on pixel sizes.

**From the 2026-11-09 Direct3D 11 spike**

| Run | fps in | Arrival → on screen (mean / p95) | Presentation | CPU (one core) |
|---|---|---|---|---|
| Webcam MJPG 1080p30, fullscreen, V-Sync | 26.1 | 5.3 / 8.1 ms | overlay (direct) | 16% |
| Webcam MJPG 1080p30, window 1 px short of fullscreen, V-Sync | 30.0 | 5.3 / 8.0 ms | overlay (direct) | 30% (JPEG decoding) |
| Ripsaw YUY2 1080p60, fullscreen, V-Sync | 59.4 | 4.9 / 7.7 ms | mostly overlay | 13% |
| Ripsaw NV12 1440p60 (the default), fullscreen, V-Sync | 58.8 | 4.85 / 7.73 ms | mostly overlay | 12% |
| Ripsaw NV12 1440p60, fullscreen, tearing allowed | 59.94 | immediate (tears) | mostly overlay | 17% |

- The refresh stayed fixed at 164.85 Hz in every run (VRR off). 60 fps frames were held for 3, 3, 3 then 2 refreshes: the expected slight unevenness.
- CPU figures include the spike's 1 ms statistics polling, which product code shouldn't need.
- The 1440p V-Sync run was added after the review. Its frames were black (no HDMI signal at the time, so arrival timing was jittery). Upload and present timing don't depend on picture content.
- SharpDX 4.2.0 builds and runs on net48 with the .NET 5 SDK, and `ShaderBytecode.Compile` works (d3dcompiler_47). Sample Grabber and Null Renderer work on Windows 11.
- `SwapChain.FrameStatistics` gives exact display times when `PresentRefreshCount == SyncRefreshCount`. With tearing these times can come before the Present call, so don't use them as latency then.
- In one run the Ripsaw delivered about 60 fps of black frames with jittery timing (sd 3 ms), likely with no HDMI signal. With a live signal it delivered a rock-steady 59.94 fps (sd 0.13 ms).
- A second open of the same UVC device preempts the first: the second Start succeeds and the first graph gets EC_ERRORABORT with 0xC00D3EA3 (MF_E_VIDEO_RECORDING_DEVICE_PREEMPTED). Measured on a webcam.

## 9. Left to the builder
- project and solution layout, and names
- threading details
- exact shaders, scaling filter and colour handling beyond 4.2
- settings key names
- toolbar visuals beyond the layout in section 4.6
- how the self-test drives the window
- log format
- how to simulate device loss, busy devices and clock drift in tests
