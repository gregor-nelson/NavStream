# HANDOVER — "Logo settings" dashboard popdown (CODE-COMPLETE, needs live run)

**Branch:** `overlay`. **Date:** 2026-06-06. **Constraint:** edit + stage only — do NOT `git commit`/`push` (no-auto-commit; user commits).
**Status:** feature **code-complete**, all static gates **GREEN**. Only the live run (UAC + real screen → user launches) + a republish to `dist` remain. Nothing committed.

---

## What this is

Makes the InterMoor brand watermark **operator-tunable from the dashboard** instead of via compile-time consts. Four live knobs in an **inline popdown** in the Display panel (user explicitly chose a `<details>` popdown over a floating popup, "instead of bloating the primary UI surface"). Reuses the existing `.settings-advanced` disclosure idiom (chevron + uppercase summary), collapsed by default.

**Context that de-risked it:** the user confirmed they **have now seen the logo on the live grid** — so the long "invisible watermark" saga is resolved (it draws; it was always tuning/contrast, never absence). These sliders are the tuning fix. See memory `logo-watermark.md` (updated).

### The 4 params (all reuse the existing `IntValue` discrete-command pattern — no protocol float)

| Param | Command | Range | Config field (default) | Render mapping |
|---|---|---|---|---|
| Opacity | `setLogoOpacity` | 0–80 | `LogoOpacityPct` = 40 | `alpha = pct/100` |
| Brightness | `setLogoBrightness` | 0–100 | `LogoBrightnessPct` = 50 | `rgbLift = pct/100 * 0.5` (50→0.25, current look) |
| Size | `setLogoSize` | 5–30 | `LogoSizePct` = 15 | `widthFrac = pct/100` |
| Position | `setLogoPosition` | 0–3 | `LogoPosition` = 0 | corner: 0=BL,1=BR,2=TR,3=TL |

**Defaults (40/50/15/BL) reproduce the prior hardcoded look exactly** — nothing changes visually until a slider moves. Live repaint (drag → instant, no reconnect). Persisted **only on Save** (round-trips in `streams.json`). Top-left corner overlaps the health badge (allowed; flagged in the control's tooltip).

---

## Exactly what changed (all on `overlay`, uncommitted)

**My slider work (7 files):**
- `OverlayForm.cs` — `WatermarkWidthFrac/Opacity/Brightness` consts → live fields `_widthFrac/_opacity/_brightness` + new `_logoCorner`; kept `WatermarkMinWidth/MaxWidth` const. Added 4 guarded setters `SetLogoOpacity/Brightness/Size/Position` (each: guard-if-unchanged → set field → `RenderNow()`, mirroring `SetLogoVisible`). `DrawWatermark` reads the fields; ColorMatrix uses `_opacity`+`_brightness`; `x`/`y` chosen by `_logoCorner`.
- `Config.cs` — added `LogoOpacityPct(40)/LogoBrightnessPct(50)/LogoSizePct(15)/LogoPosition(0)`; clamps in `Normalize()` (0–80 / 0–100 / 5–30 / 0–3). `Save()` serializes automatically.
- `ControlProtocol.cs` — `VisualSnapshot` += 4 mirror fields (sliders self-populate); `ControlCommands` += 4 command-name consts.
- `RenderApp.cs` — `ApplyLogoParams()` helper (maps pct/enum → render units; each overlay setter no-ops if unchanged so one knob = one repaint) called in `OnFormShown` after `SetLogoVisible`; `BuildSnapshot` populates the 4 `Visual` fields; `DispatchCommand` 4 new cases (clamp into `_config` → `ApplyLogoParams()` → `_server?.PushNow()`).
- `web/index.html` — `<details class="settings-advanced logo-advanced" id="logo-advanced">` in the Display panel: 3 `<input type="range">` (+`%` readouts) and a position `<select>`, each with a tooltip.
- `web/style.css` — `.logo-controls` grid + `.logo-field` + `.logo-val` + `.logo-advanced.is-disabled` greying + themed `input[type="range"]` (webkit/moz thumb).
- `web/app.js` — `els` for the 5 controls; `throttle()` (leading+trailing, ~60ms) + `setRange()` helpers; live `input` handlers (instant readout + throttled send) and position `change`; per-frame mirror in `renderDisplay` (guarded by `activeElement`); disable+grey when grid down OR logo off.

**Pre-existing logo-watermark feature on the same branch (also uncommitted — do NOT lose):** `NavStream.csproj` (embeds `assets/logo.png` as `NavStream.logo.png`), `LogoImage.cs` (new loader), `assets/` (new, the white-stripped logo), plus the `setLogo` toggle wiring already in the 4 C# files + web. `ViewFrameBuilder.cs` needs **no change** — `ViewFrame.Visual` embeds the whole `VisualSnapshot`, so the new fields pass straight through to `f.visual`.

`git status` at handover: `M Config.cs ControlProtocol.cs NavStream.csproj OverlayForm.cs RenderApp.cs web/app.js web/index.html web/style.css` · `?? HANDOVER-*.md LogoImage.cs assets/`.

---

## Verification — static gates DONE (all GREEN)

1. `dotnet build "C:\Users\gregor\Downloads\Dev\mpv\NavStream.csproj" -c Release` → **0 warn / 0 err**.
2. `node --check web/app.js` → clean.
3. grep: each of the 4 command names appears in `ControlProtocol.cs` (const), `RenderApp.cs` (a `case`), `web/app.js` (a `send`). No leftover `Watermark{WidthFrac,Opacity,Brightness}` refs in code (only in the two markdown handovers). All `logo-*` HTML IDs match the `els` lookups.

## What REMAINS (the only open work)

1. **Republish to dist** (the running grid launches `dist\NavStream.exe`, a ~212MB single-file — the Release build above is NOT what runs):
   `dotnet publish "C:\Users\gregor\Downloads\Dev\mpv\NavStream.csproj" -c Release -o "C:\Users\gregor\Downloads\Dev\mpv\dist"`
2. **Live run (user launches — UAC `requireAdministrator` + real screen):**
   - Open `▸ Logo settings`; drag each slider → watermark repaints instantly; pick each corner → logo moves; toggle Brand logo off → the 4 controls disable + grey.
   - **Persist:** Save → full app exit → relaunch → all 4 controls restored to saved values; watermark matches.
   - **Round-trip (optional):** open a 2nd dashboard → its sliders reflect the live values from the snapshot.
3. **Commit decision (user's):** the whole `overlay` branch (logo watermark + this popdown) is uncommitted. Honor no-auto-commit — hand the commit back to the user.

## Constraints / env
Windows 10, PowerShell, .NET 9 WinForms, single-file self-contained publish. Dashboard at `http://<ip>:8080`, no password. Work on `overlay`; `main` frozen, `dev` is integration. Spec this implements: `HANDOVER-logo-overlay-params-execution.md`. Memory: `logo-watermark.md`, `no-auto-commit.md`.
