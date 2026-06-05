# HANDOVER — Dynamic Grid (Tier 1: variable cell count, restart-based)

**Document type:** *planning brief.* This is **not** the execution doc. Your job, plan agent, is to
turn this brief into `HANDOVER-dynamic-grid-tier1-execution.md` — an implementable, anchor-based,
phase-by-phase doc with checkpoints — which then goes to a **coding agent for review and implementation**.
Do not write production code. Resolve the open questions (§6), pin every edit to a real anchor, and define
the verify gates.

> **Branch & workflow (NEW 2026-06-05).** All work for this feature happens on the **`dev`** branch, not
> `main`. `main` is frozen at the known-good commit **`9e45605` ("Alignment Commit")** so a broken experiment
> is always one `git checkout main` away from recovery. This supersedes the old "no git" constraint *for this
> feature*. The execution doc must: (a) state implementation lands on `dev`; (b) recommend committing at each
> phase checkpoint so one bad phase rolls back without losing the good ones; (c) **not** merge to `main` until
> the manual launch matrix passes — and, for high cell counts, the Phase-6 rig test (§2). Note: the prior
> `Alignment Commit` deleted the earlier copies of both handover docs; this file is the recreated, verified
> version.

> **Read §9 first.** It is a *verification addendum* added after the brief was first written: the original
> "not yet reviewed" files were partly checked against the real code, the load-bearing **atomicity claim was
> confirmed**, every anchor in §4 was re-checked, and **two concrete new findings** were added. §9 tells you
> which anchors are now verified vs. which still need your own eyes — start there, then read §1–§8.

Mirror the style of the existing execution docs referenced in memory
(resolved decisions up front, finalized substitution/shape, N phases each ending in a concrete checkpoint).

---

## 1. What the feature is

A browser-dashboard capability to **add / remove camera streams and toggle which defined streams are
active**, with the render wall **auto-tiling to the active count** (1=full, 2=side-by-side, 3/4=2×2,
… up to 4×4 = 16). Changes apply via the **existing supervisor-relaunch path** (Restart Display) — the wall
blinks ~1 s, no live re-tiling. This is the user-selected **Tier 1** of three options (Tier 2 = live
add/remove no restart; Tier 3 = true BSP tiling WM — both explicitly **out of scope** here).

This **supersedes the queued `grid-count` feature** (the 1–4 dropdown) — it is its superset. The execution
doc should note that grid-count is retired/folded in, and the implementer should not build both.

## 2. Product context & lifecycle state

`C:\Users\gregor\Downloads\Dev\mpv` — `MpvGrid`, a .NET 9 WinForms live-camera video wall (libmpv engine,
one mpv handle per feed). A separate browser dashboard process talks to the render process over a control
pipe (telemetry out / commands in) and is served to the LAN.

**Critical sequencing caveat — put this in the execution doc's risk section:** the libmpv migration is
code-complete and build-green, but **Phase 6 field tests are still outstanding** (telemetry property
spellings, **4× simultaneous HW decode / GPU load (D9)**, `wid` under PerMonitorV2). Tier 1 makes it trivial
to run **9–16 simultaneous decodes**, far past the unvalidated 4×. The execution doc must state that a
high-cell-count wall stays **gated behind the pending Phase-6 rig validation** — ship the mechanism, but
don't certify a 16-up wall until the GPU/decode question is answered on the real rig. **This is the single
biggest risk in the feature.**

**Constraints (locked from prior sessions):** build must stay green; `streams.json` lives beside the exe;
product/namespace is `MpvGrid`. (The old "no git" constraint is lifted for this feature — see the branch note
above; work on `dev`.)

## 3. Locked scope & design decisions

These are decided — the plan agent should treat them as fixed and design around them:

1. **Restart-based, not live.** All roster/layout changes take effect on Restart Display (close render →
   supervisor relaunches → fresh `Config.Load()` → grid rebuilt). The **live engine is never mutated** by
   this feature. *(See §9: this is true of the engine; the live path still mutates `Config` — resolve the
   precedence point.)*
2. **Contiguous active set + separate inactive pool.** `Config.Streams` (+ the parallel `Names` /
   `OverlayNamesOnly`) remains the **active, displayed set**, always contiguous. Deactivated streams move to
   a **new pool list the engine never reads**. *This preserves the load-bearing invariant (§4) so the
   fragile live per-cell mirroring path needs zero changes.* This is the whole reason Tier 1 is low-risk —
   do not break it.
3. **Layout auto-derives from the active count.** No separate count/grid picker in Tier 1. Add a stream → a
   cell appears; deactivate one → grid re-tiles. Near-square: `cols=⌈√n⌉`, `rows=⌈n/cols⌉`.
4. **Atomic "Apply layout & restart."** The roster editor is **dashboard-local state**. One action persists
   `streams.json` *and* triggers the restart together — never a half-applied roster fighting the live engine.
5. **Keep the parallel-list data model** (don't refactor `Streams`/`Names`/`OverlayNamesOnly` into stream
   objects). The object refactor is a Tier-2+ concern; note it as such but don't do it now.
6. **Cap = 16 cells (4×4).** (Confirm in §6.)

## 4. Architecture map (anchors for the plan agent)

**The load-bearing invariant:** *engine feed-index == `Config.Streams` index == `Config.Names` index ==
`Config.OverlayNamesOnly` index.* Live per-cell edits (`SetName`, `SetUrl`, names-only, reconnect) are
mirrored back into `Config` **by feed index** via `RenderApp.SetListItem` (`RenderApp.cs:278`, used at
`:228`/`:238`/`:358`). Keeping the active set contiguous (decision §3.2) preserves this — so `ApplyStreams`
(`RenderApp.cs:346`) and the live paths stay untouched. **[§9: verified.]**

Choke points to generalize (each currently hard-codes 4):

- `GridLayout.cs:17` — `Quadrants(w,h)` returns exactly 4 rects, 2×2 only, with a zero-gap / zero-overflow
  remainder-absorption contract. **The single layout choke point** — both consumers below just take an array
  of rects, so generalizing this one pure function flows to both video tiling and overlay badges. **[§9: verified.]**
- `GridForm.cs:14` `new MpvHost[4]`, ctor loop `:45`, `ApplyGrid` `:69-79` (calls `Quadrants`, loops `i<4`).
  Ctor already receives `Config` (`:30`), so it can read the active count. Overlay reposition hook at `:81`.
  Hotkeys D1–D4 at `:155-158`. **[§9: verified.]**
- `OverlayForm.cs` — `_quadrants` from `Quadrants` (`:61`, `:98`); `PaintOverlay` already loops
  `i < _feeds.Count && i < _quadrants.Length` (`:135`). Array-driven **but** `_quadrants` is built from
  `(width,height)` only — **it must be told the active count** to generalize. **[§9: verified + see Finding 1.]**
- `Engine.cs:30` — `count = Math.Min(views.Count, _config.Streams.Count)`. **Already count-agnostic.** No
  change expected; the cell count is driven by how many hosts `GridForm` builds. **[§9: verified.]**
- `GridForm.cs:155-158` — hotkeys `D1`–`D4` (+ NumPad). Extend to `D1`–`D9`; cells 10–16 rely on the
  dashboard's per-card Reconnect button.
- `Config.cs:170-185` — `Normalize()` caps `Streams`/`Names`/`OverlayNamesOnly` at `.Take(4)` (lines
  `:173`/`:179`/`:184`). Lift to 16, add the pool, keep dedupe/trim. `Load` `:124` / `Save` `:154` round-trip
  the whole `Config`; unknown/legacy keys are already ignored (`JsonOpts`, no `UnmappedMemberHandling`), so
  old `streams.json` stays compatible. **[§9: verified.]**

Already dynamic (little/no change, but the plan agent should still confirm the dashboard side):

- `web/app.js` `renderFeeds` is claimed to grow/shrink the card list to `feeds.length`; `ControlSnapshot.Feeds`
  is an arbitrary-length `List`. The dashboard read-side is claimed to scale to N. **[§9: NOT yet opened by me —
  verify.]**
- `ViewFrameBuilder.cs` — the header **tally** is count-agnostic (`Tally` reads `snap.Feeds.Count`), **but**
  `BuildFeeds` hard-codes **four** offline placeholder rows for the disconnected state. **[§9: reviewed —
  Finding 2.]**

Control plumbing to extend:

- `ControlProtocol.cs:80` (`ControlCommands`) + `:97` (`ControlCommand` DTO) — add the roster command.
  **[§9: NOT yet opened by me — verify these line anchors.]**
- `RenderApp.cs:182` `DispatchCommand` — add one case that writes Config + `Save()` + runs the existing
  `RestartDisplay` close path (`:191`). Reuse, don't reinvent. **[§9: verified.]**

Restart mechanism: `RestartDisplay` closes the form → process exits → supervisor relaunches → fresh
`Config.Load()`. **[§9: CONFIRMED — see addendum; this is the atomicity linchpin and it holds.]**

## 5. Phase skeleton (plan agent fleshes each into anchor-based edits + a checkpoint)

1. **General tiling.** `GridLayout.Tile(int count, int w, int h)` → `count` zero-gap/zero-overflow rects;
   keep `Quadrants` as `Tile(4,…)` or inline. *Checkpoint: a pure-function check of rect coverage for
   n=1..16 (the existing "checkable without a window" property).*
2. **Variable cell count in the render window.** `GridForm` builds `Clamp(Streams.Count,1,16)` hosts;
   `ApplyGrid` + `OverlayForm` consume `Tile(count,…)` (thread the count into `OverlayForm` — Finding 1);
   hotkeys → D1–D9. *Checkpoint: hand-edit `streams.json` to 3 and 9 streams; launch; correct tiling,
   overlay alignment, per-feed reconnect.*
3. **Config schema + back-compat.** Lift caps to 16; add the inactive pool; `Normalize` handles it; old
   config loads unchanged. *Checkpoint: old 4-stream config loads; new fields round-trip.*
4. **Control protocol.** Add the `applyRoster` (or equivalent) command = write Config + Save + restart,
   atomic; live paths untouched. *Checkpoint: command persists + relaunches with the new roster.*
5. **Dashboard roster editor.** Add-stream button, remove/deactivate per card, an inactive-pool section
   (reactivate/delete), and one "Apply layout & restart" action with a blink warning. *Checkpoint: add /
   remove / deactivate / reactivate, Apply, wall rebuilds correctly.*

*(Optionally add a Phase 0: branch hygiene — confirm on `dev`, commit each checkpoint.)*

## 6. Open questions the plan agent MUST resolve before writing the execution doc

Decide each (use the recommendation unless there's a reason not to), and bake the answer into the doc:

1. **Cap.** 16 (4×4)? (Recommend yes.)
2. **Exact tiling table for n=1..16.** Partial last row: **stretch edge-to-edge** (recommended, fills the
   wall) vs centered. Pin the ambiguous small cases explicitly: n=2 side-by-side vs stacked; n=3 = 2-over-1
   stretched vs 2×2-with-one-empty. Produce a definitive table so the implementer isn't guessing.
3. **Inactive-pool shape.** Parallel arrays vs a small pool-only `{url,name,namesOnly}` object. The pool
   has no positional-alignment requirement, so an object list is cleaner there — confirm.
4. **Explicit layout override?** Strictly auto-from-count for Tier 1 (recommended), or allow forcing a grid
   shape with empty cells? (Recommend auto-only; note override as a future add-on.)
5. **Command shape.** New `applyRoster` verb vs extending `save`. Define the exact payload
   (`activeStreams[]`, `activeNames[]`, `activeNamesOnly[]`, `inactivePool[]`). Recommend a distinct verb so
   it can't be confused with the live per-cell `save`+`streams` path. *(See §9 precedence note: have the
   render OVERWRITE Config wholesale from this payload — don't positional-merge.)*
6. **Atomicity / restart.** Supervisor re-reads `streams.json` on relaunch — **confirmed in §9.** Still
   define the exact write-then-close ordering and what happens if the write fails (don't restart on a failed
   save).
7. **Precedence vs the live path.** **The subtlest correctness point — see §9 for the recommended resolution.**
   While a roster edit is pending in the dashboard, the operator can still live-edit a current cell
   (name/url). Recommended: roster Apply carries the dashboard's full current view (which already reflects
   live edits via telemetry) and the render overwrites Config wholesale; additionally disable live per-cell
   edits once the roster editor is dirty.
8. **Empty roster edge case.** 0 active streams → clamp to min 1, or allow a black wall? Define it.
9. **Hotkeys.** D1–D9 only, dashboard for 10–16? (Recommend yes.)
10. **Dashboard UX specifics.** Exact placement in `index.html` (the Streams panel), add/remove vs
    deactivate semantics, reactivate-from-pool, whether active cells are reorderable, and the precise
    warning copy for the restart blink.
11. **Verify `web/app.js` `renderFeeds` / `web/style.css`** are count-agnostic, and fix the
    `ViewFrameBuilder.BuildFeeds` 4-placeholder case (Finding 2). Read the web files (not yet opened) and
    record findings.
12. **Retire grid-count.** Confirm the old `grid-count` handover/memory is superseded and say so.

## 7. What the execution doc must contain (acceptance for your output)

- A resolved-decisions header (answers to all of §6).
- The definitive n=1..16 tiling table.
- Per-phase, **anchor-based** edits (file + line/symbol + before/after intent), each ending in a concrete,
  runnable checkpoint, with a **`dev`-branch commit** at each checkpoint.
- A final acceptance checklist + **verify gates**: `dotnet build MpvGrid.csproj` → 0 warnings / 0 errors;
  `node --check web/app.js`; a grep gate confirming no residual hard-coded `4`/`Quadrants` in the changed
  paths; and a manual launch matrix (1, 3, 9, 16 streams; add/remove/deactivate/reactivate; Apply→restart).
- The Phase-6 / decode-load risk note from §2 (ship the mechanism; gate high counts on the rig test).
- Explicit "live engine path untouched" assertion with the invariant from §4 — **and** the §9 precedence
  resolution that makes "live `Config` mutation vs. roster overwrite" safe.

## 8. Files to read

**Already reviewed & anchors verified (trust §4/§9 for these):** `GridLayout.cs`, `GridForm.cs`,
`Config.cs`, `Engine.cs`, `OverlayForm.cs`, `RenderApp.cs`, `Supervisor.cs`, `Program.cs`,
`ViewFrameBuilder.cs`.

**Still NOT opened — the plan agent must read and verify before writing the doc:** `ControlProtocol.cs`
(confirm the `ControlCommands` / `ControlCommand` DTO shape + the `:80`/`:97` anchors before extending it),
`FeedController*.cs` (`SetName`/`SetUrl`/`SetNamesOnly` behaviour), `web/app.js` (`renderFeeds` grow/shrink —
claimed dynamic, unread), `web/index.html` (Streams-panel placement for the roster editor), `web/style.css`
(any fixed 4-up CSS grid in the dashboard's **own** layout).

## 9. Verification addendum (checked against the real code, `dev` @ 9e45605, 2026-06-05)

I read the actual source for the anchors below. Treat anything marked **✓ verified** as confirmed; anything
marked **verify** still needs the plan agent's eyes.

**✓ The atomicity linchpin holds (the previously-unverified assumption).** `Program.Main` routes `--render`
to `RenderHost.Run`, which calls a **fresh `Config.Load()`** off disk. The supervisor relaunches the render
by `Process.Start`-ing a **brand-new `MpvGrid.exe --render`** process each cycle; the supervisor's own
`Config.Load()` only feeds its backoff params, not the camera list. ⟹ *write `streams.json` → close render →
supervisor relaunches → the new process reads the new roster.* "Apply & restart" is genuinely atomic at the
process boundary. This was the biggest open risk in the brief and it is sound.

**✓ Anchors confirmed:** `GridLayout.Quadrants` is a pure function returning exactly 4 rects (TL,TR,BL,BR),
zero-gap/zero-overflow — the single layout choke point. `GridForm` hard-codes 4 in `new MpvHost[4]`, the
build loop, `ApplyGrid`, and the D1–D4 hotkeys; its ctor already has `Config`. `Engine` sizes itself from
`Math.Min(views.Count, Streams.Count)` — count-agnostic. `Config.Normalize` caps all three parallel lists at
`.Take(4)`; legacy/unknown JSON keys are ignored on load, so old configs survive. The invariant + by-index
mirroring (`RenderApp.SetListItem` at `SetName`/`SetOverlayNamesOnly`/`ApplyStreams`) is exactly as the brief
describes, and `ApplyStreams` is positional + only reconnects changed cells.

**Finding 1 — `OverlayForm` is not quite "free."** `PaintOverlay` is array-driven (good), but `_quadrants` is
rebuilt in the **ctor** and in **`Reposition`** from `(width,height)` only — there is no count parameter, and
`SetFeeds` does not rebuild `_quadrants`. So generalizing tiling means **threading the active count into
`OverlayForm`** (add it to the ctor + `Reposition` signatures, or recompute `_quadrants` from `_feeds.Count`
inside `SetFeeds`). Small, but the brief's "minimal change / just needs the general rects" understates it —
pin the exact mechanism in the execution doc.

**Finding 2 — a real residual hard-coded 4 in the dashboard view model.** `ViewFrameBuilder.BuildFeeds`
returns **exactly four** grey placeholder feed rows when the grid is disconnected ("the form kept four rows").
The live `Tally` is count-agnostic, so this only bites the **down/offline** state — a 9-camera roster would
still show 4 grey rows while the grid is restarting. Cosmetic, not load-bearing, but concrete. Decide: collapse
the offline state to a single "grid down" indicator, or size the placeholders to the last-known / configured
roster count. (Note: while disconnected the dashboard process has no live snapshot — it would need a
last-known count, or to `Config.Load` its own `streams.json`, to size them correctly.)

**Precedence resolution (Q7) — the one genuinely subtle correctness point.** "The live engine is never
mutated" is true of the *engine*, but the live per-cell path **does mutate the render's in-memory `Config`**
(`Names`/`OverlayNamesOnly`/`Streams` by index) and persists on `Save` — i.e. the live path and the roster
Apply write the **same** `streams.json`. The clean, code-supported design: the dashboard **already receives
every live edit via telemetry** (`FeedSnapshot` carries `Name`/`NamesOnly`/`Url`), so the roster Apply should
send the dashboard's **full desired active arrays + pool**, and the render should **overwrite `Config`
wholesale**, `Save`, then close. Wholesale overwrite (a) preserves live edits the operator just made, and
(b) sidesteps the index-shift merge hazard (deactivating a middle cell renumbers everything; a positional
merge would misalign names). Close the residual "edited after the payload was built" race by **disabling live
per-cell edits once the roster editor is dirty**. Bake this exact resolution into the execution doc.

**Files I did NOT open (still must be verified):** `ControlProtocol.cs`, `FeedController*.cs`, `web/app.js`,
`web/index.html`, `web/style.css`. See §8.
