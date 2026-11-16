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

## Revision after measuring (2026-11-16): approach 2, with a bounded wait

Approach 1 failed its measuring gate. A per-frame trace on the 60 Hz screen found the real cause, and the owner approved building the fix below ("Yes, build it"). The traces and numbers are in the session notes, not in the repo.

**Cause (measured).** The card sends 59.940 fps and the screen refreshes at 60.000 Hz. A frame has to reach the display about 3–4 ms before a refresh to be shown at it (the display's latch deadline). When one frame misses that deadline, because of arrival jitter, a slow Present or an OS hiccup, it stays queued. Every later frame then queues behind it, one refresh late, and the render thread blocks in Present. Nothing drains that queue until the beat between the two rates (about 16.7 s) leaves a refresh with nothing new to show. `Present(0)` without tearing never replaces a queued frame in direct scanout; it blocks just like `Present(1)`. That's why approach 1 changed nothing. At 144 and 165 Hz a refresh with nothing new follows every frame, so the queue drains at once.

**Fix.** The swap chain gets a frame-latency waitable object (`DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT`, maximum frame latency 1). It signals when the previously queued frame leaves the queue. Before each present:
- **V-Sync On, default:**
  - Wait up to **4 ms** for the object. If it signals, `Present(1)`.
  - If it doesn't, skip this frame and count it as dropped. The queued frame shows at the next refresh, and the following frame lands on time, so the backlog is gone.
  - A frame within 4 ms of the refresh it missed would land a refresh late anyway, whether it waits or skips. Waiting keeps the picture at 60 fps instead of juddering at 30 fps; skipping on every busy frame did judder (**Claude's call, from measurement**).
- **V-Sync On, never skip:** wait (bounded at 1 s), then `Present(1)`. This is the old behaviour.
- **V-Sync Off:** wait (bounded at 1 s), then present as before (`Present(0, ALLOW_TEARING)` when supported).
- **Never present without a slot (Claude's call, after review).**
  - A wait that times out skips the frame in every mode. A failed Present keeps its slot for the next frame. That way every present is matched by exactly one slot taken from the waitable object.
  - The object is a counting semaphore with no cap (measured): presenting without a slot would leave a surplus that lifts the latency limit for the rest of the stream.
  - If no slot comes for 1 s while frames keep arriving, Framejet recreates the swap chain, which resets the object, and logs it.

**Measured with a throwaway version, 60 Hz fullscreen, 15 s runs.**
- Arrival → screen: 12.1–13.2 ms mean, 18.9–20.7 p95. Before, a run was either ~14 or ~30+ ms.
- 1–5% of frames skipped, at most 25% in any 1 s window.
- Windowed (composed) never skipped.
- 144 and 165 Hz were no worse.

**Measurement (Claude's call).**
- Arrival → Present measures Framejet's own work: arrival to the start of the queue wait.
- The queue wait is reported separately as its own timing.
- Present → screen is measured from the Present call.

**Must-pass 9's mean bar (Claude's call; the owner can object).**
- The old mean bar, half a refresh + 1.5 ms, assumed no latch deadline. It can't be met at 60 Hz: the ~3–4 ms deadline alone adds that much to the mean.
- The mean bar becomes **half a refresh + 5.5 ms**: 13.8 ms at 60 Hz, 8.5 ms at 165 Hz.
  - That is the measured ~4 ms latch deadline plus the old bar's 1.5 ms slack. With a deadline D, frames presented less than D before a refresh show one refresh later, so the expected mean is exactly half a refresh + D.
  - A first try at half a refresh + 4 ms, with no slack, was missed by 0.1 ms in 2 of 3 runs (12.44 and 12.46 ms). A 15 s run covers less than one 16.7 s beat, so the phases it samples vary.
  - The old 30 ms backlog still fails this bar by a wide margin.
- The p95 bar is unchanged (one refresh + 1.5 ms), and it still catches a frame waiting an extra refresh.

**Known risk, not fixed here.** In a few runs, a skip was followed by a display stall: 33 ms usually, once ~120 ms. The flip really didn't retire, and the presentation mode didn't change. This happened about once per 30 s at 60 Hz. The suspected cause is adaptive sync on that monitor, which is unconfirmed. Never skip frames avoids it. Turning adaptive sync off for the monitor would confirm or rule it out.

**Separate finding (follow-up, not this work).** Over 15 s runs at 144 Hz, Present → screen p95 is ~10.4 ms even with the original presenter, against an 8.44 ms bar. The earlier 4 s pass was luck.

Approach 1's code (`Present(0)` for V-Sync On and the replaced-frame count) is removed. The setting, menu and docs parts of the design stand.
