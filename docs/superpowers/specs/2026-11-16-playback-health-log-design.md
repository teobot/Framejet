# Playback health log (dev builds) — design

## Intent

The owner plays for an hour or so on a dev build and won't always notice or note a problem. Framejet should keep its own
record, so that a stutter, a dropped burst or an audio glitch leaves a timestamped trace in the log to find and fix
afterwards. QA by data, not by memory.

## Goals, non-goals, anti-goals

- **Goal:** every bad second is in the log within about a second of it happening, with its numbers; each minute has a
  summary; what the player was doing (fullscreen, which screen, minimised, settings changed) is in the same timeline.
- **Goal:** an hour of play, and a relaunch or two, lose nothing.
- **Non-goal:** precise latency measurement. `MeasureLatency` stays off: it wakes the render thread every 1 ms and
  would change what is being tested.
- **Anti-goal:** the monitoring itself causing hiccups. No per-second allocation-heavy reads (`GetStats` allocates
  ~150 KB and the GCs pause the render thread), nothing on the render or capture threads.
- **Anti-goal (the owner's):** any of this in a published release. It runs only in dev builds.

## Approaches

1. **In-app health lines in the existing log (picked).** One timeline with the events already logged; no new file or
   tool.
2. A separate CSV of per-second counters. Easier to chart, but splits the timeline from the events; more to build.
3. Play now on what is logged today. The existing minute line hides short bursts in running totals; the owner chose to
   wait for the build.

## Design

- **Dev builds only:** on when `Application.ProductVersion` parses as an `AppVersion` with `IsDev` (`0.0.0-dev`, any
  unstamped build, Debug or Release). Published releases log exactly as before. Gated on the version rather than
  `#if DEBUG`, so the owner can play on an optimised Release build **(Claude's call)**. Off in the self-test, like the
  existing stats timer.
- **Cheap counters:** `D3D11Presenter.GetCounters()` returns arrived, drawn, dropped, device resets and the draw rate
  without copying the timing arrays; `WasapiAudioBackend.GetCounters()` returns running, trims and underruns. Both are
  cumulative.
- **`Core/Diagnostics/HealthMonitor`** (pure, unit-tested) takes a sample every second on the UI thread and returns a
  `hiccup:` line, or null. A second is judged only when it and the one before had video running and not paused
  (minimised). It is flagged when:
  - the card delivered under 90% of the mode's frame rate for the time elapsed;
  - more than 10% of the arrived frames were dropped (1–5% skipped is normal at 60 Hz) **(Claude's call)**;
  - the Direct3D device was reset;
  - audio ran and trimmed or underran;
  - the tick came more than 2 s after the last (the UI thread stalled).
  A counter that goes down means the backend restarted: the new value is the delta.
- **Minute line:** in dev builds the existing `stats:` line becomes per-minute: arrived, drawn, dropped (and %),
  lowest fps, hiccups and resets in the minute; arrival→present and queue-wait p95; audio buffer, max, trims and
  underruns; process memory and GC counts.
- **Context lines (dev builds):** fullscreen on/off and the screen moved to (device name, size, display-mode refresh),
  minimise/restore, and V-Sync, Never skip frames and Scaling changes.
- **Log retention (dev builds):** keep the last 5 sessions (`Framejet.old.log`, `Framejet.old2.log` …
  `Framejet.old4.log`) and allow 8 MB per log, instead of 2 sessions and 512 KB. `FileLog` takes both as options;
  release defaults are unchanged.
- **Docs:** `docs/design.md` (logs) and `CLAUDE.md` get a line on dev builds. The README describes releases and is
  unchanged.

## Left to the builder

Exact wording of the lines; where the per-second sampling lives in `MainForm`; test names.
