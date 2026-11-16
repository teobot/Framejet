# 60 Hz V-Sync latency fix — design

A small change to the presenter (`D3D11Presenter`), a setting, and a right-click menu item. It closes the known limitation in `docs/design.md` §6 must-pass 9.

## Intent

On a 60 Hz screen with V-Sync On, a ~60 fps source often runs ~30 ms late: each frame waits behind the previous one instead of showing at the next refresh. Lag is the top anti-goal, so V-Sync On should show the newest frame at the next refresh, even if that means an occasional frame is never shown **(owner)**. The owner dislikes choppiness, so skipping frames can be turned off **(owner)**.

## Cause (Claude's theory, to be confirmed by measuring first)

V-Sync On is `Present(1)` with a frame-latency limit of 1. A frame presented while the previous one still waits for its refresh is shown one refresh later. A 59.94 fps source against a ~60 Hz refresh drifts so slowly that this backlog persists, adding about one refresh (~16.7 ms) on top of the normal wait. At 144 and 165 Hz each frame spans more than two refreshes, so the backlog always clears.

## Goals, non-goals, anti-goals

- **Goal:** with V-Sync On and the default setting, a ~60 fps source on a 60 Hz screen meets must-pass 9: Present → screen mean ≤ half a refresh + 1.5 ms (9.8 ms at 60 Hz) and p95 ≤ one refresh + 1.5 ms (18.2 ms).
- **Goal:** V-Sync On still never tears (must-pass 8).
- **Goal:** the 144 and 165 Hz results don't get worse.
- **Non-goal:** changing V-Sync Off, the refresh rate, or VRR.
- **Anti-goal:** visible choppiness for someone who turned skipping off. With the option on, behaviour is exactly today's.

## Approaches

1. **"Newest frame wins" (picked by the owner).** With V-Sync On, present so a newer frame replaces one still waiting for its refresh, instead of queueing behind it. With the flip-model swap chain, the candidate is `Present(0)` *without* `DXGI_PRESENT_ALLOW_TEARING`: DWM shows the latest frame at the next refresh and discards the older one, without tearing. It is small and stays in the presenter. Risk: when the picture is in direct scanout (independent flip/overlay), Windows may treat sync interval 0 differently. That has to be measured on the real screen before anything else gets built.
2. **Detect and skip (fallback).** Keep `Present(1)`. Watch DXGI frame statistics, and when a frame is still queued as the next arrives, skip one frame to drain the backlog. It works whatever Windows does with sync interval 0, but it needs the statistics running all the time and is more code. Use it only if approach 1's measurement fails.

## Design

- **Presenting:** V-Sync On, with skipping allowed (the default): newest frame wins (approach 1), at every refresh rate. V-Sync On with skipping off: `Present(1)`, as today. V-Sync Off: unchanged.
- **Setting (Claude's call):** `AppSettings.NeverSkipFrames`, default `false`, key `vsync.neverskip` in `settings.ini`. It's passed through `DirectShowVideoBackend` to `D3D11Presenter` the same way `VSync` is, and can change live.
- **Menu (Claude's call):** a checkable right-click menu item **"Never skip frames"**, directly under ✓ V-Sync. It is disabled (greyed) while V-Sync is off, because it has no effect then. It doesn't appear on the toolbar.
- **Counting:** a frame replaced before it reached the screen counts as dropped, as other dropped frames do, so the self-test's dropped count shows the cost.
- **Docs:** once measured, update `docs/design.md` (§4.2 V-Sync On, §4.6 menu, §4.8 remembered settings, §6 must-pass 9 to drop the known limitation), `CLAUDE.md`'s known-limitation line, and the README's "Late picture on a 60 Hz screen" section.

## Verification

1. **Measure first:** on the 60 Hz screen, run the self-test with today's code to record the backlog. Then run it with approach 1, windowed and fullscreen, checking the Present → screen bars above, that nothing tears, and the presentation mode (overlay or composed).
2. Re-run the self-test on the 165 and 144 Hz screens: must-pass 9 still passes, with no worse numbers.
3. With "Never skip frames" on, the 60 Hz numbers match today's.
4. If approach 1 fails step 1 (for example, it backlogs or tears in direct scanout), stop and switch to approach 2. The setting and menu stay the same.

## Left to the builder

- Whether a frame was replaced can be inferred from DXGI present/frame-statistics counts; how exactly is the builder's call.
- Whether `Present(0)` blocks under frame-latency 1; adjust the latency limit only if measurement shows it's needed.
- Tests: a Core test for the setting's load and save; a hardware test on the presenter where practical.

## Coordination

Other sessions are also changing `D3D11Presenter`: live latency display, Vortice in place of SharpDX, and precompiled shaders. Expect merge conflicts in that file. The present call is the only line this change needs there, plus the new property.
