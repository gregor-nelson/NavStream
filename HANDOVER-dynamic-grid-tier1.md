# HANDOVER — Dynamic Grid (Tier 1: variable cell count, restart-based)

**Document type:** *planning brief.* This is **not** the execution doc. Your job, plan agent, is to
turn this brief into `HANDOVER-dynamic-grid-tier1-execution.md` — an implementable, anchor-based,
phase-by-phase doc with checkpoints — which then goes to a **coding agent for review and implementation**.
Do not write production code. Resolve the open questions (§6), pin every edit to a real anchor, and define
the verify gates.

Mirror the style of the existing execution docs referenced in memory
(`HANDOVER-mpv-migration-execution.md`: resolved decisions up front, finalized substitution/shape, N phases
each ending in a concrete checkpoint).

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
don't certify a 16-up wall until the GPU/decode question is answered on the real rig.

**Constraints (locked from prior sessions):** no git; build must stay green; `streams.json` lives beside
the exe; product/namespace is `MpvGrid`.

## 3. Locked scope & design decisions

These are decided — the plan agent should treat them as fixed and design around them:

1. **Restart-based, not live.** All roster/layout changes take effect on Restart Display (close render →
   supervisor relaunches → fresh `Config.Load()` → grid rebuilt). The **live engine is never mutated** by
   this feature.
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
(`RenderApp.cs:346`) and the live paths stay untouched.

Choke points to generalize (each currently hard-codes 4):

- `GridLayout.cs:17` — `Quadrants(w,h)` returns exactly 4 rects, 2×2 only, with a zero-gap / zero-overflow
  remainder-absorption contract. **The single layout choke point** — both consumers below just take an array
  of rects, so generalizing this one pure function flows to both video tiling and overlay badges.
- `GridForm.cs:14` `new MpvHost[4]`, ctor loop `:45`, `ApplyGrid` `:69` (calls `Quadrants`, loops `i<4`).
  Ctor already receives `Config` (`:30`), so it can read the active count. Overlay reposition hook at `:57`.
- `OverlayForm.cs` — `_quadrants` from `Quadrants` (`:52,:61,:98`); `PaintOverlay` already loops
  `i < _feeds.Count && i < _quadrants.Length` (`:135`). **Already array-driven** — just needs the general
  rects. Minimal change.
- `Engine.cs:30` — `count = Math.Min(views.Count, _config.Streams.Count)`. **Already count-agnostic.** No
  change expected; the cell count is driven by how many hosts `GridForm` builds.
- `GridForm.cs:155-158` — hotkeys `D1`–`D4`. Extend to `D1`–`D9`; cells 10–16 rely on the dashboard's
  per-card Reconnect button (`app.js:530`).
- `Config.cs:170-185` — `Normalize()` caps `Streams`/`Names`/`OverlayNamesOnly` at `.Take(4)`. Lift to 16,
  add the pool, keep dedupe/trim. `Load` `:124` / `Save` `:154` round-trip the whole `Config`; unknown/legacy
  keys are already ignored, so old `streams.json` stays compatible.

Already dynamic (little/no change, but **verify**):

- `web/app.js:451` `renderFeeds` already grows/shrinks the card list to `feeds.length`; `ControlSnapshot.Feeds`
  is an arbitrary-length `List` (`ControlProtocol.cs:70`). The dashboard read-side already scales to N.
- `ViewFrameBuilder.cs` — server-side SSE frame + the header **tally** (`f.tally.healthy/total`, consumed
  `app.js:296`). **Plan agent must read this file** (not yet reviewed) and confirm it derives everything from
  the feed list with no hard-coded 4. Likewise check `web/style.css` for any fixed 4-up assumption in the
  dashboard's own layout.

Control plumbing to extend:

- `ControlProtocol.cs:80` (`ControlCommands`) + `:97` (`ControlCommand` DTO) — add the roster command.
- `RenderApp.cs:182` `DispatchCommand` — add one case that writes Config + `Save()` + runs the existing
  `RestartDisplay` close path (`:191`). Reuse, don't reinvent.

Restart mechanism (verify, then rely on): `RestartDisplay` closes the form → process exits 0 → supervisor
relaunches → `Config.Load()`. **Plan agent: confirm in `Supervisor.cs` / `Program.cs` that relaunch re-reads
`streams.json` fresh** (the feature's atomicity depends on it).

## 5. Phase skeleton (plan agent fleshes each into anchor-based edits + a checkpoint)

1. **General tiling.** `GridLayout.Tile(int count, int w, int h)` → `count` zero-gap/zero-overflow rects;
   keep `Quadrants` as `Tile(4,…)` or inline. *Checkpoint: a pure-function check of rect coverage for
   n=1..16 (the existing "checkable without a window" property).*
2. **Variable cell count in the render window.** `GridForm` builds `Clamp(Streams.Count,1,16)` hosts;
   `ApplyGrid` + `OverlayForm` consume `Tile(count,…)`; hotkeys → D1–D9. *Checkpoint: hand-edit `streams.json`
   to 3 and 9 streams; launch; correct tiling, overlay alignment, per-feed reconnect.*
3. **Config schema + back-compat.** Lift caps to 16; add the inactive pool; `Normalize` handles it; old
   config loads unchanged. *Checkpoint: old 4-stream config loads; new fields round-trip.*
4. **Control protocol.** Add the `applyRoster` (or equivalent) command = write Config + Save + restart,
   atomic; live paths untouched. *Checkpoint: command persists + relaunches with the new roster.*
5. **Dashboard roster editor.** Add-stream button, remove/deactivate per card, an inactive-pool section
   (reactivate/delete), and one "Apply layout & restart" action with a blink warning. *Checkpoint: add /
   remove / deactivate / reactivate, Apply, wall rebuilds correctly.*

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
   it can't be confused with the live per-cell `save`+`streams` path.
6. **Atomicity / restart.** Confirm the supervisor re-reads `streams.json` on relaunch; define the exact
   write-then-close ordering and what happens if the write fails (don't restart on a failed save).
7. **Precedence vs the live path.** While a roster edit is pending in the dashboard, the operator can still
   live-edit a current cell (name/url). Define precedence so the two never corrupt `Config` (recommended:
   roster Apply snapshots the current live Config first, or the dashboard disables live per-cell edits once
   the roster editor is dirty).
8. **Empty roster edge case.** 0 active streams → clamp to min 1, or allow a black wall? Define it.
9. **Hotkeys.** D1–D9 only, dashboard for 10–16? (Recommend yes.)
10. **Dashboard UX specifics.** Exact placement in `index.html` (the Streams panel `:126-137`), add/remove
    vs deactivate semantics, reactivate-from-pool, whether active cells are reorderable, and the precise
    warning copy for the restart blink.
11. **Verify ViewFrameBuilder / style.css** are count-agnostic (read them; record findings).
12. **Retire grid-count.** Confirm the old `grid-count` handover/memory is superseded and say so.

## 7. What the execution doc must contain (acceptance for your output)

- A resolved-decisions header (answers to all of §6).
- The definitive n=1..16 tiling table.
- Per-phase, **anchor-based** edits (file + line/symbol + before/after intent), each ending in a concrete,
  runnable checkpoint.
- A final acceptance checklist + **verify gates**: `dotnet build MpvGrid.csproj` → 0 warnings / 0 errors;
  `node --check web/app.js`; a grep gate confirming no residual hard-coded `4`/`Quadrants` in the changed
  paths; and a manual launch matrix (1, 3, 9, 16 streams; add/remove/deactivate/reactivate; Apply→restart).
- The Phase-6 / decode-load risk note from §2.
- Explicit "live engine path untouched" assertion with the invariant from §4.

## 8. Files to read first

`GridLayout.cs`, `GridForm.cs`, `OverlayForm.cs`, `Engine.cs`, `Config.cs`, `RenderApp.cs`,
`ControlProtocol.cs`, `FeedController.cs`, `web/app.js`, `web/index.html`, **and the not-yet-reviewed**
`ViewFrameBuilder.cs`, `Supervisor.cs`, `Program.cs`, `web/style.css`.
