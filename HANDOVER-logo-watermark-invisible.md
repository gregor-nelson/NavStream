# HANDOVER — per-cell InterMoor logo watermark is invisible at runtime

**Branch:** `overlay` (cut from `dev`). Nothing committed (per the no-auto-commit rule — edit + stage only, user commits).
**Status:** feature code COMPLETE, builds GREEN (0/0), published to `dist\NavStream.exe`, isolated render tests PASS — **but the user still sees no logo on the live grid.** The health badges DO show.
**Date:** 2026-06-06.

---

## Mission for the fresh session

You are a **plan agent**. Produce a tight **execution doc** that:
1. Nails the **root cause** of why the watermark renders in isolation but is invisible on the live grid.
2. Specifies the **minimal fix** + exact verification steps.
3. Is ready to hand to a **coding agent** for review + implementation.

Do the diagnosis cheapest-first (below). Don't re-implement the feature — it's built and correct in isolation. The bug is *why it doesn't show in-process*.

---

## What was built (the feature)

A faint **InterMoor logo watermark in the bottom-left of every grid cell**, with a **dashboard on/off toggle** ("Brand logo" switch). Source logo `C:\Users\gregor\Pictures\getsitelogo.png` (308×195, blue mark + "INTERMOOR" wordmark on white) had its **white background stripped to straight alpha** (white→transparent + colour recovery) and saved to `assets\logo.png`.

Wired across 6 layers, mirroring the existing `setOverlay`/`OverlayEnabled` pattern exactly:

| Layer | File | What |
|---|---|---|
| Asset | `assets\logo.png` | white-stripped logo, 308×195, straight alpha |
| Embed | `NavStream.csproj` | `<EmbeddedResource Include="assets\logo.png">` with `<LogicalName>NavStream.logo.png</LogicalName>` |
| Loader | `LogoImage.cs` (NEW) | loads `NavStream.logo.png` once into a `Bitmap`; **catches all exceptions → returns null silently** |
| Render | `OverlayForm.cs` | `DrawWatermark(g)` (per-cell loop over `_quadrants`), `SetLogoVisible(bool)` + `_showLogo` field, tuning consts |
| Config | `Config.cs` | `public bool LogoEnabled { get; set; } = true;` |
| Protocol | `ControlProtocol.cs` | `VisualSnapshot.LogoEnabled`; `ControlCommands.SetLogo = "setLogo"` |
| App | `RenderApp.cs` | startup `_overlay.SetLogoVisible(_config.LogoEnabled)`; snapshot `LogoEnabled=...`; `case SetLogo`; `SetLogo(bool)` method |
| Web | `web\index.html` | `#logo` switch (reuses `.switch` CSS — no CSS change) |
| Web | `web\app.js` | `els.logo`; `change → send setLogo`; per-frame mirror `els.logo.checked = !!v.logoEnabled` |

### Watermark draw (the suspect code), `OverlayForm.cs`
- `PaintOverlay(g)` calls `DrawWatermark(g)` **FIRST**, then loops feeds drawing badges.
- `DrawWatermark`:
  ```
  if (!_showLogo) return;              // guard A
  var logo = LogoImage.Default;
  if (logo is null) return;            // guard B
  // ColorMatrix Matrix33 = 0.40 (alpha scale)
  foreach (var quad in _quadrants) { ... DrawImage(logo, bottom-left of cell, attrs); }
  ```
- Tuning consts: `WatermarkWidthFrac=0.20`, `WatermarkMinWidth=64`, `WatermarkMaxWidth=240`, `WatermarkOpacity=0.40`.
- Overlay render pipeline: `RenderNow()` → 32bpp ARGB bitmap via GDI+ → `Premultiply()` → `PushLayered()`/`UpdateLayeredWindow`. One layered, click-through, topmost window over the whole grid. Badges + watermark are in the **same bitmap**.

---

## The blocker — precise symptom

- **Badges show** on the live grid (user confirmed). **Logo never shows** — across ALL three iterations: (1) whole-wall always-on, (2) whole-wall + toggle, (3) per-cell + 40% opacity. **No crash.**

### The key narrowing argument (use this)
`DrawWatermark` is called **before** the badge loop and has **two early-return guards** (A: `!_showLogo`, B: `logo is null`). Since **badges render fine and nothing crashes**, `DrawWatermark` must be **returning without throwing**. Therefore the bug is necessarily ONE of:
- **(a)** the live grid is running a **different/older binary** than the one we keep fixing (badges yes, no logo code at all), OR
- **(b)** an **early-return guard fires** in-process — `logo is null` (B) or `_showLogo == false` (A), OR
- **(c)** `DrawImage` runs but produces nothing visible (position/opacity/premultiply).

**(c) is already disproven** (see "ruled out"). So prioritise **(a)** then **(b)**.

---

## What's already verified / RULED OUT (don't redo)

- **Resource embeds correctly:** `NavStream.logo.png` is present in `bin\Release\...\NavStream.dll` (and `bin\Debug`). Confirmed via reflection.
- **Image decodes + draws correctly in isolation:** Loaded the **published Release DLL** via `Assembly.LoadFile`, got `NavStream.logo.png`, decoded (308×195), called the **real** `NavStream.GridLayout.Tile(2/4, 1920, 1080)` via reflection, drew per-cell with the **same math + `ColorMatrix{Matrix33=0.40}`**. Result: the InterMoor mark appears bottom-left in **every** cell, clearly visible on a dark wall. So the asset, the resource name, the draw math, the ColorMatrix, and the cell rects are all correct **when run under PowerShell's .NET**.
- **ColorMatrix is identity+alpha** (`m00=m11=m22=m44=1`, `m33=0.40`) — RGB preserved, alpha scaled. Correct.
- **Rects are on-screen:** e.g. 1920×1080 / 2-up → logo at bottom-left of each half, fully within bounds.
- **Config default is ON:** `Config.LogoEnabled = true`; `dist\streams.json` has **no** `LogoEnabled` key, so deserialization keeps the `true` default. `OverlayEnabled: true` in that file (overlay is enabled).

So: in isolation everything is right. The gap is the **actual in-process render of the running binary**.

---

## Ranked hypotheses

### H1 (PRIMARY) — a stale/wrong binary is what's actually running
Found on disk:
- `bin\Release\net9.0-windows\win-x64\MpvGrid.exe` and `bin\Debug\...\MpvGrid.exe` — **leftovers from the MpvGrid→NavStream rename**; these predate the logo entirely.
- Four `NavStream.exe`: `dist\`, `bin\Debug\`, `bin\Release\`, `bin\Release\...\publish\`.

If a desktop shortcut, the supervisor, the tray relaunch path, or the user's launch habit points at `MpvGrid.exe` or a stale `NavStream.exe`, you get **badges-yes / logo-never / no-crash** permanently, regardless of dist rebuilds. **This single hypothesis explains every observation.**

**Diagnose:**
- With the grid running: `Get-Process NavStream,MpvGrid -ErrorAction SilentlyContinue | Select-Object Name,Id,Path,StartTime`
- Inspect any shortcuts the user launches from (Desktop/Start Menu/taskbar) — resolve their target paths.
- Check how the user actually starts it (ask). Confirm it's `dist\NavStream.exe` and not a leftover.
- Add a **build-marker log line at startup** (e.g. `Logger.Info($"NavStream build {File.GetLastWriteTime(exePath)} — logo watermark v3 per-cell")`) so the running build is unambiguous in `navstream.log`.
- Consider deleting/renaming the stale `MpvGrid.exe` copies to force the issue.

### H2 — `LogoImage.Default` is null in-process (silent catch)
`LogoImage.Load()` swallows **all** exceptions → returns null → guard B fires → badges still draw. Contradicted by the isolation test (same DLL resource decodes fine), but the **running process** is a different runtime context. Cheap to confirm with logging.

**Diagnose:** temporarily log inside `LogoImage.Load()` — on success `logo {w}x{h}`, in `catch` log the **exception** (currently silently eaten). Also log once in `DrawWatermark` whether `LogoImage.Default` is null.

### H3 — `_showLogo == false` in-process
Startup calls `_overlay.SetLogoVisible(_config.LogoEnabled)`. If `LogoEnabled` is somehow false at load (unexpected Config deserialization, or a `setLogo:false` command from the dashboard — note the web checkbox's HTML default is unchecked), guard A fires.

**Diagnose:** log `_config.LogoEnabled` at startup and `_showLogo` on first `DrawWatermark`. Verify how `Config` is loaded (does the loader ever reset booleans to false?). Check whether the dashboard emits `setLogo:false` on connect.

### H4 (LOW) — draws but invisible (position/opacity/premultiply)
Disproven by previews + isolation; per-cell 40% in both cells should be obvious. Only revisit if H1–H3 are all cleanly excluded. If so, check `_quadrants` contents at the moment the overlay actually paints (DPI/Bounds timing), and whether badges land where expected in **both** cells.

---

## Diagnostic plan (cheapest-first)

1. **Confirm the binary.** Resolve the exact .exe the live grid runs (process Path + StartTime; resolve shortcuts; ask the user how they launch). Add a startup build-marker log. → settles H1.
2. **Instrument the load + draw.** Add temporary logs: `LogoImage.Load` (success dims / caught exception), and first `DrawWatermark` call (`_showLogo`, `logo==null`, `_quadrants.Length`, first rect). → settles H2/H3, and confirms the draw executes.
3. **Rebuild + republish + run, then read `dist\navstream.log`** (or wherever `LogPath` resolves). The logs disambiguate definitively.
4. Write the execution doc: confirmed root cause → minimal fix → verification.

> Running the grid needs **UAC elevation** (app manifest is `requireAdministrator`) and a real screen; the user must launch it. Have them launch via `! <path>\NavStream.exe` in-session, or run it and report `navstream.log`. The grid was last torn down via a dashboard full-stop; it is not currently running.

---

## Key files & symbols

- `OverlayForm.cs` — `PaintOverlay`, `DrawWatermark`, `SetLogoVisible`/`_showLogo`, watermark consts; render pipeline `RenderNow`/`Premultiply`/`PushLayered`.
- `LogoImage.cs` — `Load()` (silent `catch`), resource name `"NavStream.logo.png"`.
- `NavStream.csproj` — `EmbeddedResource assets\logo.png` + `LogicalName NavStream.logo.png`.
- `RenderApp.cs` — overlay creation (`new OverlayForm(_form, _form.Bounds, ...)`), `SetLogoVisible` at startup, snapshot build, `case ControlCommands.SetLogo`, `SetLogo(bool)`.
- `Config.cs` — `LogoEnabled` (+ how Config is loaded/saved; find the loader).
- `ControlProtocol.cs` — `VisualSnapshot.LogoEnabled`, `ControlCommands.SetLogo`.
- `web\index.html`, `web\app.js` — `#logo` switch, `els.logo`, send + mirror.
- `Logger.cs` — logging API for instrumentation.
- `Supervisor.cs` / `TrayContext.cs` / `Signaller.cs` — how render/grid processes are launched + relaunched (relevant to H1: what path is spawned).

## Build / publish / run

```powershell
# build (fast sanity)
dotnet build "C:\Users\gregor\Downloads\Dev\mpv\NavStream.csproj"
# publish self-contained single file -> dist\NavStream.exe (~212 MB)
dotnet publish "C:\Users\gregor\Downloads\Dev\mpv\NavStream.csproj" -c Release -o "C:\Users\gregor\Downloads\Dev\mpv\dist"
# verify embedded resource in a built DLL
# [Reflection].GetManifestResourceNames() should contain 'NavStream.logo.png'
```

## Constraints / environment

- **Windows 10**, PowerShell, .NET 9 WinForms, single-file self-contained publish (`PublishSingleFile`/`SelfContained`/`win-x64`), native `libmpv-2.dll` embedded + self-extracted.
- App manifest = `requireAdministrator` (UAC every launch); dashboard on `http://<ip>:8080`, no password.
- **Work on `overlay` branch. Do NOT `git commit`/`push`** — edit + stage only; user commits. `main` is frozen; `dev` is the integration branch.
- Reuse the existing `setOverlay`/`OverlayEnabled` pattern for any protocol changes.

## Design intent (so the coding agent keeps it)

- Per-cell logo, **bottom-left of each cell** (mirrors the badge's top-left), scales with tiling (1-up…16-up), subtle (alpha 0.40), straight-alpha PNG over the per-pixel-alpha layered overlay. The white "M" rays are intentionally transparent (take the video colour underneath). Toggle = "Brand logo" dashboard switch, shares the overlay's `H` visibility. Tuning via the four `Watermark*` consts.
