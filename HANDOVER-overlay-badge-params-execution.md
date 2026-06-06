# EXECUTION DOC — user-controllable health-overlay (badge) params in the dashboard

**Branch:** `overlay`. **Constraint:** edit + stage only, do NOT commit (no-auto-commit).
**Template:** the existing **Logo settings** feature (HANDOVER-logo-overlay-params-execution.md) — this mirrors it
layer-for-layer. **Scope:** 3 live params — **Opacity, Size, Position** — each a dashboard control in a new
**"Overlay settings"** popdown, **persisted via the existing Save button**. Defaults reproduce the current look
exactly. **Date:** 2026-06-06.

---

## What this controls

The **health overlay** — the per-cell badges/pills drawn by `OverlayForm.DrawBadge` and its three renderers:
- `DrawPill` — the frosted-glass nameplate (brand number badge + LED + vessel name + status word/metric).
- `DrawNamePill` — the "name only" pill (LED + vessel name).
- `DrawAmbient` — healthy-and-unnamed cells (just an LED + a faint cell number, no pill surface).

These are tuned today only by compile-time constants/derived geometry. Like the watermark, the overlay repaints via
`RenderNow()` with **no engine reconnect**, so every knob is **live**: drag → instant repaint, no rebuild, no restart.
This is *not* the brand watermark (`DrawWatermark`, the Logo settings) — that keeps its own four knobs and is left
untouched.

---

## The 3 params — ranges, mappings, defaults

| Param | Drives (existing geometry) | Wire (IntValue) | Config field (default) | Render mapping |
|---|---|---|---|---|
| **Opacity** | `PillFill` alpha (196) + the pill surface's shadow/sheen/border, in proportion | `setBadgeOpacity` 30–100 | `BadgeOpacityPct` = **77** | overlay `_badgeOpacity = pct/100f`; surface alphas ×`_badgeOpacity / 0.77` (1.0 at default → 196) |
| **Size** | the master `scale = clamp(quad.Height/540, .85, 2.2)` factor | `setBadgeSize` 60–160 | `BadgeSizePct` = **100** | overlay `_badgeScale = pct/100f`; `scale *= _badgeScale` (so `inset = 16*scale` rides along) |
| **Position** | the anchor corner (badge currently fixed top-left) | `setBadgePosition` 0–3 | `BadgePosition` = **0** (TL) | corner enum, see below |

**Why Opacity = 77 / range 30–100.** The current pill fill is `Color.FromArgb(196, …)`; `255 × 0.77 = 196`, so a
default of **77%** reproduces it exactly and the slider reads as a literal opacity percentage. The shadow (α16), sheen
(α36→0) and border (α64) alphas are scaled by the same factor (`_badgeOpacity / 0.77`, = 1.0 at default) so the whole
frosted-glass panel fades coherently. **Text, LEDs and the brand number badge stay full-strength** for legibility —
Opacity tunes the *surface*, not the content. `DrawAmbient` has no surface, so Opacity does not affect it (by design;
it is already minimal). Range 30–100 lets the operator go nearly ghostly (α≈76) up to fully solid (α≈254).

**Why Size multiplies `scale`.** Everything in the badge renderers is already derived from `scale` (paddings, fonts,
LED, badge square, pill height, **and** `inset = 16*scale`). Multiplying the one master factor scales the whole badge
uniformly, exactly as designed for 1080p→4K. The corner inset rides along (a bigger badge sits a touch further from the
edge) — coherent and a one-line change. Range 60–160% (0.6×–1.6×); caution noted below for tiny cells at large sizes.

**Position enum:** `0 = top-left (current default), 1 = top-right, 2 = bottom-right, 3 = bottom-left`. (Note: this
differs from the logo's enum, which starts at bottom-left — each control's default is its own natural starting corner,
so 0 = "no change". They are independent.) The badge top-left is `(quad.X + inset, quad.Y + inset)`; for the other
corners anchor by the *measured* pill size:
- TL (0) → `x = quad.X + inset`,            `y = quad.Y + inset`
- TR (1) → `x = quad.Right - inset - w`,    `y = quad.Y + inset`
- BR (2) → `x = quad.Right - inset - w`,    `y = quad.Bottom - inset - h`
- BL (3) → `x = quad.X + inset`,            `y = quad.Bottom - inset - h`

Right-anchored when corner ∈ {1,2}; bottom-anchored when corner ∈ {2,3}.

> **The one structural wrinkle vs the logo.** The watermark is a fixed-aspect image, so its `w/h` are known before
> drawing and corner placement is trivial. A badge's width is **dynamic** (depends on the vessel name + status text),
> computed *inside* the renderer. So Position requires a **measure-then-anchor** order: each renderer computes its
> `pillW/pillH` first (it already does), then derives `ox/oy` from the corner just before it lays the pill down — instead
> of receiving a pre-computed top-left. Mechanical, but it changes the three renderers' signatures (drop the incoming
> `ox/oy`, take `quad` + `inset` + the corner instead).

---

## The one protocol decision (identical to the logo feature)

`ControlCommand` has `BoolValue`/`IntValue`/`StringValue` but **no float**. Reuse the **existing `IntValue`
discrete-command pattern** — send each param as an **int percent / enum**, convert in the render. **No protocol-type
change.** Three independent commands, so a slider drag sends only its own value.

---

## Layer-by-layer wiring (mirrors the `setLogo*` thread exactly)

1. **`OverlayForm.cs`**
   - Add 3 private fields with the defaults: `_badgeOpacity` (0.77f), `_badgeScale` (1f), `_badgeCorner` (0), plus a
     `const float DefaultBadgeOpacity = 0.77f`.
   - Add 3 setters mirroring `SetLogoOpacity` etc.: `SetBadgeOpacity(float)`, `SetBadgeSize(float)`,
     `SetBadgePosition(int)` — each guards on unchanged → set field → `RenderNow()`.
   - `DrawBadge`: `scale = clamp(...) * _badgeScale;` (so `inset = 16*scale` scales too); keep
     `maxWidth = quad.Width - 2*inset`. Pass `quad` + `inset` (not the old `ox/oy`) down to the three renderers.
   - `DrawPill` / `DrawNamePill` / `DrawAmbient`: compute sizes as today, then derive `ox/oy` from the corner via a
     small `Anchor(quad, inset, w, h, out ox, out oy)` helper, and draw from there.
   - `DrawPillSurface` + `DrawShadow`: become instance methods (drop `static`) and scale the fill/shadow/sheen/border
     alphas by `_badgeOpacity / DefaultBadgeOpacity` (a clamped-byte helper keeps them in 0–255). At default → identical.

2. **`Config.cs`**
   - Add `BadgeOpacityPct` (77), `BadgeSizePct` (100), `BadgePosition` (0), next to the `Logo*` block.
   - In `Normalize()`: clamp opacity 30–100, size 60–160, position 0–3.
   - `Save()` already serializes Config → these persist automatically (same as `LogoOpacityPct`).

3. **`ControlProtocol.cs`**
   - `ControlCommands`: add `SetBadgeOpacity` / `SetBadgeSize` / `SetBadgePosition` name constants.
   - `VisualSnapshot`: add `BadgeOpacityPct`, `BadgeSizePct`, `BadgePosition` (mirror-back so the sliders self-populate).
   - **No `ViewFrameBuilder` change** — it forwards `last.Visual` wholesale, so the new fields reach the browser as
     `f.visual.badge*` automatically.

4. **`RenderApp.cs`**
   - `OnFormShown`: after `ApplyLogoParams()`, call a new `ApplyBadgeParams()` to push the persisted values into the overlay.
   - `BuildSnapshot`: populate the 3 new `VisualSnapshot` fields from `_config`.
   - `DispatchCommand`: 3 new `case`s, each mirroring the `SetLogoOpacity` case — clamp into `_config.*`, call
     `ApplyBadgeParams()`, `_server?.PushNow()`.
   - `ApplyBadgeParams()`: maps `_config.Badge*` → overlay setters (`/100f` for opacity & size; position as-is).

5. **`web/index.html`**
   - A second `<details class="settings-advanced overlay-advanced" id="overlay-advanced">` popdown in the Display panel
     (next to "Logo settings"), summary **"Overlay settings"**, containing 2 range sliders (Opacity / Size) with a `%`
     readout + a 4-way Position `<select>`. Reuse the existing `.logo-controls`/`.logo-field`/`.logo-val` layout classes.

6. **`web/app.js`**
   - `els`: add `overlayAdvanced`, `badgeOpacity`(+`Val`), `badgeSize`(+`Val`), `badgePosition`.
   - `wireControls`: throttled (~60 ms) `input` handlers → `setBadgeOpacity`/`setBadgeSize`; `change` → `setBadgePosition`
     (copy the logo slider block verbatim).
   - `renderDisplay`: mirror `v.badgeOpacityPct` / `v.badgeSizePct` / `v.badgePosition` into the controls (`setRange` +
     the select), and disable + grey them when the grid is down **or the overlay-badges switch is off**
     (`on && v.overlayEnabled`), mirroring the logo block's disable logic.

7. **`web/style.css`**
   - One line: extend the logo disabled-grey rule to the overlay popdown —
     `.overlay-advanced.is-disabled .logo-controls { opacity: 0.5; }`. Everything else reuses the logo/range styling.

---

## Persistence behavior

Identical to the logo knobs: live changes apply **immediately**; they persist to `streams.json` only on the existing
**Save** button. On next launch `Config.Load` → `OnFormShown` → `ApplyBadgeParams()` re-applies them. Defaults
(77/100/TL) reproduce the current appearance, so an un-Saved session looks unchanged after restart.

---

## Verification gates

1. `dotnet build NavStream.csproj -c Release` → **0/0**.
2. `node --check web/app.js` → clean.
3. Grep: each of `setBadgeOpacity` / `setBadgeSize` / `setBadgePosition` appears in `ControlProtocol.cs`,
   `RenderApp.cs` (a `case`), and `app.js` (a `send`).
4. **Live run (UAC, user launches):** open `▸ Overlay settings` → drag Opacity (pills fade/solidify), Size (badges
   grow/shrink), pick each corner (badges move TL→TR→BR→BL); toggle "Overlay badges" off → the 3 controls grey out.
   Default values must look identical to before.
5. **Persist:** Save → full exit → relaunch → controls restored, overlay matches.
6. **Round-trip:** a 2nd dashboard reflects the live values from the snapshot.

---

## Caution / risk

~3× the `setLogo` template plus the measure-then-anchor refactor of the three renderers (the only non-trivial bit; the
opacity/size knobs are one-liners). No engine touch; confined to overlay + protocol + dashboard. At extreme Size in a
small cell a long pill can overflow the quad (pre-existing risk, just likelier) — range capped at 160% and noted in the
UI. One republish to land; **zero rebuilds for tuning thereafter.**
