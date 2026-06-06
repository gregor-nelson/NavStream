# EXECUTION DOC — user-controllable logo-overlay params in the dashboard

**Branch:** `overlay`. **Constraint:** edit + stage only, do NOT commit (no-auto-commit).
**Scope (confirmed with user):** 4 live params — **Opacity, Brightness, Size, Position** — each a dashboard control, **persisted via the existing Save button**. Defaults preserve the current look exactly.
**Date:** 2026-06-06.

---

## Background (why this is low-risk)

The watermark render path is already **proven correct end-to-end** (see HANDOVER-logo-watermark-invisible.md findings: it loads, draws at the right alpha, and rides the same `UpdateLayeredWindow` push as the health badges). The only reason these can't be tuned today is that they're compile-time `const`s in `OverlayForm.cs`. The overlay repaints via `RenderNow()` on demand with **no engine reconnect**, so every one of these is a **live** change: drag → instant repaint, no rebuild, no restart.

This work also **supersedes the two hardcoded tweaks** currently staged in `OverlayForm.cs` (`WatermarkBrightness = 0.25f`, `WatermarkWidthFrac = 0.15f`): those values become the slider **defaults** rather than fixed constants.

---

## The 4 params — ranges, mappings, defaults

| Param | Overlay field (was const) | Wire (IntValue) | Config field (default) | Render mapping |
|---|---|---|---|---|
| **Opacity** | `WatermarkOpacity` (0.40) | `setLogoOpacity` 0–80 | `LogoOpacityPct` = **40** | `opacity = pct / 100f` |
| **Brightness** | `WatermarkBrightness` (0.25) | `setLogoBrightness` 0–100 | `LogoBrightnessPct` = **50** | `translation = (pct/100) * 0.5f` (so 50% → 0.25, current look) |
| **Size** | `WatermarkWidthFrac` (0.15) | `setLogoSize` 5–30 | `LogoSizePct` = **15** | `widthFrac = pct / 100f` (px min/max clamps stay internal) |
| **Position** | *(new)* `_logoCorner` | `setLogoPosition` 0–3 | `LogoPosition` = **0** (BL) | corner enum, see below |

**Position enum:** `0=bottom-left (current), 1=bottom-right, 2=top-right, 3=top-left`. In `DrawWatermark`, pick `x`/`y` from the corner (keep `inset`):
- BL → `x = quad.X + inset`, `y = quad.Bottom - inset - h`
- BR → `x = quad.Right - inset - w`, `y = quad.Bottom - inset - h`
- TR → `x = quad.Right - inset - w`, `y = quad.Y + inset`
- TL → `x = quad.X + inset`, `y = quad.Y + inset`
> ⚠ **TL overlaps the health badge** (badge sits top-left). Allowed (operator's choice) but call it out in the UI tooltip.

---

## The one protocol decision

`ControlCommand` has `BoolValue`/`IntValue`/`StringValue` but **no float**. Reuse the **existing `IntValue` discrete-command pattern** (identical to `setMonitor`) — send each param as an **int percent / enum**, convert in the render. **No protocol-type change.** All four are independent commands so a slider drag sends only its own value.

---

## Layer-by-layer wiring (mirrors the existing `setLogo` thread)

1. **`OverlayForm.cs`**
   - Promote the 3 consts → private fields (same default values); add `_logoCorner` field (default BL).
   - Add 4 setters: `SetLogoOpacity(float)`, `SetLogoBrightness(float)`, `SetLogoSize(float)`, `SetLogoPosition(int)`. Each: guard if unchanged → set field → `RenderNow()` (same shape as the existing `SetLogoVisible`).
   - `DrawWatermark`: read the fields instead of consts; `ColorMatrix` uses `_opacity` + `_brightness` (the `Matrix33` + `Matrix40/41/42` already added); compute `x`/`y` by `_logoCorner`. Keep `WatermarkMinWidth`/`WatermarkMaxWidth`/inset as consts.

2. **`Config.cs`**
   - Add `LogoOpacityPct` (40), `LogoBrightnessPct` (50), `LogoSizePct` (15), `LogoPosition` (0).
   - In `Normalize()`: clamp opacity 0–80, brightness 0–100, size 5–30, position 0–3.
   - `Save()` already serializes Config → these persist automatically (same as `LogoEnabled`).

3. **`ControlProtocol.cs`**
   - `VisualSnapshot`: add `LogoOpacityPct`, `LogoBrightnessPct`, `LogoSizePct`, `LogoPosition` (mirror-back so sliders self-populate).
   - `ControlCommands`: add the 4 command-name constants.

4. **`RenderApp.cs`**
   - `OnFormShown` (startup): after `SetLogoVisible`, apply the 4 persisted Config values to the overlay via the new setters.
   - `BuildSnapshot`: populate the 4 new `VisualSnapshot` fields from `_config`.
   - `DispatchCommand`: 4 new `case`s. Each mirrors the existing `SetLogo` helper — update `_config.*` (keep Config in sync), call the overlay setter, `_server?.PushNow()`. Clamp on the way in.

5. **`web/index.html`**
   - Under the **Brand logo** switch (Display panel): 3 `<input type="range">` (opacity/brightness/size) with label + live `%` readout, and a 4-way **Position** control (segmented buttons or `<select>`). Reuse existing `.control-field` styling.

6. **`web/app.js`**
   - `els`: add the 4 inputs.
   - `wireControls`: range inputs fire on **`input`** (live), **throttled ~50 ms**, sending `setLogoOpacity/Brightness/Size` with `intValue`; position fires on `change` → `setLogoPosition`.
   - Frame mirror (where `els.logo.checked = !!v.logoEnabled`): set each input's `.value` from `v.logoOpacityPct` etc. and reflect the position selection. Echo-safe — setting `.value` programmatically doesn't fire `input`/`change` (see app.js comment ~line 136).
   - Optional polish: disable/grey the 4 controls when the logo toggle is off.

---

## Persistence behavior

Live changes apply **immediately**; they persist to `streams.json` only when the operator hits the existing **Save** button (consistent with vessel names / source URLs). On next launch `Config.Load` → `OnFormShown` re-applies them. Defaults (40/50/15/BL) reproduce the current appearance, so an un-Saved session looks unchanged after restart.

---

## Verification gates (for the coding agent)

1. `dotnet build NavStream.csproj -c Release` → **0/0**.
2. `node --check web/app.js` → clean.
3. Grep: each command-name constant appears in `ControlProtocol.cs`, `RenderApp.cs` (a `case`), and `app.js` (a `send`).
4. **Live run (UAC, user launches):** drag each slider → watermark repaints instantly; pick each corner → logo moves; toggle logo off → controls disable & logo vanishes.
5. **Persist:** Save → full app exit → relaunch → all 4 controls restored to saved values; watermark matches.
6. **Round-trip:** reconnect a second dashboard → controls reflect the live values from the snapshot.

---

## Effort / risk

~4× the `setLogo` template — mechanical, no engine touch, all changes confined to the overlay + protocol + dashboard. One republish to land; **zero rebuilds for tuning thereafter.** Low risk: the render path is already proven; this only parameterizes existing knobs.
