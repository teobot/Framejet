# Update Pill Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When GitHub's latest Framejet release is newer than the running copy and has `Framejet.exe` attached, show an "Update to X" pill in the toolbar and an "Update to X…" item in the right-click menu; both open the release page.

**Architecture:** Pure logic in Core (`Framejet.Core.Updates`: version parsing and comparison, release JSON reading, the check/show decision). Platform does one HTTPS GET. The app (`MainForm`) schedules the check 10 s after start, runs it on the thread pool, posts the result to the UI thread, and the toolbar and menu show it.

**Tech Stack:** C# 9, .NET Framework 4.8, WinForms, xUnit 2.4.2, Xunit.SkippableFact. Framework assemblies only: `HttpWebRequest` (System.dll), `DataContractJsonSerializer` (System.Runtime.Serialization).

**Spec:** `docs/superpowers/specs/2026-11-16-update-pill-design.md`

## Global Constraints

- Single exe: no new NuGet packages or copy-local dlls. Framework references only.
- No Core, Platform or third-party types in `Program`'s fields or `Main`.
- The check never runs on the UI thread, never in the self-test, never for a `-dev` version, never when `update.check=false`.
- Any failure: no pill, no status text, exactly one `Log.Error` line.
- The pill never brings the toolbar up (do not call `_autoHide.WarningRaised`).
- API URL: `https://api.github.com/repos/teobot/Framejet/releases/latest`. Required asset: `Framejet.exe`. Delay 10 s, timeout 10 s.
- Only open a page whose URL starts with `https://github.com/teobot/Framejet/releases/`.
- Copy (exact): pill `Update to 1.2.0`; tooltip `Open the Framejet 1.2.0 release page`; menu `Update to 1.2.0…`.
- Settings key `update.check` (true/false, default true), matching the file's lowercase dotted keys. (The design doc said `CheckForUpdates`; this plan's name wins and Task 4 updates the design doc.)
- Unstamped builds are version `0.0.0-dev`.
- In xunit 2.4.2 `Assert.Equal(float, float, int)` is ambiguous; cast to `double`.
- Commits are GPG-signed; never bypass. Pass messages with `git commit -F <file>`. End each message with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **GitHub answers with something other than a release** (rate-limit 403 JSON `{"message":"API rate limit exceeded…"}`, an HTML error page, an empty body): no pill, one log line. Pinned in Task 2 (`Parse_rejects_a_body_without_a_tag`, `Parse_rejects_non_json`) and Task 4 (catch-all around fetch + parse).
2. **The latest tag isn't version-shaped** (`latest`, `Framejet 1.0.0`): no pill. Pinned in Task 2 (`No_offer_for_an_unparseable_tag`).
3. **The running copy is ahead of or equal to the latest** (a pre-release or a local build): no pill. Pinned in Tasks 1 and 2.
4. **The window closes while the check is in flight:** the result is dropped without an exception. `ControlDispatcher.Post` already drops posts once the handle is gone; Task 4's `ApplyUpdate` also returns when `_closed`, and `OnFormClosed` disposes the pending timer.
5. **A narrow window:** the pill keeps its width and the status text ellipsises first. Checked by hand in Task 4.

---

### Task 1: AppVersion

**Files:**
- Create: `src/Framejet.Core/Updates/AppVersion.cs`
- Test: `tests/Framejet.Core.Tests/Updates/AppVersionTests.cs`

**Interfaces:**
- Produces: `public sealed class AppVersion : IComparable<AppVersion>` in namespace `Framejet.Core.Updates` with
  - `public static bool TryParse(string text, out AppVersion version)`
  - `public string Prerelease { get; }` (`""` when none)
  - `public bool IsDev { get; }` (first pre-release identifier is `dev`, case-insensitive)
  - `public int CompareTo(AppVersion other)`
  - `public override string ToString()` (normalised: no `v`, no `+metadata`, numbers as given, `-pre` kept)

- [ ] **Step 1: Write the failing tests**

```csharp
[Theory]
[InlineData("1.2.0", "1.2.0")]
[InlineData("v1.2.0", "1.2.0")]
[InlineData("1.2.0+8c63b00", "1.2.0")]
[InlineData(" 1.2.0-rc.1 ", "1.2.0-rc.1")]
[InlineData("1.2", "1.2")]
[InlineData("1.2.0.4", "1.2.0.4")]
public void Parses_release_tags_and_product_versions(string text, string shown)
{ Assert.True(AppVersion.TryParse(text, out var v)); Assert.Equal(shown, v.ToString()); }

[Theory]
[InlineData(null)] [InlineData("")] [InlineData("latest")] [InlineData("Framejet 1.0.0")] [InlineData("1")] [InlineData("1.2.3.4.5")] [InlineData("1.x.0")]
public void Rejects_non_versions(string text) => Assert.False(AppVersion.TryParse(text, out _));

[Theory]
[InlineData("1.1.0", "1.2.0")]
[InlineData("1.2.0-rc.1", "1.2.0")]
[InlineData("1.2.0-rc.1", "1.2.0-rc.2")]
[InlineData("1.2.0-rc.2", "1.2.0-rc.10")]
[InlineData("1.2.0-1", "1.2.0-alpha")]
[InlineData("1.2.0-rc", "1.2.0-rc.1")]
[InlineData("1.9.0", "1.10.0")]
[InlineData("1.2", "1.2.1")]
[InlineData("0.0.0-dev", "1.0.0")]
public void Orders_lower_before_higher(string lower, string higher)
{
    AppVersion.TryParse(lower, out var a); AppVersion.TryParse(higher, out var b);
    Assert.True(a.CompareTo(b) < 0); Assert.True(b.CompareTo(a) > 0);
}

[Theory]
[InlineData("1.2", "1.2.0")] [InlineData("v1.2.0", "1.2.0+abc")] [InlineData("1.2.0", "1.2.0.0")]
public void Equal_versions_compare_equal(string a, string b)
{ AppVersion.TryParse(a, out var x); AppVersion.TryParse(b, out var y); Assert.Equal(0, x.CompareTo(y)); }

[Theory]
[InlineData("0.0.0-dev", true)] [InlineData("1.2.0-DEV.3", true)] [InlineData("1.2.0", false)] [InlineData("1.2.0-rc.1", false)] [InlineData("1.2.0-devel", false)]
public void Knows_dev_builds(string text, bool dev) { AppVersion.TryParse(text, out var v); Assert.Equal(dev, v.IsDev); }
```

- [ ] **Step 2: Run them and see them fail**

Run: `dotnet test tests\Framejet.Core.Tests --filter "FullyQualifiedName~AppVersionTests"`
Expected: build error, `AppVersion` not found.

- [ ] **Step 3: Implement `AppVersion`**

Grammar: optional leading `v`/`V`, 2–4 dot-separated non-negative integers, optional `-` pre-release of dot-separated `[0-9A-Za-z-]+` identifiers, optional `+metadata` (dropped). Trim whitespace first. Missing numeric parts compare as 0. Pre-release precedence (SemVer 2.0 §11): no pre-release > any pre-release; identifiers compared left to right, numeric ones numerically, numeric < alphanumeric, alphanumeric ordinal; a shorter list that is a prefix sorts lower.

- [ ] **Step 4: Run them and see them pass**

Run: `dotnet test tests\Framejet.Core.Tests --filter "FullyQualifiedName~AppVersionTests"`
Expected: all pass.

- [ ] **Step 5: Commit** — `feat(core): parse and compare app versions`

---

### Task 2: Release reading, the update decision, copy and the setting

**Files:**
- Create: `src/Framejet.Core/Updates/ReleaseInfo.cs`, `src/Framejet.Core/Updates/UpdateCheck.cs`
- Modify: `src/Framejet.Core/Framejet.Core.csproj` (add `<Reference Include="System.Runtime.Serialization" />`), `src/Framejet.Core/Ui/UiText.cs`, `src/Framejet.Core/Settings/AppSettings.cs`, `src/Framejet.Core/Settings/SettingsStore.cs`
- Test: `tests/Framejet.Core.Tests/Updates/UpdateCheckTests.cs`, `tests/Framejet.Core.Tests/Settings/SettingsStoreTests.cs`

**Interfaces:**
- Consumes: `AppVersion` (Task 1).
- Produces (namespace `Framejet.Core.Updates`):
  - `public sealed class ReleaseInfo { string Tag; string PageUrl; IReadOnlyList<string> AssetNames; public static ReleaseInfo Parse(string json); }` — reads `tag_name`, `html_url`, `assets[].name`; throws `FormatException` for non-JSON or a missing/blank `tag_name`. Unknown fields ignored.
  - `public sealed class UpdateOffer { public string Version { get; } public string PageUrl { get; } }`
  - `public static class UpdateCheck` with
    - `public const string LatestReleaseApi = "https://api.github.com/repos/teobot/Framejet/releases/latest";`
    - `public const string ReleasePagePrefix = "https://github.com/teobot/Framejet/releases/";`
    - `public const string ExeAsset = "Framejet.exe";`
    - `public static readonly TimeSpan Delay = TimeSpan.FromSeconds(10), Timeout = TimeSpan.FromSeconds(10);`
    - `public static bool ShouldCheck(string runningVersion, bool enabled, bool selfTesting)`
    - `public static UpdateOffer Evaluate(string runningVersion, ReleaseInfo latest)` — null unless: both versions parse, latest > running, an asset equals `Framejet.exe` (ordinal, ignore case), and `PageUrl` starts with `ReleasePagePrefix` (ordinal).
    - `public static string UserAgent(string runningVersion)` — `"Framejet/" + normalised version`, or `"Framejet"` if it doesn't parse.
  - `UiText.UpdateTo(string version)` → `"Update to " + version`; `UiText.UpdateTooltip(string version)` → `"Open the Framejet " + version + " release page"`.
  - `AppSettings.CheckForUpdates` (`bool`, default `true`), stored as `update.check`.

- [ ] **Step 1: Write the failing tests**

```csharp
const string V120 = "{\"tag_name\":\"v1.2.0\",\"html_url\":\"https://github.com/teobot/Framejet/releases/tag/v1.2.0\",\"prerelease\":false," +
    "\"assets\":[{\"name\":\"DirectShowLib-source.zip\"},{\"name\":\"Framejet.exe\"},{\"name\":\"THIRD-PARTY-NOTICES.md\"}],\"author\":{\"login\":\"x\"}}";
static string NoExe => V120.Replace("Framejet.exe", "Other.exe");

[Fact] public void Parse_reads_tag_page_and_assets()
{ var r = ReleaseInfo.Parse(V120); Assert.Equal("v1.2.0", r.Tag); Assert.Equal("https://github.com/teobot/Framejet/releases/tag/v1.2.0", r.PageUrl); Assert.Contains("Framejet.exe", r.AssetNames); }
[Fact] public void Parse_rejects_a_body_without_a_tag()
    => Assert.Throws<FormatException>(() => ReleaseInfo.Parse("{\"message\":\"API rate limit exceeded\",\"documentation_url\":\"https://docs.github.com\"}"));
[Theory] [InlineData("<html>oops</html>")] [InlineData("")] public void Parse_rejects_non_json(string body) => Assert.Throws<FormatException>(() => ReleaseInfo.Parse(body));

[Fact] public void Offers_a_newer_release_with_its_exe()
{ var o = UpdateCheck.Evaluate("1.1.0", ReleaseInfo.Parse(V120)); Assert.Equal("1.2.0", o.Version); Assert.Equal("https://github.com/teobot/Framejet/releases/tag/v1.2.0", o.PageUrl); }
[Fact] public void Offers_the_release_to_its_own_release_candidate() => Assert.NotNull(UpdateCheck.Evaluate("1.2.0-rc.1", ReleaseInfo.Parse(V120)));
[Theory] [InlineData("1.2.0")] [InlineData("1.2.0+abc")] [InlineData("1.3.0-beta.1")] [InlineData("2.0.0")]
public void No_offer_when_current_or_ahead(string running) => Assert.Null(UpdateCheck.Evaluate(running, ReleaseInfo.Parse(V120)));
[Fact] public void No_offer_before_the_exe_is_attached() => Assert.Null(UpdateCheck.Evaluate("1.1.0", ReleaseInfo.Parse(NoExe)));
[Fact] public void Exe_name_match_ignores_case() => Assert.NotNull(UpdateCheck.Evaluate("1.1.0", ReleaseInfo.Parse(V120.Replace("Framejet.exe", "framejet.EXE"))));
[Fact] public void No_offer_for_an_unparseable_tag() => Assert.Null(UpdateCheck.Evaluate("1.1.0", ReleaseInfo.Parse(V120.Replace("\"v1.2.0\"", "\"latest\""))));
[Theory] [InlineData("https://evil.example/teobot/Framejet/releases/tag/v1.2.0")] [InlineData("http://github.com/teobot/Framejet/releases/tag/v1.2.0")] [InlineData("file:///C:/Windows/notepad.exe")]
public void No_offer_for_a_page_outside_the_releases(string url)
    => Assert.Null(UpdateCheck.Evaluate("1.1.0", ReleaseInfo.Parse(V120.Replace("https://github.com/teobot/Framejet/releases/tag/v1.2.0", url))));
[Fact] public void No_offer_without_a_release() => Assert.Null(UpdateCheck.Evaluate("1.1.0", null));

[Theory]
[InlineData("1.1.0", true, false, true)]
[InlineData("1.1.0", false, false, false)]
[InlineData("1.1.0", true, true, false)]
[InlineData("0.0.0-dev", true, false, false)]
[InlineData("not a version", true, false, false)]
[InlineData(null, true, false, false)]
public void Checks_only_real_versions_when_enabled_outside_the_self_test(string running, bool enabled, bool selfTesting, bool expected)
    => Assert.Equal(expected, UpdateCheck.ShouldCheck(running, enabled, selfTesting));

[Theory] [InlineData("1.2.0+abc", "Framejet/1.2.0")] [InlineData("junk", "Framejet")]
public void User_agent_names_only_the_app(string running, string expected) => Assert.Equal(expected, UpdateCheck.UserAgent(running));

[Fact] public void Update_copy_is_exact()
{ Assert.Equal("Update to 1.2.0", UiText.UpdateTo("1.2.0")); Assert.Equal("Open the Framejet 1.2.0 release page", UiText.UpdateTooltip("1.2.0")); }
```

In `SettingsStoreTests`: `Missing_file_gives_defaults` also asserts `Assert.True(s.CheckForUpdates)`; `Round_trips_every_field` sets `CheckForUpdates = false` and asserts `Assert.False(r.CheckForUpdates)`; `Tolerates_junk` adds the line `update.check=perhaps` and asserts `Assert.True(s.CheckForUpdates)`.

- [ ] **Step 2: Run them and see them fail**

Run: `dotnet test tests\Framejet.Core.Tests --filter "FullyQualifiedName~UpdateCheckTests|FullyQualifiedName~SettingsStoreTests"`
Expected: build errors for the missing types and members.

- [ ] **Step 3: Implement** the types and members above. `ReleaseInfo.Parse` uses `DataContractJsonSerializer` over private `[DataContract]` DTOs (`[DataMember(Name = "tag_name")]` etc.), wrapping `SerializationException`/`ArgumentException`/`XmlException` in `FormatException`. `UpdateOffer.Version` is `AppVersion.ToString()` of the tag. `SettingsStore`: `case "update.check": if (bool.TryParse(value, out var uc)) s.CheckForUpdates = uc; break;` and `Line("update.check", B(s.CheckForUpdates));` after `vsync`.

- [ ] **Step 4: Run the whole Core suite**

Run: `dotnet test tests\Framejet.Core.Tests`
Expected: all pass.

- [ ] **Step 5: Commit** — `feat(core): decide when to offer an update`

---

### Task 3: Unstamped builds say 0.0.0-dev

**Files:**
- Modify: `Directory.Build.props` (add `<Version>0.0.0-dev</Version>` to the PropertyGroup), `.github/workflows/release.yml:34-35` (fallback `$version = '0.0.0-dev'`, comment: "any other tag name, or Run workflow, leaves it at 0.0.0-dev"), `.github/ISSUE_TEMPLATE/bug_report.yml:9` (example stays `Framejet 1.2.0`).

- [ ] **Step 1: Build Release**

Run: `dotnet build Framejet.sln -c Release`
Expected: 0 warnings, 0 errors.

- [ ] **Step 2: Check the version**

Run (PowerShell): `(Get-Item src\Framejet\bin\Release\net48\Framejet.exe).VersionInfo.ProductVersion`
Expected: `0.0.0-dev`.

Run: `dotnet build Framejet.sln -c Release "-p:Version=1.3.0"` then the same command.
Expected: `1.3.0`.

- [ ] **Step 3: Commit** — `build: unstamped builds say 0.0.0-dev`

---

### Task 4: Fetch, wire up the pill and the menu item, document it

**Files:**
- Create: `src/Framejet.Platform/Os/LatestRelease.cs`, `tests/Framejet.Platform.Tests/Os/LatestReleaseTests.cs`
- Modify: `src/Framejet/MainForm.cs`, `src/Framejet/ToolbarWindow.cs`, `src/Framejet/VideoMenu.cs`, `docs/design.md`, `README.md`, `CLAUDE.md`, `docs/superpowers/specs/2026-11-16-update-pill-design.md`

**Interfaces:**
- Consumes: `UpdateCheck`, `ReleaseInfo`, `UpdateOffer`, `UiText.UpdateTo/UpdateTooltip`, `AppSettings.CheckForUpdates` (Task 2).
- Produces:
  - `Framejet.Platform.Os.LatestRelease.Fetch(string url, string userAgent, TimeSpan timeout) : string` — synchronous `HttpWebRequest` GET with headers `User-Agent` and `Accept: application/vnd.github+json`, `Timeout` and `ReadWriteTimeout` = timeout; returns the body (UTF-8); throws `WebException` on failure or non-2xx.
  - `MainForm.AvailableUpdate : UpdateOffer` (not `Update`, which would hide `Control.Update()` and warn) (null until an offer arrives), `MainForm.OpenUpdatePage() : void`.
  - `ToolbarWindow.SetUpdate(UpdateOffer offer) : void`.

- [ ] **Step 1: Write the network test**

```csharp
[SkippableFact, Trait("Category", "Network")]
public void Fetches_the_latest_release()
{
    string json = null;
    try { json = LatestRelease.Fetch(UpdateCheck.LatestReleaseApi, "Framejet-tests", UpdateCheck.Timeout); }
    catch (WebException ex) { Skip.If(true, "GitHub not reachable: " + ex.Message); }
    var r = ReleaseInfo.Parse(json);
    Assert.StartsWith(UpdateCheck.ReleasePagePrefix, r.PageUrl);
    Assert.True(AppVersion.TryParse(r.Tag, out _));
}
```

- [ ] **Step 2: Run it and see it fail**

Run: `dotnet test tests\Framejet.Platform.Tests --filter "FullyQualifiedName~LatestReleaseTests"`
Expected: build error, `LatestRelease` not found.

- [ ] **Step 3: Implement `LatestRelease.Fetch`**, then run Step 2's command. Expected: PASS (or skipped offline).

- [ ] **Step 4: Wire `MainForm`**
  - Fields: `IDisposable _updateTimer; bool _updateStarted;`.
  - End of `StartDevices()`: `if (UpdateCheck.ShouldCheck(Application.ProductVersion, Settings.CheckForUpdates, _selfTesting)) _updateTimer = Clock.After(UpdateCheck.Delay, StartUpdateCheck);`
  - `StartUpdateCheck()`: guard with `_updateStarted`; capture `Application.ProductVersion` on the UI thread; `Task.Run` fetches, parses and evaluates; posts `ApplyUpdate(offer)` on success, or `Log.Error("Update check failed", ex)` on any exception, through `_dispatcher.Post`. Nothing touches the UI or `Log` off the UI thread.
  - `ApplyUpdate(UpdateOffer offer)`: return if `_closed`; set `AvailableUpdate = offer`; log `"Update check: " + (offer == null ? "up to date" : offer.Version + " available")`; `_toolbar?.SetUpdate(offer)`. Never calls `_autoHide.WarningRaised`.
  - `OpenUpdatePage()`: if `AvailableUpdate` is null return; `Process.Start(new ProcessStartInfo(AvailableUpdate.PageUrl) { UseShellExecute = true })` in try/catch → `Log.Error("Opening the release page failed", ex)`.
  - `OnFormClosed`: `_updateTimer?.Dispose();`.

- [ ] **Step 5: Add the pill to `ToolbarWindow`**
  - A `Button _update` created in the constructor, `Visible = false`, added to `Controls` but **not** to `_items` (so `LayoutControls`'s width table stays as is). It's added before `HookHover(this)`/`UseMenu(this, …)` so it gets both. Click → `_owner.OpenUpdatePage()`.
  - `SetUpdate(offer)`: null hides it; otherwise `Text = UiText.UpdateTo(offer.Version)`, tooltip `UiText.UpdateTooltip(offer.Version)`, visible, then `LayoutControls()`.
  - `LayoutControls`: when `_update.Visible`, size it to its text (`TextRenderer.MeasureText` + `Px(20)` padding, height = control height), place it at the right edge (`ClientSize.Width - Px(PadPx) - width`), and end `_status` a `gap` before it. The status text ellipsises first.
  - Look: pill-shaped (fully rounded ends via a `Region` from a rounded `GraphicsPath`, rebuilt on resize), `DarkTheme.AccentBack` fill, `DarkTheme.Text`, no border; hover `DarkTheme.AccentBorder`.

- [ ] **Step 6: Add the menu item to `VideoMenu.Fill`**

Right after the version item, when `form.AvailableUpdate != null`: `menu.Items.Add(Item(UiText.UpdateTo(form.AvailableUpdate.Version) + "…", false, form.OpenUpdatePage));`

- [ ] **Step 7: Document it**
  - `docs/design.md`: new **4.11 Update notice** (owner: for anyone who downloaded it; the pill only opens the release page; Claude's calls: everything else in the spec, key `update.check`, version `0.0.0-dev`); add the menu item to the 4.6 menu line and the pill to the 4.6 toolbar line. Leave "auto-update" in the §2 non-goals.
  - `README.md`, end of **Download**: "Framejet asks GitHub for the latest release about 10 seconds after it starts, and if there's a newer one, an **Update to …** button appears in the toolbar and the right-click menu. It sends nothing about you or your PC. To turn it off, close Framejet and set `update.check=false` in `%AppData%\Framejet\settings.ini`."
  - `CLAUDE.md`: add `UpdateCheck`/`AppVersion` to the Core policy list; one line under Settings and logs: "Unstamped builds are `0.0.0-dev` and never check for updates."
  - Design doc: `CheckForUpdates=false` → `update.check=false`.

- [ ] **Step 8: Build and run every automated check**

Run: `dotnet build Framejet.sln -c Release` → 0 warnings. `dotnet test tests\Framejet.Core.Tests` → all pass. In Windows PowerShell: `.github\check-single-exe.ps1 src\Framejet\bin\Release\net48` → passes.

- [ ] **Step 9: Check it by hand** (close any running Framejet first)
  - Build stamped as an older version: `dotnet build Framejet.sln -c Release "-p:Version=1.1.0"`. Run the exe. After about 10 s, move the mouse: the pill reads "Update to 1.2.0" (or the current latest) at the toolbar's right end; the toolbar did **not** pop up on its own. Hover shows the tooltip; click opens the release page.
  - Right-click: "Update to 1.2.0…" sits under "Framejet 1.1.0" and opens the page; it also works in fullscreen.
  - Narrow the window: the status text ellipsises, the pill stays whole.
  - Log has `Update check: 1.2.0 available`.
  - Rebuild without `-p:Version`: no pill, no update-check line in the log.
  - Rebuild with `"-p:Version=1.1.0"`, set `update.check=false` in a scratch `settings.ini` passed with `--settings <folder>\settings.ini`: no pill, no update-check line.
  - Disconnect the network (or block api.github.com) with `-p:Version=1.1.0`: no pill, one `Update check failed` line.

- [ ] **Step 10: Commit** — `feat: show an update pill when a newer release is out`
