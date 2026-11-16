# Scaling Choice Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a four-option Scaling choice (Fill, Whole steps crisp, Whole steps smooth, 1:1), available from the toolbar and the right-click menu and saved in settings.

**Architecture:** A pure Core function `Scaling.Place` decides the picture's rectangle from the choice, and `Scaling.Filter` decides the scaling shader. `FrameRenderer.Draw` and `StatusText.Scale` both call them, so the status always describes what is drawn. The choice reaches the render thread as a volatile presenter property, the same way V-Sync does.

**Tech Stack:** C# 9, .NET Framework 4.8, WinForms, SharpDX Direct3D 11, xUnit 2.4.2 (+ Xunit.SkippableFact for hardware tests).

**Spec:** `docs/superpowers/specs/2026-11-16-scaling-choice-design.md`

## Global Constraints

- Settings key `scaling`, values `fill|crisp|smooth|1to1`, default `fill`; an unknown value loads as `fill`.
- Menu and toolbar labels (exact): `Fill`, `Whole steps (crisp)`, `Whole steps (smooth)`, `1:1`. The submenu is titled `Scaling`.
- The status format is unchanged: "1:1" when the drawn box equals the frame size (including a cropped 1:1), otherwise the percentage per the existing `StatusText.Scale` rules ("200%" for a 2× whole step).
- No extra GPU pass, copy or Present. The only change in the draw is the viewport rectangle and the sampler/shader choice.
- The self-test forces `Fill` in memory, as it does V-Sync, and never saves.
- `Assert.Equal(float, float, int)` is ambiguous in xunit 2.4.2: cast to `double`.
- Commits are GPG-signed. In Windows PowerShell use `git commit -F <file>`. Don't write personal details anywhere.
- `dotnet build Framejet.sln -c Debug` and `-c Release` must stay at 0 warnings.

## Review Focus

1. **The window is smaller than the picture in Whole steps** (1440p in a maximized 2560×1360 window): it must fall back to the Fill rectangle, never draw a 0× (empty) picture. Covered by a `Place` test in Task 1.
2. **1:1 in a window smaller than the picture:** the viewport origin goes negative, and the GPU must clip it, not reject it or draw black. Covered by the crop test in Task 3.
3. **A zero-size area** (minimized, or mid-resize): `Place` must not throw, and the status leaves the scale out. Covered by Task 1 and Task 2 tests.
4. **Junk or old settings files** (`scaling=maybe`, key missing): load as Fill. Covered in Task 2.
5. **Changing scaling or mode while video runs:** the rectangle is recomputed on every draw from the current frame size and choice, so there's no cached box to go stale. Covered in Task 3 by `Draw` taking the mode as an argument (no stored rectangle). Check it by hand in Task 5.

---

### Task 1: Core placement and filter rules

**Files:**
- Create: `src/Framejet.Core/Video/Scaling.cs`
- Test: `tests/Framejet.Core.Tests/Video/ScalingTests.cs`

**Interfaces:**
- Produces:
  - `public enum ScalingMode { Fill, WholeStepsCrisp, WholeStepsSmooth, OneToOne }` (namespace `Framejet.Core.Video`)
  - `public enum ScaleFilter { Bilinear, CatmullRom, Point }`
  - `public static class Scaling` with:
    - `public static readonly IReadOnlyList<ScalingMode> All` (in the order above)
    - `public static Rectangle Place(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight, ScalingMode mode)`
    - `public static ScaleFilter Filter(ScalingMode mode, Rectangle box, int sourceWidth, int sourceHeight)`
    - `public static string Key(ScalingMode mode)` → `"fill"|"crisp"|"smooth"|"1to1"`
    - `public static ScalingMode Parse(string key)` → the matching mode, or `Fill` for null, empty or unknown values (case-insensitive)

- [ ] **Step 1: Write the failing tests** in `ScalingTests.cs`:

```csharp
[Theory] // sw, sh, tw, th, mode, x, y, w, h
[InlineData(2560, 1440, 2560, 1360, ScalingMode.Fill,             71,   0, 2418, 1360)]
[InlineData(2560, 1440, 2560, 1360, ScalingMode.WholeStepsCrisp,  71,   0, 2418, 1360)] // 1× doesn't fit: falls back to Fill
[InlineData(2560, 1440, 2560, 1360, ScalingMode.WholeStepsSmooth, 71,   0, 2418, 1360)]
[InlineData(2560, 1440, 2560, 1360, ScalingMode.OneToOne,          0, -40, 2560, 1440)] // crop, centred
[InlineData(1920, 1080, 2560, 1440, ScalingMode.Fill,              0,   0, 2560, 1440)]
[InlineData(1920, 1080, 2560, 1440, ScalingMode.WholeStepsCrisp, 320, 180, 1920, 1080)]
[InlineData(1920, 1080, 2560, 1440, ScalingMode.OneToOne,        320, 180, 1920, 1080)]
[InlineData(1280,  720, 2560, 1440, ScalingMode.WholeStepsSmooth,  0,   0, 2560, 1440)] // exactly 2×
[InlineData(1280,  720, 2560, 1360, ScalingMode.WholeStepsCrisp, 640, 320, 1280,  720)] // 2× doesn't fit: 1×
[InlineData(1920, 1080, 1280,  720, ScalingMode.OneToOne,       -320, -180, 1920, 1080)]
public void Place_follows_the_mode(int sw, int sh, int tw, int th, ScalingMode mode, int x, int y, int w, int h)
    => Assert.Equal(new Rectangle(x, y, w, h), Scaling.Place(sw, sh, tw, th, mode));

[Theory, MemberData(nameof(AllModes))]
public void Place_with_no_area_does_not_throw(ScalingMode mode) { Scaling.Place(1920, 1080, 0, 0, mode); }

[Theory] // mode, box w, box h, expected (source 1280×720)
[InlineData(ScalingMode.Fill,             2560, 1440, ScaleFilter.CatmullRom)]
[InlineData(ScalingMode.WholeStepsSmooth, 2560, 1440, ScaleFilter.CatmullRom)]
[InlineData(ScalingMode.WholeStepsCrisp,  2560, 1440, ScaleFilter.Point)]
[InlineData(ScalingMode.WholeStepsCrisp,  1280,  720, ScaleFilter.Bilinear)]
[InlineData(ScalingMode.WholeStepsCrisp,   640,  360, ScaleFilter.Bilinear)] // shrunk to fit: same as Fill
[InlineData(ScalingMode.OneToOne,         1280,  720, ScaleFilter.Bilinear)]
[InlineData(ScalingMode.Fill,              640,  360, ScaleFilter.Bilinear)]
public void Filter_is_point_only_for_crisp_upscaling(ScalingMode mode, int bw, int bh, ScaleFilter expected)
    => Assert.Equal(expected, Scaling.Filter(mode, new Rectangle(0, 0, bw, bh), 1280, 720));

[Theory, MemberData(nameof(AllModes))]
public void Key_round_trips(ScalingMode mode) => Assert.Equal(mode, Scaling.Parse(Scaling.Key(mode)));

[Theory] [InlineData(null)] [InlineData("")] [InlineData("maybe")]
public void Unknown_keys_parse_as_fill(string key) => Assert.Equal(ScalingMode.Fill, Scaling.Parse(key));

[Fact] public void Keys_are_the_settings_values()
    => Assert.Equal(new[] { "fill", "crisp", "smooth", "1to1" }, Scaling.All.Select(Scaling.Key).ToArray());
```

(`AllModes` is `Scaling.All.Select(m => new object[] { m })`.)

- [ ] **Step 2: Run the tests and check they fail**

Run: `dotnet test tests\Framejet.Core.Tests --filter "FullyQualifiedName~ScalingTests"`
Expected: a build failure, because `Scaling` doesn't exist.

- [ ] **Step 3: Implement `Scaling.cs`**

- Fill: delegate to `Letterbox.Fit`.
- Whole steps: `n = Math.Min(tw / sw, th / sh)` using integer division. If `n >= 1`, return an `sw*n × sh*n` box centred with `(tw - w) / 2, (th - h) / 2`; otherwise use `Letterbox.Fit`.
- OneToOne: an `sw × sh` box at `((tw - sw) / 2, (th - sh) / 2)`. C# integer division truncates toward zero, and that is what the tests expect.
- `Filter`: if the box is larger than the source on either axis, return `Point` for `WholeStepsCrisp` and `CatmullRom` for every other mode. Otherwise return `Bilinear`. This matches today's `FrameRenderer` rule.

- [ ] **Step 4: Run the tests and check they pass**

Run: `dotnet test tests\Framejet.Core.Tests --filter "FullyQualifiedName~ScalingTests"`
Expected: all pass.

- [ ] **Step 5: Commit** `feat: add Scaling placement rules`

---

### Task 2: Status text, labels and the setting

**Files:**
- Modify: `src/Framejet.Core/Ui/StatusText.cs`, `src/Framejet.Core/Ui/UiText.cs`, `src/Framejet.Core/Settings/AppSettings.cs`, `src/Framejet.Core/Settings/SettingsStore.cs`
- Test: `tests/Framejet.Core.Tests/Ui/UiLogicTests.cs`, `tests/Framejet.Core.Tests/Settings/SettingsStoreTests.cs`

**Interfaces:**
- Consumes: `ScalingMode`, `Scaling.Place`, `Scaling.Key`, `Scaling.Parse` (Task 1).
- Produces:
  - `StatusText.Scale(int frameWidth, int frameHeight, Size area, ScalingMode scaling = ScalingMode.Fill)`
  - `StatusText.Format(VideoMode mode, double measuredFps, Size pictureArea, IEnumerable<string> warnings, ScalingMode scaling = ScalingMode.Fill)`
  - `UiText.ScalingMenu = "Scaling"` and `UiText.ScalingLabel(ScalingMode mode)`, which returns the exact labels in Global Constraints
  - `AppSettings.Scaling` (a `ScalingMode`, default `Fill`), stored as `scaling=<key>`

- [ ] **Step 1: Write the failing tests**

  In `UiLogicTests.cs`:
  ```csharp
  [Theory] // fw, fh, area w, area h, mode, expected
  [InlineData(2560, 1440, 2560, 1360, ScalingMode.OneToOne,        "1:1")]   // cropped is still 1:1
  [InlineData(2560, 1440, 2560, 1360, ScalingMode.WholeStepsCrisp, "94%")]   // fell back to Fill (2418 px wide)
  [InlineData(1920, 1080, 2560, 1440, ScalingMode.WholeStepsCrisp, "1:1")]
  [InlineData(1920, 1080, 2560, 1440, ScalingMode.Fill,            "133%")]
  [InlineData(1280,  720, 2560, 1440, ScalingMode.WholeStepsSmooth,"200%")]
  public void Scale_follows_the_scaling_mode(int fw, int fh, int aw, int ah, ScalingMode mode, string expected)
      => Assert.Equal(expected, StatusText.Scale(fw, fh, new Size(aw, ah), mode));
  [Theory, MemberData(...all modes...)] public void Scale_is_null_with_no_area(ScalingMode mode)
      => Assert.Null(StatusText.Scale(1920, 1080, Size.Empty, mode));
  [Fact] public void Scaling_labels_are_the_spec_copy()
      => Assert.Equal(new[] { "Fill", "Whole steps (crisp)", "Whole steps (smooth)", "1:1" }, Scaling.All.Select(UiText.ScalingLabel).ToArray());
  ```

  In `SettingsStoreTests.cs`:
  - Extend the defaults test with `Assert.Equal(ScalingMode.Fill, s.Scaling)`.
  - Extend the round-trip test with `Scaling = ScalingMode.WholeStepsCrisp` and assert it reads back.
  - Extend the junk-lines test's file with `scaling=maybe\n` and assert `ScalingMode.Fill`.

- [ ] **Step 2: Run the tests and check they fail**

Run: `dotnet test tests\Framejet.Core.Tests`
Expected: a build failure, because of the missing members.

- [ ] **Step 3: Implement**

- `StatusText.Scale` replaces `Letterbox.Fit` with `Scaling.Place(..., scaling)`. Keep the existing early `null` return for a zero area (it comes before `Place`).
- `Format` passes `scaling` through.
- `SettingsStore` reads `case "scaling": s.Scaling = Scaling.Parse(value)` and writes `Line("scaling", Scaling.Key(s.Scaling))` next to `vsync`.

- [ ] **Step 4: Run the tests and check they pass**

Run: `dotnet test tests\Framejet.Core.Tests`
Expected: all pass, including the existing status tests unchanged.

- [ ] **Step 5: Commit** `feat: status, labels and setting for Scaling`

---

### Task 3: The renderer and presenter draw by the choice

**Files:**
- Modify: `src/Framejet.Platform/Video/FrameRenderer.cs` (`Draw`, `RenderToBgra`, `Dispose`, plus a point sampler)
- Modify: `src/Framejet.Platform/Video/D3D11Presenter.cs` (a `Scaling` property and its use at the `_renderer.Draw` call, about line 345)
- Modify: `src/Framejet.Platform/Video/DirectShowVideoBackend.cs` (a `Scaling` property mirrored like `VSync`, and passed when the presenter is created, about line 199)
- Test: `tests/Framejet.Platform.Tests/Video/FrameRendererTests.cs`

**Interfaces:**
- Consumes: `ScalingMode`, `ScaleFilter`, `Scaling.Place`, `Scaling.Filter` (Task 1).
- Produces:
  - `FrameRenderer.Draw(RenderTargetView target, int targetWidth, int targetHeight, ScalingMode scaling)`
  - `internal byte[] RenderToBgra(byte[] frame, int targetWidth, int targetHeight, ScalingMode scaling = ScalingMode.Fill)`
  - `D3D11Presenter.Scaling { get; set; }` (a `ScalingMode` held in a `volatile` field, read once per draw)
  - `DirectShowVideoBackend.Scaling { get; set; }`

- [ ] **Step 1: Write the failing hardware tests** in `FrameRendererTests.cs`. Extend the `Render` helper with a `ScalingMode` argument that defaults to `Fill`.

```csharp
[SkippableFact, Trait("Category", "Hardware")] public void One_to_one_crops_the_centre_when_too_big()
{ var f = new CaptureFormat(640, 480, GrabberFormat.Yuy2, false); // left half white, right half black
  var px = Render(f, SyntheticFrames.SplitLeftRight(f, 235, 16), 320, 480, ScalingMode.OneToOne); // shows source x 160..479
  AssertPixel(px, 320, 100, 240, 255, 255, 255); AssertPixel(px, 320, 220, 240, 0, 0, 0); }
[SkippableFact, Trait("Category", "Hardware")] public void Crisp_doubles_without_blending_the_edge()
{ var f = new CaptureFormat(640, 480, GrabberFormat.Yuy2, false);
  var px = Render(f, SyntheticFrames.SplitLeftRight(f, 235, 16), 1280, 960, ScalingMode.WholeStepsCrisp);
  AssertPixel(px, 1280, 639, 480, 255, 255, 255); AssertPixel(px, 1280, 640, 480, 0, 0, 0); }
[SkippableFact, Trait("Category", "Hardware")] public void Smooth_doubles_with_a_blended_edge()
{ var f = new CaptureFormat(640, 480, GrabberFormat.Yuy2, false);
  var px = Render(f, SyntheticFrames.SplitLeftRight(f, 235, 16), 1280, 960, ScalingMode.WholeStepsSmooth);
  int i = (480 * 1280 + 640) * 4; Assert.InRange(px[i], 20, 235); } // Catmull-Rom puts ~20% of the white side here
[SkippableFact, Trait("Category", "Hardware")] public void Whole_steps_leaves_black_bars()
{ var f = new CaptureFormat(640, 480, GrabberFormat.Yuy2, false);
  var px = Render(f, SyntheticFrames.Solid(f, 235, 128, 128), 1000, 600, ScalingMode.WholeStepsCrisp); // 1× at (180, 60)
  AssertPixel(px, 1000, 100, 300, 0, 0, 0); AssertPixel(px, 1000, 500, 300, 255, 255, 255); }
```

- [ ] **Step 2: Run the tests and check they fail**

Run: `dotnet test tests\Framejet.Platform.Tests --filter "FullyQualifiedName~FrameRendererTests"`
Expected: a build failure, because `Render` has no scaling argument and `RenderToBgra` has no overload.

- [ ] **Step 3: Implement**

- In `FrameRenderer.Draw`:
  - Replace `Letterbox.Fit` with `Scaling.Place(w, h, targetWidth, targetHeight, scaling)`.
  - Choose the pass-2 shader and sampler from `Scaling.Filter`: `Point` uses `_bilinear` with a new `_pointClamp` sampler (`Filter.MinMagMipPoint`, clamp, created next to `_linearClamp`); `CatmullRom` and `Bilinear` stay as today with `_linearClamp`.
  - Bind the sampler for pass 2 only. Pass 1 keeps `_linearClamp`.
  - The negative viewport origin from 1:1 is legal in D3D11 (the minimum is −32768), so pass it through unchanged.
- The presenter reads `_scaling` once per draw: `_renderer.Draw(_backBufferView, _bufferWidth, _bufferHeight, _scaling)`.
- `DirectShowVideoBackend.Scaling` mirrors the `VSync` property pattern, including the presenter initializer.

- [ ] **Step 4: Run the tests and check they pass**

Run: `dotnet test tests\Framejet.Platform.Tests --filter "FullyQualifiedName~FrameRendererTests"`
Expected: all pass, including the 5 existing tests. Check the run didn't skip: the GPU tests need only a D3D11 device, not the card.

- [ ] **Step 5: Commit** `feat: draw the picture by the Scaling choice`

---

### Task 4: The toolbar, menu, MainForm wiring and self-test

**Files:**
- Modify: `src/Framejet/MainForm.cs`, `src/Framejet/VideoMenu.cs`, `src/Framejet/ToolbarWindow.cs`, `src/Framejet/SelfTestRunner.cs`

**Interfaces:**
- Consumes: `AppSettings.Scaling`, `UiText.ScalingMenu`, `UiText.ScalingLabel`, `Scaling.All` (Task 2), `DirectShowVideoBackend.Scaling` (Task 3), and `StatusText.Format`/`Scale` with a scaling argument.
- Produces: `MainForm.SetScaling(ScalingMode mode)`, which writes `Settings.Scaling`, sets `VideoBackend.Scaling`, calls `SaveSettings()` and then `_toolbar?.RefreshContent()`. It does nothing after close.

- [ ] **Step 1: Implement**

- **MainForm:**
  - `StartDevices` creates the backend with `Scaling = Settings.Scaling`.
  - `PictureScale`, `PictureStatus` and `UpdateStatus` pass `Settings.Scaling` to `StatusText`.
- **VideoMenu:** a `Scaling` submenu (title `UiText.ScalingMenu`) right after `Modes(form)`. It has one checked item per `Scaling.All`, labelled with `UiText.ScalingLabel`, and clicking an item calls `form.SetScaling(mode)`.
- **ToolbarWindow:**
  - A `_scaling` `ComboBox` from `Combo(...)`, placed in `_items` right after `_modes`, with a width that fits "Whole steps (smooth)" (the builder's call), and styled and wired like `_modes`.
  - Fill it in `RefreshVideo` from `Scaling.All`, with the current choice selected. On `SelectionChangeCommitted` it calls `_owner.SetScaling`, guarded by `_refreshing` like `OnModeCommitted`.
  - Tooltip: "Scaling".
- **SelfTestRunner.Start:** set `_form.Settings.Scaling = ScalingMode.Fill` next to `VSync = true`.

- [ ] **Step 2: Build both configurations**

Run: `dotnet build Framejet.sln -c Debug; dotnet build Framejet.sln -c Release`
Expected: both succeed with 0 warnings.

- [ ] **Step 3: Run Core tests**

Run: `dotnet test tests\Framejet.Core.Tests`
Expected: all pass.

- [ ] **Step 4: Commit** `feat: Scaling dropdown in the toolbar and right-click menu`

---

### Task 5: Docs and an end-to-end check

**Files:**
- Modify: `docs/design.md` (§4.2 "Scaling", the §4.6 toolbar line and the right-click menu list)

- [ ] **Step 1: Update `docs/design.md`**

  - **§4.2 Scaling:** add one sentence: the player picks Fill, Whole steps (crisp/smooth) or 1:1 **(owner)**, with full detail in `docs/superpowers/specs/2026-11-16-scaling-choice-design.md`.
  - **§4.6 toolbar order:** `video device ▾ · mode ▾ · scaling ▾ · rescan ⟳ | …`.
  - **§4.6 menu list:** add `Scaling ▸` after `Mode ▸`.

- [ ] **Step 2: Run the self-test** (close Framejet and don't run the Platform tests at the same time; use a scratch settings folder)

Run: `Framejet.exe --selftest report.txt --settings <scratch>\settings.ini --seconds 4`, using the Release build.
Expected: exit code 0 and `result: PASS`, and the report's menu line includes `Scaling`.

- [ ] **Step 3: Check by hand on the card**

  - Pick each option in a window and in fullscreen, and check the status scale matches: 1080p fullscreen on the 1440p screen reads "133%" in Fill and "1:1" with a border in Whole steps.
  - Maximize a window with 1440p and 1:1, and check it reads "1:1" and is cropped top and bottom.
  - Restart, and check the choice is remembered.

- [ ] **Step 4: Commit** `docs: Scaling choice in the design`
