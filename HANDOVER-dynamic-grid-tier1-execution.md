# EXECUTION DOC — Dynamic Grid (Tier 1: variable cell count, restart-based)

**Document type:** *execution doc.* This is the implementable spec produced from
`HANDOVER-dynamic-grid-tier1.md` (the planning brief). It is written for a **coding agent to review and
implement**. Every edit is anchored to a real file + symbol/line (verified against `dev` @ `9e45605`,
2026‑06‑05). Decisions are resolved up front; phases each end in a runnable checkpoint and a `dev`-branch
commit.

> **Branch & workflow.** All work lands on **`dev`**. `main` is frozen at `9e45605` ("Alignment Commit") —
> `git checkout main` recovers a known-good build. Commit at **every phase checkpoint** so one bad phase
> rolls back without losing the good ones. **Do not merge to `main`** until the manual launch matrix (§ACCEPTANCE)
> passes *and* the high-cell-count rig test (§RISK) is done.

> **Verification status (read before implementing).** The plan agent re-read the full source this session and
> **verified every anchor below against real code**, including the five files the brief had left unopened
> (`ControlProtocol.cs`, `FeedController*.cs`, `web/app.js`, `web/index.html`, `web/style.css`) plus the
> command-routing plumbing (`HttpControlBridge.cs`, `TrayContext.cs`, `ControlClient.cs`, `ControlServer.cs`,
> `Supervisor.cs`, `Program.cs`, `Constants.cs`). Findings are folded in below. The atomicity linchpin is
> **confirmed end-to-end** (§ATOMICITY).

---

## 0. RESOLVED DECISIONS (answers to brief §6)

| # | Question | Decision |
|---|----------|----------|
| **Q1** | Cap | **16 cells (4×4).** Enforced in three places: `Config.Normalize` caps active lists at 16; `GridForm` clamps host count to `[1,16]`; the dashboard "Add stream" disables at 16 active. |
| **Q2** | Tiling table | **Near-square, last row stretched edge-to-edge.** `cols=⌈√n⌉`, `rows=⌈n/cols⌉`; first `rows-1` rows hold `cols` cells, the last row holds the remainder **stretched full-width**. Definitive table in §TILING. (n=2 side-by-side, n=3 = 2-over-1-stretched — both honored.) |
| **Q3** | Inactive-pool shape | **Object list** `List<InactiveStream>` where `InactiveStream { string Url; string Name; bool NamesOnly; }`. The pool has no positional-alignment requirement, so an object list is cleaner and self-describing. The **active** set keeps the parallel-array model (brief §3.5 — unchanged). |
| **Q4** | Explicit layout override | **No — auto-from-count only** for Tier 1. A forced grid shape with empty cells is a future add-on; note only. |
| **Q5** | Command shape | **New distinct verb `applyRoster`** (do NOT extend `save`/`setStreams` — those are positional live-merge paths and must not be confused with the wholesale-overwrite restart path). Payload: `streams[]`, `names[]`, `namesOnly[]`, `inactivePool[]`. The render **overwrites `Config` wholesale** from the payload (§PRECEDENCE), `Save()`s, then restarts. |
| **Q6** | Atomicity / restart ordering | Confirmed (§ATOMICITY). Ordering: **overwrite Config → `Save()` (synchronous) → `_form.Close()`**. **If `Save()` throws, do NOT close** — log + push telemetry; the live grid keeps running the old roster. |
| **Q7** | Precedence vs the live path | **Wholesale overwrite + disable live per-cell edits while the roster editor is dirty** (§PRECEDENCE). The render overwrites `Config.Streams/Names/OverlayNamesOnly/InactivePool` from the payload — never a positional merge (deactivating a middle cell renumbers everything; a merge would misalign). |
| **Q8** | Empty roster | **Never persist 0 active via the UI** (dashboard disables removing/deactivating the last active stream and disables Apply at 0 active). **Defensively**, `GridForm` clamps host count to `≥1`, so a hand-edited empty `streams.json` shows **one black cell**, not a zero-size grid or crash. |
| **Q9** | Hotkeys | **D1–D9 (+ NumPad1–9).** Cells 10–16 rely on the dashboard per-card Reconnect. The existing `ForceReconnect` wiring already guards `index < Feeds.Count`, so out-of-range keys are safe no-ops. |
| **Q10** | Dashboard UX | A new **"Roster (layout)"** sub-section in the Streams panel: membership-only editor (add / remove / deactivate / reactivate) over a **draft** seeded from telemetry + pool; one **"Apply layout & restart"** button (blink warning) + **"Discard"**. Active cells are **not reorderable** in Tier 1 (note as future). Live per-cell name/url/namesOnly editing stays in the existing feed cards and is **disabled while the roster draft is dirty**. Details in §PHASE-5. |
| **Q11** | `web/app.js` / `style.css` count-agnostic? + Finding 2 | **Verified count-agnostic:** `renderFeeds` (app.js:450) grows/shrinks the card list to `feeds.length`; `.feeds` CSS (style.css:249) is `repeat(auto-fit, minmax(360px,1fr))` — no fixed 4-up. **Finding 2 fix:** `ViewFrameBuilder.BuildFeeds` (ViewFrameBuilder.cs:205) hard-codes 4 offline placeholders → size to last-known feed count, clamp `[1,16]` (§PHASE-5). |
| **Q12** | Retire grid-count | **Confirmed superseded.** This feature is the superset of the queued 1–4 `grid-count` dropdown. The implementer **must not build `grid-count`**. (Plan agent will update `memory/grid-count-feature.md` + the index to mark it retired.) |

---

## TILING — definitive table (n = 1..16)

Algorithm: `cols = ceil(sqrt(n))`, `rows = ceil(n / cols)`. First `rows-1` rows each hold `cols` cells; the
**last row holds `n-(rows-1)*cols` cells stretched edge-to-edge**. Cells are numbered **row-major**
(left→right, top→bottom), preserving today's TL=1, TR=2, BL=3, BR=4 ordering.

| n | cols | rows | per-row cell counts (top→bottom) | notes |
|---|------|------|----------------------------------|-------|
| 1 | 1 | 1 | [1] | full screen |
| 2 | 2 | 1 | [2] | side-by-side |
| 3 | 2 | 2 | [2, 1] | 2-over-1, bottom stretched full width |
| 4 | 2 | 2 | [2, 2] | **identical to today's `Quadrants`** |
| 5 | 3 | 2 | [3, 2] | bottom 2 each half-width |
| 6 | 3 | 2 | [3, 3] | |
| 7 | 3 | 3 | [3, 3, 1] | bottom 1 stretched full width |
| 8 | 3 | 3 | [3, 3, 2] | (4×2 is arguably nicer — out of scope; auto-only, Q4) |
| 9 | 3 | 3 | [3, 3, 3] | |
| 10 | 4 | 3 | [4, 4, 2] | |
| 11 | 4 | 3 | [4, 4, 3] | |
| 12 | 4 | 3 | [4, 4, 4] | |
| 13 | 4 | 4 | [4, 4, 4, 1] | |
| 14 | 4 | 4 | [4, 4, 4, 2] | |
| 15 | 4 | 4 | [4, 4, 4, 3] | |
| 16 | 4 | 4 | [4, 4, 4, 4] | |

**Pixel contract (must match today's zero-gap / zero-overflow guarantee).** Use **boundary arithmetic**, not
per-cell sizes, so adjacent cells share an exact edge and the last cell reaches exactly `width`/`height`:

```
// row r spans y ∈ [ r*H/rows , (r+1)*H/rows )
// cell c of cellsInRow spans x ∈ [ c*W/cellsInRow , (c+1)*W/cellsInRow )
```

Integer division of the boundaries (not the spans) absorbs the odd pixel exactly the way `Quadrants` does
today. **This reproduces `Quadrants(w,h)` bit-for-bit at n=4** (verify in the Phase‑1 checkpoint).

---

## ATOMICITY (brief Q6 / §9 linchpin) — CONFIRMED end-to-end

Traced this session through real code:

1. Browser `POST /command` → `HttpControlBridge.HandleCommand` (HttpControlBridge.cs:171) deserializes a
   `ControlCommand` → `_onCommand` = `TrayContext.OnCommand` (TrayContext.cs:102).
2. `OnCommand`'s switch handles only the **supervisor** verbs (`feedToggle`/`shutdownDisplays`/`exitApplication`);
   **everything else falls through `default → _client.Send(cmd)`** (TrayContext.cs:113-114) onto the render pipe.
   ⟹ `applyRoster`, being a render-pipe verb, needs **no `TrayContext` change**.
3. Render pipe → `ControlServer.ServeClient` (ControlServer.cs:64) reads the line → `_onCommand` →
   `RenderApp.HandleCommand` (RenderApp.cs:174) → `BeginInvoke` → `DispatchCommand` (RenderApp.cs:182) on the UI thread.
4. New `applyRoster` case: overwrite `Config` → `_config.Save()` (synchronous `File.WriteAllText`, Config.cs:154)
   → `_form?.Close()` (same proven path as `RestartDisplay`, RenderApp.cs:191-195).
5. `_form.Close()` → render process exits **`Constants.ExitRestart` (= 0)** (Program.cs:76).
6. `Supervisor.Run` main loop (Supervisor.cs:98): `WaitForExit` → code is not `ExitStop`, not `_paused` →
   `StartRender` → `Process.Start("MpvGrid.exe --render")` (Supervisor.cs:337) → `RenderHost.Run`
   (Program.cs:31) → **fresh `Config.Load()` off disk** (Program.cs:33) → `GridForm(config)` builds hosts from
   the **just-written** `streams.json`.

`Save()` is synchronous and completes before `Close()`, so **write-then-restart is atomic at the process
boundary**. The new render reads the new roster. The live engine is never re-tiled.

> **Known pre-existing behavior (note, not a blocker):** `ExitRestart` within `SupervisorMinHealthyMs`
> (default 10 s) is treated as a crash-loop and incurs the existing exponential backoff (≤15 s cap,
> Supervisor.cs:125-130). This already affects `Restart Display`; `applyRoster` shares the exact same exit-0
> path, so behavior is unchanged. Mention in the UI hint only if field testing finds it annoying — out of scope here.

---

## PRECEDENCE (brief Q7 / §9) — the one subtle correctness point

"The live engine is never mutated" is true of the **engine**, but the live per-cell path **does mutate the
render's in-memory `Config`** by index (`RenderApp.SetListItem` at RenderApp.cs:228/238/358) and persists on
`Save`. The live path and `applyRoster` write the **same** `streams.json`. Resolution (bake in exactly):

1. The dashboard already receives every live edit via telemetry (`FeedSnapshot.Name/NamesOnly/Url`,
   ControlProtocol.cs:22). The roster Apply sends the dashboard's **full desired active arrays + pool**.
2. The render **overwrites `Config` wholesale** from that payload (never positional merge) — this preserves
   live edits the operator just made *and* sidesteps the index-shift hazard (deactivating a middle cell
   renumbers everything).
3. Close the residual "edited after the payload was built" race by **disabling live per-cell edits once the
   roster draft is dirty** (§PHASE-5). While dirty, the live feed set is stable (no live name/url/membership
   churn), so the draft can safely reference active feeds by their current index.

---

## LIVE-PATH-UNTOUCHED ASSERTION (the whole reason Tier 1 is low-risk)

**The invariant:** *engine feed-index == `Config.Streams` index == `Config.Names` index ==
`Config.OverlayNamesOnly` index.* Live per-cell edits (`SetName`/`SetUrl`/names-only/reconnect) mirror back
into `Config` by feed index via `RenderApp.SetListItem`. Keeping the **active set contiguous** (deactivated
streams move to the separate `InactivePool` the engine never reads) preserves this invariant — so
`ApplyStreams` (RenderApp.cs:346) and the live per-cell paths get **zero changes**. Do not break contiguity.

---

## DATA-MODEL CHANGES (one shared `InactiveStream` type)

Add a small shared POCO (place it in `Config.cs`, the persistence owner; it is reused by the wire DTOs in the
same `MpvGrid` namespace):

```csharp
/// <summary>A defined-but-inactive stream parked in the roster pool. Not positionally aligned with anything;
/// the engine never reads it. Round-trips in streams.json and over both IPC hops.</summary>
internal sealed class InactiveStream
{
    public string Url { get; set; } = "";
    public string Name { get; set; } = "";
    public bool NamesOnly { get; set; }
}
```

It serializes correctly on all three hops with no extra attributes: streams.json (indented PascalCase write /
case-insensitive read), the pipe (`ControlJson`, case-insensitive), and the browser SSE frame
(`ViewFrameBuilder`'s camelCase policy → `url`/`name`/`namesOnly`).

---

## PHASES

Each phase: anchor-based edits → checkpoint → `git commit` on `dev`.

### PHASE 0 — Branch hygiene & baseline
- `git branch --show-current` ⟹ must be `dev`. (Confirmed this session.)
- Baseline build: `dotnet build MpvGrid.csproj` ⟹ **0 warnings / 0 errors** before touching anything.
- **Checkpoint / commit:** none (no code change) — this is the green starting point.

---

### PHASE 1 — General tiling function
**File:** `GridLayout.cs`

- **Add** `public static Rectangle[] Tile(int count, int width, int height)` implementing §TILING with the
  boundary-arithmetic pixel contract. Clamp `count` to `[1,16]` defensively. Cells returned **row-major**.
- **Keep** the public `Quadrants(int,int)` as a thin shim: `=> Tile(4, width, height);` (so any straggler
  caller still compiles and behaves identically), OR inline-replace its callers in Phase 2 and delete it.
  Recommend keeping the shim this phase (smaller diff), then removing it once Phase 2's callers move to `Tile`.

**Checkpoint (pure function — no window needed).** There is **no test project** in this solution, so verify
with a throwaway `#if DEBUG` self-check (or a scratch console) asserting, for **every n in 1..16** at a couple
of sizes (e.g. 1920×1080, 2560×1440, odd 1921×1081):
- exactly `n` rects returned;
- **coverage:** `Σ area == width*height`;
- **no overlap** between any two rects;
- every rect within `[0,width]×[0,height]`, no negative dims;
- **`Tile(4,w,h)` is element-wise equal to the old `Quadrants(w,h)`** (TL,TR,BL,BR).

Remove the self-check before commit (or guard it `#if DEBUG` and leave it).

- **Commit:** `Phase 1: general N-up tiling (GridLayout.Tile), Quadrants == Tile(4)`.

---

### PHASE 2 — Variable cell count in the render window
**Files:** `GridForm.cs`, `OverlayForm.cs`

**`GridForm.cs`:**
- :14 `private readonly MpvHost[] _views = new MpvHost[4];` → size from config:
  `private readonly MpvHost[] _views;` and in the ctor compute
  `int cells = Math.Clamp(_config.Streams.Count, 1, 16); _views = new MpvHost[cells];`
  **before** the build loop. (Ctor already receives `Config` at :30.)
- :45 ctor loop `for (int i = 0; i < 4; i++)` → `for (int i = 0; i < _views.Length; i++)`.
- :17/:18 update the `Views` doc comment (drop "4").
- :69-82 `ApplyGrid`: `:76` `GridLayout.Quadrants(ClientSize.Width, ClientSize.Height)` →
  `GridLayout.Tile(_views.Length, ClientSize.Width, ClientSize.Height)`; `:77` loop `i < 4` → `i < _views.Length`.
- :135-160 `ProcessCmdKey`: extend hotkeys to **D5–D9 + NumPad5–9** calling `ForceReconnect?.Invoke(4..8)`.
  Keep the existing D1–D4. (The `ForceReconnect` lambda in `RenderApp` already guards `i < Feeds.Count`, so
  pressing a key past the live count is a safe no-op.)

**`OverlayForm.cs` (Finding 1 — count must be threaded in):**
- The quadrants are rebuilt in the **ctor (:61)** and **`Reposition` (:95-100)** from `(width,height)` only.
  Generalize by deriving the count from `_feeds.Count` (which `PaintOverlay` at :135 already bounds by):
  - Add a private `void RebuildQuadrants()` →
    `_quadrants = GridLayout.Tile(Math.Clamp(_feeds.Count, 1, 16), Bounds.Width, Bounds.Height);`
  - `SetFeeds` (:102-106): after `_feeds = feeds;` call `RebuildQuadrants();` then `RenderNow();`.
  - `Reposition` (:95-100): after `Bounds = screenBounds;` call `RebuildQuadrants();` then `RenderNow();`.
  - ctor (:61): replace the direct `GridLayout.Quadrants(...)` with `RebuildQuadrants();` (at ctor time
    `_feeds` is empty ⟹ clamps to 1 rect; harmless — `RenderApp.OnFormShown` immediately calls
    `SetFeeds` + `Reposition`, and `PaintOverlay` is bounded by `_feeds.Count == 0` so nothing draws yet).
  - This keeps `_quadrants` always consistent with the feed count `PaintOverlay` iterates — **no ctor/Reposition
    signature change needed** (cleaner than adding a count parameter).

> Alignment note: `GridForm` tiles by host count `Clamp(Streams.Count,1,16)`; `OverlayForm` tiles by
> `_feeds.Count` = `min(hosts, Streams.Count)` (Engine.cs:30). For any `Streams.Count ≥ 1` these are equal, so
> badges align with cells. Only the `Streams.Count == 0` edge differs (1 host, 0 feeds) and the overlay paints
> nothing — no visible misalignment.

**Checkpoint.** Hand-edit `streams.json` beside the built exe to **3** streams, launch (double-click =
supervisor), confirm: correct 2-over-1 tiling, overlay badges aligned, per-feed Reconnect (D1–D3 + dashboard)
work. Repeat with **9** streams (3×3) and confirm D1–D9 + 3×3 alignment. Then **16** (4×4).

- **Commit:** `Phase 2: GridForm builds N hosts + N-up tiling; OverlayForm tiles by feed count; hotkeys D1–D9`.

---

### PHASE 3 — Config schema + back-compat
**File:** `Config.cs`

- Add the field (near :35, after `OverlayNamesOnly`):
  `public List<InactiveStream> InactivePool { get; set; } = new();`
- Add the `InactiveStream` type (§DATA-MODEL) to this file.
- `Normalize` (:168-223): change the three **`.Take(4)`** caps to **`.Take(16)`** (Streams :173, Names :179,
  OverlayNamesOnly :184). Add pool normalization at the end:
  ```csharp
  InactivePool = (InactivePool ?? new List<InactiveStream>())
      .Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Url))
      .Select(p => new InactiveStream { Url = p.Url.Trim(), Name = (p.Name ?? "").Trim(), NamesOnly = p.NamesOnly })
      .Take(64)   // soft cap to bound streams.json; arbitrary, generous
      .ToList();
  ```
  (Leave the existing default 4-stream `Streams` initializer at :14 unchanged.)

> Do **not** add per-position dedup to the active arrays — duplicate camera URLs across cells are legitimate
> (same camera shown twice). `Normalize` already does not dedup; keep it that way.

**Checkpoint.** (a) An **old** `streams.json` with no `InactivePool` key loads unchanged (defaults to empty;
unknown keys already ignored — Config.cs:114). (b) Hand-write an `InactivePool` array + a 6-entry `Streams`,
load + `Save` (e.g. via the running dashboard's existing Save) and confirm the file **round-trips** all fields
and the 6 streams survive (no longer capped to 4).

- **Commit:** `Phase 3: Config caps lifted to 16 + InactivePool, back-compat preserved`.

---

### PHASE 4 — Control protocol (`applyRoster`)
**Files:** `ControlProtocol.cs`, `RenderApp.cs`

**`ControlProtocol.cs`:**
- `ControlCommands` (:80): add `public const string ApplyRoster = "applyRoster";`
- `ControlCommand` DTO (:97): add the payload fields (alongside the existing `Streams`):
  ```csharp
  public List<string>? Names { get; set; }          // active vessel names, aligned with Streams
  public List<bool>? NamesOnly { get; set; }         // active "name only" flags, aligned with Streams
  public List<InactiveStream>? InactivePool { get; set; }  // the parked pool (wholesale)
  ```
- `ControlSnapshot` (:70): add `public List<InactiveStream> InactivePool { get; set; } = new();` so the
  dashboard can render the pool.

**`RenderApp.cs`:**
- `BuildSnapshot` (:111-171): populate the new snapshot field —
  `snap.InactivePool = _config.InactivePool.Select(p => new InactiveStream { Url = p.Url, Name = p.Name, NamesOnly = p.NamesOnly }).ToList();`
  (copy so telemetry never aliases live config).
- `DispatchCommand` (:182): add the case (place it near `SetStreams`/`Save`):
  ```csharp
  case ControlCommands.ApplyRoster:
      ApplyRoster(cmd);
      break;
  ```
- Add the method (model it on the §PRECEDENCE + §ATOMICITY rules — wholesale overwrite, zip-drop blank actives
  to keep the three parallel arrays aligned, Save-before-Close, never close on Save failure):
  ```csharp
  /// <summary>Apply a full roster from the dashboard: overwrite Config wholesale (active arrays + pool),
  /// persist, then restart the display so a fresh process re-reads streams.json (atomic at the process
  /// boundary). Never a positional merge — see the execution doc §PRECEDENCE. UI thread.</summary>
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

> No `TrayContext` change — `applyRoster` routes through the `default` branch (§ATOMICITY step 2).

**Checkpoint.** With the app running, send a raw `applyRoster` over the existing `POST /command` endpoint
(loopback; e.g. `curl`/PowerShell `Invoke-RestMethod` to `http://localhost:8080/command` with body
`{"name":"applyRoster","streams":["rtsp://a/h264","rtsp://b/h264","rtsp://c/h264"],"names":["A","B","C"],"namesOnly":[false,false,false],"inactivePool":[{"url":"rtsp://d/h264","name":"D","namesOnly":false}]}`).
Confirm: `streams.json` is rewritten (3 active + 1 pooled), the grid **blinks and relaunches as a 3-up wall**,
and the pool entry survives in the file.

- **Commit:** `Phase 4: applyRoster command — wholesale overwrite + Save + restart; pool in snapshot`.

---

### PHASE 5 — Dashboard roster editor + view-model
**Files:** `ViewFrameBuilder.cs`, `web/index.html`, `web/style.css`, `web/app.js`

**`ViewFrameBuilder.cs`:**
- **Finding 2 fix** — `BuildFeeds` (:205-214) hard-codes 4 offline placeholders. Track the last-known count and
  size to it:
  - Add a field `private int _lastFeedCount;` updated in `Tally` (:86-99): `_lastFeedCount = snap.Feeds.Count;`
    (and do **not** reset it in `OnConnection`'s disconnect branch at :74-80 — keep the last value across the blink).
  - In `BuildFeeds`, replace the `for (int i = 0; i < 4; i++)` with
    `int n = Math.Clamp(_lastFeedCount, 1, 16); for (int i = 0; i < n; i++)`. (Exactness during the ~1 s blink
    doesn't matter; it snaps to the real count on reconnect.)
- **Pool passthrough** — add `public List<InactiveStream> InactivePool { get; set; } = new();` to `ViewFrame`
  (:236-250), and in `BuildFrame` (:138-152) set
  `InactivePool = connected && last is not null ? last.InactivePool : new List<InactiveStream>()`. (Serializes
  camelCase → `inactivePool` for the browser.)

**`web/index.html`:** in the **Streams** panel (:126-137), after the `#feeds` div / before `.panel-foot`, add a
**Roster (layout)** sub-block:
- an **"Add stream"** row: a URL `<input>` (+ optional name `<input>`) and an **Add** button;
- a container for the **active roster** list (rendered by JS) — each row: cell #, name/url label, a
  **Deactivate** (→ pool) button, a **Remove** button;
- an **"Inactive pool"** container (rendered by JS) — each row: name/url label, an **Activate** button, a
  **Delete** button;
- a footer: **"Apply layout & restart"** (primary) + **"Discard"** button + a feedback span + the blink
  warning copy (e.g. *"Applying rebuilds the wall — it blinks ~1 s while the display restarts."*).

**`web/style.css`:** add styles for the roster list rows / pool rows / add-stream row, reusing the existing
`--surface*`/`--outline` tokens and `.btn`/`.btn-sm` classes (mirror `.feed*` styling so it reads as the same
product). No change needed to `.feeds` (already count-agnostic).

**`web/app.js`** — add a **roster-draft** model (mirror the existing latch pattern):
- New `els` handles for the roster DOM; a `roster = { active: [], pool: [], dirty: false }` draft and a
  `rosterLatched` flag (re-armed on disconnect alongside `namesLatched`/`settingsLatched` at :273).
- **Seed** the draft once per connection from a frame that carries live feeds (same `hasLive` guard as
  `renderSettings`): `active` = `f.feeds` mapped to `{url,name,namesOnly}`; `pool` = `f.inactivePool` copy.
  While **not dirty**, re-seed every qualifying frame so the draft mirrors live telemetry.
- **Mutations** (each sets `dirty = true`, stops re-seeding, and calls a `renderRoster()` that repaints both
  lists): `Add` (push to `active` if `active.length < 16`); `Remove` (drop from `active`); `Deactivate` (move
  active→pool); `Activate` (move pool→active if `< 16`); `Delete` (drop from pool).
- **Guards:** disable Deactivate/Remove on the **last** active row; disable Add/Activate at 16 active; disable
  **"Apply layout & restart"** when `active.length === 0`.
- **Precedence (§PRECEDENCE):** while `roster.dirty`, **disable the live per-cell edit controls** in the feed
  cards (`name`, `namesOnly`, `reconnect`, `url`) — extend `updateRow` (:543-578) to also gate on
  `!roster.dirty` — and show a "roster has unsaved changes" hint. (The Source box's normal exception at
  :570-577 is overridden while dirty.)
- **Apply** → `send({ name:'applyRoster', streams: active.map(a=>a.url), names: active.map(a=>a.name),
  namesOnly: active.map(a=>a.namesOnly), inactivePool: roster.pool })`; on success show the blink feedback. The
  SSE drops during the restart, which re-arms `rosterLatched` (draft re-seeds from the new live roster on
  reconnect, `dirty=false`).
- **Discard** → `dirty=false`, re-enable live edits, re-seed the draft from the latest frame.
- Call `renderRoster(f)` from `render` (:263-282) alongside the other `render*` calls.

> Minor copy scrub (optional, same commit): `index.html:89` "4-feed display" and `app.js:131` "Restart the
> whole 4-feed display" → "display"/"grid"; `app.js:373` comment "4 offline placeholders" → "offline
> placeholders". Cosmetic; not gated.

**Checkpoint.** With a camera rig or reachable RTSP/RTP sources: **add** a stream (wall grows on Apply),
**deactivate** one (moves to pool, wall shrinks on Apply), **reactivate** from pool, **remove** a pooled
entry. Confirm each Apply blinks once and the wall rebuilds at the right count with badges aligned and the
pool persisted in `streams.json`. Confirm live name/url edits are blocked while the roster is dirty and
restored after Apply/Discard.

- **Commit:** `Phase 5: dashboard roster editor (add/remove/deactivate/reactivate + Apply), pool view-model, offline-placeholder count fix`.

---

## ACCEPTANCE CHECKLIST & VERIFY GATES

**Build / static gates (run after every phase that touches them):**
- `dotnet build MpvGrid.csproj` ⟹ **0 warnings / 0 errors**.
- `node --check web/app.js` ⟹ clean.
- **Grep gate** — no residual hard-coded 4-up in the **changed paths**:
  - `GridForm.cs`: no `new MpvHost[4]`, no `i < 4`, no `Quadrants(` (uses `Tile`).
  - `OverlayForm.cs`: no `Quadrants(` (uses `Tile`/`RebuildQuadrants`).
  - `Config.cs`: no `.Take(4)` (now `.Take(16)`).
  - `ViewFrameBuilder.cs`: `BuildFeeds` no longer loops `i < 4`.
  - (If the `Quadrants` shim was kept in `GridLayout.cs`, that single `Tile(4, …)` is the **only** intentional
    `4` allowed; otherwise remove it.)

**Manual launch matrix (pre-merge):**
- Launch at **1, 3, 9, 16** streams (hand-edited `streams.json`) → correct tiling, overlay alignment, per-feed
  reconnect (D1–D9 + dashboard).
- Old 4-stream `streams.json` (no `InactivePool`) loads unchanged.
- Dashboard: **add / remove / deactivate / reactivate**, then **Apply → restart** → wall rebuilds at the new
  count; pool persists; live edits disabled while dirty; Save-failure does not restart.
- Empty-roster guard: cannot Apply with 0 active; a hand-edited empty `streams.json` shows one black cell, no crash.

---

## RISK (brief §2 — the single biggest risk; put in front of the operator before high counts)

The libmpv migration's **Phase‑6 field tests are still outstanding** — telemetry property spellings, **4×
simultaneous HW decode / GPU load (D9)**, `wid` under PerMonitorV2. Tier 1 makes **9–16 simultaneous decodes**
trivial, far past the unvalidated 4×. **Ship the mechanism, but keep a high-cell-count wall (≳5) gated behind
the pending Phase‑6 rig validation.** Do **not** certify a 16-up wall until the GPU/decode question is answered
on the real hardware (HwDecode defaults OFF — software decode at 16-up will be CPU-heavy; that is the field
test). Recommend a one-line caution in the roster editor when the active count crosses ~5 until the rig test
clears it.

---

## FILES TOUCHED (summary)

| File | Change | Phase |
|------|--------|-------|
| `GridLayout.cs` | `+Tile(count,w,h)`; `Quadrants` → shim or removed | 1 |
| `GridForm.cs` | N hosts, `Tile`, loops by `_views.Length`, hotkeys D1–D9 | 2 |
| `OverlayForm.cs` | `RebuildQuadrants()` from `_feeds.Count` via `Tile` | 2 |
| `Config.cs` | caps 4→16; `+InactivePool`; `+InactiveStream` type; pool Normalize | 3 |
| `ControlProtocol.cs` | `+ApplyRoster` const; `+Names/NamesOnly/InactivePool` on cmd; `+InactivePool` on snapshot | 4 |
| `RenderApp.cs` | `+ApplyRoster()` + dispatch case; snapshot pool passthrough | 4 |
| `ViewFrameBuilder.cs` | offline-placeholder count fix; `+InactivePool` on `ViewFrame` | 5 |
| `web/index.html` | roster editor markup in Streams panel | 5 |
| `web/style.css` | roster/pool/add-row styles | 5 |
| `web/app.js` | roster-draft model + `applyRoster` send + dirty/precedence gating | 5 |

**No change:** `Engine.cs` (count-agnostic, Engine.cs:30), `TrayContext.cs` (routes via `default`),
`ControlClient.cs`/`ControlServer.cs`/`HttpControlBridge.cs` (transport-only), `Supervisor.cs`/`Program.cs`
(relaunch path is already correct), `FeedController*.cs`, `MpvHost.cs`.
