# HANDOVER — Dynamic Grid (Tier 1) · PHASE 4 (`applyRoster` control protocol)

**For:** a fresh coding agent picking up **only Phase 4**. Self-contained — everything you need is here.
**Repo:** `C:\Users\gregor\Downloads\Dev\mpv` (MpvGrid, .NET 9 WinForms, libmpv). **Shell:** PowerShell on Windows.
**Branch:** **`dev`** (all work lands here; `main` is frozen). Confirm with `git branch --show-current` before starting.
**Full spec:** `HANDOVER-dynamic-grid-tier1-execution.md` → read its **PHASE 4** section + **ATOMICITY**, **PRECEDENCE**,
and **DATA-MODEL** sections. This doc is the authoritative, anchor-verified subset for Phase 4.

---

## Where we are (do NOT redo)

All on `dev`, build currently green (0 warnings / 0 errors):

| Phase | Commit | What |
|---|---|---|
| 1 | `0eed130` | `GridLayout.Tile(count,w,h)` (N-up tiling); `Quadrants` → shim `Tile(4,…)` |
| 2 | `b65f5e5` | `GridForm` builds N hosts + `Tile`; `OverlayForm.RebuildQuadrants()` from `_feeds.Count`; hotkeys D1–D9 |
| 3 | `a17409f` | `Config.Normalize` caps 4→16; **`InactivePool` list + `InactiveStream` POCO already exist** (Config.cs:40 / :248) + pool Normalize |

**Key consequence of Phase 3:** the shared `InactiveStream { string Url; string Name; bool NamesOnly; }` type
**already exists** in `Config.cs` (namespace `MpvGrid`). Phase 4 just *uses* it — do not redefine it.

Phases remaining after you: Phase 5 (web dashboard). The **runtime** launch/visual tests for Phases 2 & 4 are
deliberately deferred to one combined pass after the C# phases (the app needs a UAC elevation prompt + a screen
+ the camera rig). So your Phase 4 verification is **static** (build + grep + a wire round-trip console check +
code review) — see "Verify gates". Do not try to launch the app.

---

## Phase 4 scope — edit ONLY `ControlProtocol.cs` and `RenderApp.cs`

Goal: a new **distinct** command verb `applyRoster` that **overwrites `Config` wholesale** from the dashboard's
full desired roster (active arrays + inactive pool), `Save()`s, then **restarts the display** so a fresh render
process re-reads `streams.json`. This is atomic at the process boundary (write-then-close). It is **never** a
positional merge. Also surface the pool in telemetry so the dashboard (Phase 5) can render it.

> **Why a new verb, not `save`/`setStreams`:** those are positional **live-merge** paths (`ApplyStreams` at
> RenderApp.cs:346 swaps only changed cells by index and must keep feed-index == Config-index aligned).
> `applyRoster` is a wholesale overwrite + restart — conflating them would misalign cells. Keep it separate.

> **No `TrayContext` change needed.** `applyRoster` is a render-pipe verb; the supervisor's `TrayContext.OnCommand`
> only intercepts `feedToggle`/`shutdownDisplays`/`exitApplication` and passes everything else through its
> `default` branch to the render pipe. Don't touch routing.

### Edit 1 — `ControlProtocol.cs`

**1a.** In `ControlCommands` (the `internal static class` at line 80, constants run :82–:93), add a new constant
(e.g. right after `Save` at :92):
```csharp
public const string ApplyRoster = "applyRoster"; // wholesale roster overwrite + Save + restart (dynamic-grid Tier 1)
```

**1b.** In the `ControlCommand` DTO (line 97; existing `Streams` field at :104), add the payload fields right
after `Streams`:
```csharp
public List<string>? Names { get; set; }                 // active vessel names, aligned with Streams
public List<bool>? NamesOnly { get; set; }               // active "name only" flags, aligned with Streams
public List<InactiveStream>? InactivePool { get; set; }  // the parked pool (wholesale)
```

**1c.** In the `ControlSnapshot` DTO (line 70; last field `RenderPid` at :76), add:
```csharp
public List<InactiveStream> InactivePool { get; set; } = new(); // parked pool, for the dashboard roster editor
```

### Edit 2 — `RenderApp.cs`

**2a. Snapshot pool passthrough.** In `BuildSnapshot()` (line 111), after the `snap.Settings = new SettingsSnapshot
{ … };` block ends (line 168) and **before** `return snap;` (line 170), add:
```csharp
// Copy (not alias) so telemetry never shares the live config's pool list.
snap.InactivePool = _config.InactivePool
    .Select(p => new InactiveStream { Url = p.Url, Name = p.Name, NamesOnly = p.NamesOnly })
    .ToList();
```

**2b. Dispatch case.** In `DispatchCommand()` (line 182), add a case near `SetStreams` (:249) / `Save` (:253):
```csharp
case ControlCommands.ApplyRoster:
    ApplyRoster(cmd);
    break;
```

**2c. The method.** Add this private method near `ApplyStreams` (:346) / `SaveConfig` (:368). It encodes the three
load-bearing rules — **wholesale overwrite**, **zip+drop blank-URL actives so the 3 parallel arrays stay aligned**
(Config.Normalize filters blank `Streams` but does NOT drop the aligned `Names`/`OverlayNamesOnly`, so do the
filter here), and **Save-before-Close / never close on Save failure**:
```csharp
/// <summary>Apply a full roster from the dashboard: overwrite Config wholesale (active arrays + pool),
/// persist, then restart the display so a fresh process re-reads streams.json (atomic at the process
/// boundary). Never a positional merge — see HANDOVER-dynamic-grid-tier1-execution.md §PRECEDENCE. UI thread.</summary>
private void ApplyRoster(ControlCommand cmd)
{
    var streams   = cmd.Streams      ?? new List<string>();
    var names     = cmd.Names        ?? new List<string>();
    var namesOnly = cmd.NamesOnly    ?? new List<bool>();
    var pool      = cmd.InactivePool ?? new List<InactiveStream>();

    // Zip + drop any active entry with a blank URL, carrying its aligned name/namesOnly with it, so the
    // three parallel lists stay index-aligned (Config.Normalize filters blank Streams but does NOT drop the
    // aligned Names/OverlayNamesOnly — doing the filter here avoids that shift).
    var act = new List<(string url, string name, bool no)>();
    for (int i = 0; i < streams.Count; i++)
    {
        string url = (streams[i] ?? string.Empty).Trim();
        if (url.Length == 0) continue;
        string nm  = i < names.Count ? (names[i] ?? string.Empty).Trim() : string.Empty;
        bool   no  = i < namesOnly.Count && namesOnly[i];
        act.Add((url, nm, no));
    }

    _config.Streams          = act.Select(a => a.url).ToList();
    _config.Names            = act.Select(a => a.name).ToList();
    _config.OverlayNamesOnly = act.Select(a => a.no).ToList();
    _config.InactivePool     = pool;          // Normalize() (inside Save) trims/filters/caps the pool
    _config.Normalize();

    try { _config.Save(); }
    catch (Exception ex)
    {
        Logger.Log($"Control: applyRoster save FAILED, NOT restarting: {ex.Message}");
        _server?.PushNow();
        return;   // keep the live grid on the old roster
    }

    Logger.Log($"Control: roster applied ({_config.Streams.Count} active, {_config.InactivePool.Count} pooled) — restarting display.");
    _form?.Close();   // exit 0 → supervisor relaunches → fresh Config.Load() reads the new roster
}
```

> `_config`, `_server` (`PushNow()`), `_form` (`Close()`), `Logger.Log`, `_config.Save()`/`Normalize()` are all
> confirmed present in `RenderApp.cs`. `_form?.Close()` is the same proven restart path as `RestartDisplay`
> (RenderApp.cs:191-195). LINQ + `List<>` are available via implicit usings (the file already uses `.Select`/`.ToList`).

---

## Verify gates (all static — do NOT launch the app)

1. **Build:** `dotnet build MpvGrid.csproj` → **0 Warning(s) / 0 Error(s)**.
2. **Grep gate:**
   - `ControlProtocol.cs` contains `ApplyRoster`, and `ControlCommand` has `Names`/`NamesOnly`/`InactivePool`,
     and `ControlSnapshot` has `InactivePool`.
   - `RenderApp.cs` contains exactly one `case ControlCommands.ApplyRoster:` and one `private void ApplyRoster(`.
3. **Wire round-trip console check (there is NO test project — do this):** create a throwaway console project in a
   temp dir OUTSIDE the repo (e.g. `C:\Users\gregor\AppData\Local\Temp\roster-verify`) via `dotnet new console`.
   Copy in the `InactiveStream` POCO + a minimal `ControlCommand` DTO (just `Name`, `Streams`, `Names`,
   `NamesOnly`, `InactivePool`) and the same `JsonSerializerOptions { PropertyNameCaseInsensitive = true }`
   (this mirrors `ControlJson.Opts`). Assert:
   - (a) Deserializing the sample payload below yields `Name=="applyRoster"`, 3 streams, 3 names, 3 namesOnly,
     and a 1-entry `InactivePool` with `Url=="rtsp://d/h264"`, `NamesOnly==false`.
   - (b) Re-running the `ApplyRoster` zip+drop logic (copy it) on a payload whose 2nd stream URL is blank
     drops that entry **and** its aligned name/namesOnly (3 in → 2 out, still aligned).
   Sample payload (a):
   ```json
   {"name":"applyRoster","streams":["rtsp://a/h264","rtsp://b/h264","rtsp://c/h264"],"names":["A","B","C"],"namesOnly":[false,false,false],"inactivePool":[{"url":"rtsp://d/h264","name":"D","namesOnly":false}]}
   ```
   Print a single `ALL PASSED` / `FAILED` line, exit non-zero on failure, run with `dotnet run --project <dir>`,
   confirm `ALL PASSED`, then **delete the temp dir** (`Remove-Item -Recurse -Force <dir>`).
4. **Code review:** confirm the wholesale overwrite (not positional merge), Save-before-Close ordering, and the
   no-close-on-Save-failure branch are exactly as above.

> **Runtime checkpoint is DEFERRED** (don't run it): the execution doc's Phase 4 checkpoint (POST an `applyRoster`
> to `http://localhost:8080/command`, watch the wall blink/relaunch as a 3-up, pool persists in `streams.json`)
> needs the running app (UAC + screen). It will be done in the combined runtime pass after Phase 5. Note this in
> your report; do not attempt to launch.

---

## Commit (only after gates 1–4 pass)

Stage **only** the two changed files — do NOT add the untracked `HANDOVER-*.md` files:
```
git add ControlProtocol.cs RenderApp.cs
```
Commit on `dev` with this message (PowerShell single-quoted here-string; closing `'@` at column 0):
```
Phase 4: applyRoster command — wholesale overwrite + Save + restart; pool in snapshot

Add the applyRoster control verb (distinct from the positional live-merge save/setStreams
paths): ControlCommand gains Names/NamesOnly/InactivePool; ControlSnapshot gains InactivePool.
RenderApp.ApplyRoster overwrites Config.Streams/Names/OverlayNamesOnly + InactivePool wholesale
(zip+drop blank-URL actives to keep the 3 parallel arrays aligned), Normalize, Save, then close
the form (exit 0 -> supervisor relaunches -> fresh Config.Load reads the new roster). Save-before-
Close; on Save failure we log + push telemetry and do NOT restart (live grid keeps the old roster).
BuildSnapshot copies the pool into telemetry. No TrayContext/routing change (render-pipe verb).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```
(The `LF will be replaced by CRLF` git warning is expected and harmless.)

## Report back
Confirm: branch was `dev`; the exact edits (with line context); build 0/0; grep-gate result; the round-trip
console output line; the commit short hash (`git rev-parse --short HEAD`); temp dir deleted; and an explicit note
that the runtime checkpoint was deferred. If anything failed or you deviated, say so plainly — don't paper over it.
