# Scaling choice — design

Short design for a small change. Marked **(owner)** where the owner decided; **(Claude's call)** otherwise.

## Intent

A maximized 1440p window on the 1440p screen reads "95%" and looks soft, and 1080p fullscreen there is a soft "133%". Players should be able to choose how the picture is scaled, because people differ on sharp-with-black-bars versus filling the screen, and the owner doesn't know their own preference yet **(owner: "just give the choice")**.

## Goals, non-goals, anti-goals

- Goal: a **Scaling** choice with four options, saved like V-Sync, applying the same way windowed and fullscreen **(owner)**.
- Non-goal: a "fit window to 1:1" action. It can't help the case that prompted this: a 1440p picture needs a 1440-px-tall client area, and the 1440p screen's working area leaves about 1360 **(owner, dropped in favour of the choice)**.
- Non-goal: halving a picture that is slightly too big (1440p → 720p in a maximized window) **(owner)**.
- Anti-goals (§1): no added delay, no extra GPU pass or copy, and the status must keep telling the truth ("1:1" only when it really is).

## The choices (owner)

```
Scaling ▾
  ✓ Fill                   fills the window at any size (today's behaviour, the default)
    Whole steps (crisp)    1×, 2×, 3×… with black bars; each pixel drawn as a clean block
    Whole steps (smooth)   1×, 2×, 3×… with black bars; Catmull-Rom like Fill
    1:1                    always pixel for pixel with black bars; crops the edges if too big
```

- **Whole steps, when even 1× doesn't fit** (1440p in a maximized window): shrink to fit, as Fill does, and read "95%". You never lose any of the picture **(owner, option A)**.
- **1:1, when the picture is bigger than the area:** centred at frame size, and the overflow is cut off equally on each side **(owner, option C)**.
- **At 1× every choice** draws exactly as today's 1:1 path does.
- **Where:** a toolbar dropdown right after the mode dropdown (device ▾ · mode ▾ · scaling ▾ · ⟳), and a "Scaling ▸" submenu in the right-click menu after "Mode ▸", so it works in fullscreen **(owner)**. The toolbar dropdown's closed label is the builder's call (§9), but it must fit the toolbar.
- **Status:** unchanged format. It follows the rectangle actually drawn: "1:1", or the percentage (for example "200%" for 720p crisp at 2×). For a cropped 1:1 picture it reads "1:1" **(Claude's call)**.

## Approach

Considered:
1. **One function decides the picture's rectangle from the choice; the renderer and the status both use it (chosen).** It's `Letterbox.Fit` extended with the scaling choice, and a rectangle that is larger than the area means crop: the GPU clips the viewport to the render target. Same draw, same passes, no added delay.
2. Resizing the window or swap chain to the picture: this fights maximize and fullscreen, and can't crop.
3. A separate scaling pass: extra GPU work for nothing.

## Constraints and calls (Claude's calls)

- **Pure logic in Core:** a `ScalingMode` enum (`Fill`, `WholeStepsCrisp`, `WholeStepsSmooth`, `OneToOne`) and the rectangle function sit next to `Letterbox`, with xUnit tests covering at least: 1440p in 2560×1360 for each mode, 1080p in 2560×1440 (Fill 133%, whole steps 1:1 centred), 720p in 2560×1440 (whole steps 2× = 2560×1440), and 1:1 crop offsets that go negative and are centred. `StatusText.Scale` takes the mode.
- **Shader:** the crisp option at 2× and above samples point (nearest). Smooth and Fill upscale with Catmull-Rom. At 1× and when downscaling, all modes stay bilinear, as now.
- **Render thread:** the choice reaches the presenter as a property like `VSync`, read once per frame. A change applies from the next frame (the card sends frames continuously, so within about 17 ms at 60 fps); nothing is drawn while minimized anyway.
- **Setting:** `scaling=fill|crisp|smooth|1to1` in `settings.ini`, default `fill`. An unknown value means `fill`. It's saved on pick.
- **Self-test:** forces Fill in memory, like V-Sync, so the lag gate is unchanged. The report's scale line stays.
- **Docs:** `docs/design.md` §4.2 "Scaling" and §4.6 (toolbar layout, menu list) get updated to point here.

## Left to the builder

- Control names, the toolbar dropdown's width and label, and the tooltip text.
- How the presenter switches sampler or shader per mode.
