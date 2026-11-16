# Update pill — design

Short design for a small change. Marked **(owner)** where the owner decided; **(Claude's call)** otherwise.

## Intent

Anyone running a downloaded copy of Framejet should find out when a newer release is out, and get to it in one click **(owner)**.

## Goals, non-goals, anti-goals

- Goal: when GitHub's latest release is newer than the running copy, a pill in the toolbar says so; clicking it opens that release's page in the browser **(owner)**.
- Non-goal: downloading or replacing the exe. Auto-update stays a non-goal (`docs/design.md` §2) **(owner: "pill just opens the release page")**.
- Anti-goals (from §1): the check must never add lag, stutter or startup delay, and must never show an error to the player.

## Behaviour (Claude's calls unless marked)

- **When:** once per launch, about 10 s after start, off the UI thread. The result is posted back to the UI thread.
- **Show the pill only if** the latest release's version is newer than the running version **and** the release already has `Framejet.exe` attached (the release workflow attaches it about a minute after publishing; v1.2.0 took 64 s).
- **Where:** a pill at the right end of the toolbar, after the status text, reading "Update to 1.2.0", tooltip "Open the Framejet 1.2.0 release page". Visuals beyond that are the builder's (§9).

  ```
  [Ripsaw X ▾] [1440p60 ▾] [⟳] │ [Default output ▾] [🔈] [━━●──] │ [V-Sync] [⛶]   1440p · 59.9 fps  (Update to 1.2.0)
  ```

- **It does not bring the toolbar up** (unlike warnings): a visible toolbar can take the picture off direct scanout. It shows whenever the toolbar next shows.
- **Fullscreen:** the toolbar never shows there, so the right-click menu also gets an enabled "Update to 1.2.0…" item under the version line, opening the same page.
- **Not dismissible;** it disappears once the running copy is current.
- **Failures** (offline, timeout, rate limit, bad response): no pill, no status text, one log line.
- **Version comparison:** SemVer-style on `major.minor.patch[.n]` with a pre-release suffix sorting below the plain version (1.2.0-rc.1 < 1.2.0). A running version newer than the latest shows nothing. A leading `v` on the tag is ignored; `+metadata` is ignored.
- **Dev builds:** local and "Run workflow" builds get version `0.0.0-dev` (today they say 1.0.0, which would always show the pill). Any version with a `-dev` suffix skips the check. Release builds keep their tag version.
- **Self-test** never checks.
- **Opt-out:** `CheckForUpdates=false` in `settings.ini` turns the check off (default on, no UI). The README says Framejet asks GitHub for the latest release on start, and how to turn it off.

## Approaches

1. **GitHub REST API `GET /repos/teobot/Framejet/releases/latest`** — chosen. Documented; skips drafts and pre-releases; returns `tag_name`, `html_url` and the attached files, so it can wait for `Framejet.exe`. Unauthenticated limit is 60 requests/hour per IP, far above one per launch. Needs a `User-Agent` header (a generic `Framejet/<version>`).
2. Follow the website's `/releases/latest` redirect and read the tag from the `Location` header — no JSON, no rate limit, but undocumented and can't tell whether the exe is attached yet.
3. A version file in the repo or as a release asset — one more thing to update on every release, and it can drift.

## Constraints

- .NET Framework 4.8, single exe: no new third-party dlls. Use framework assemblies only (`HttpWebRequest`/`HttpClient`, `DataContractJsonSerializer` or equivalent). TLS 1.2+ comes from the OS default on net48.
- Layering: version parsing, comparison, release-JSON reading and the show/hide decision go in **Core** with xUnit tests; the HTTP fetch goes in **Platform**; the pill, menu item and wiring go in the **Framejet** app (`ToolbarWindow`, `VideoMenu`, `MainForm`). Copy goes in `UiText`.
- Opening the page uses the shell (`Process.Start` with the release's `html_url`).
- `docs/design.md` gains a section for this behaviour; `CLAUDE.md` gets a line if a new type or trap is worth naming.

## Left to the builder

- Exact type names, the JSON reader, timeouts (a short one, around 10 s).
- Pill styling within the dark theme, and how the toolbar layout makes room for it.
- Whether a skippable test hits the real API.
