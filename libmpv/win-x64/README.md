# Pinned native libmpv (win-x64)

> **`libmpv-2.dll` is NOT committed to git** — at ~112 MB it exceeds GitHub's
> 100 MB per-file limit, so it is `.gitignore`d. After cloning you must download
> the pinned build yourself and drop `libmpv-2.dll` in this folder before
> building. Use the **Source (URL)** and **`libmpv-2.dll` SHA-256** in the
> *Pinned build* table below to fetch and verify the exact file:
>
> ```powershell
> (Get-FileHash libmpv\win-x64\libmpv-2.dll -Algorithm SHA256).Hash -eq `
>   '5f8ab38629ed99d9612f7a274f8c157337bbb6c14134b058abcbe60b9082a0b6'
> ```

This folder holds the **pinned** `libmpv-2.dll` that the app P/Invokes (D1/D2 in
`HANDOVER-mpv-migration-execution.md`). It is loaded at runtime by
`NativeLibrary.SetDllImportResolver` from
`AppContext.BaseDirectory\libmpv\win-x64\libmpv-2.dll` (see `Mpv\MpvNative.cs`, built in Phase 1),
and copied next to the published exe by the `<None>` item in `MpvGrid.csproj`.

**Do not bundle this into the single-file exe** — `IncludeNativeLibrariesForSelfExtract=false`
keeps it loose so the resolver's fixed path can find it.

## Requirement

- `MPV_CLIENT_API_VERSION >= 2.1` (i.e. `mpv_client_api_version() >= 0x20001`).
- 64-bit (`win-x64`), single self-contained DLL (all codecs static-linked — the `mpv-dev` package form).

## Pinned build (Phase 0, verified by the smoke probe)

| Field | Value |
|---|---|
| Source (URL) | https://github.com/zhongfly/mpv-winbuild/releases/tag/2026-06-04-1d82932cce |
| Package file | `mpv-dev-x86_64-20260604-git-1d82932cce.7z` |
| Package `.7z` SHA-256 (publisher-published, verified) | `afaca115c5fdfd8aae9424e013ee7b50528516ef7b12e36ab19c1958425e4774` |
| mpv version (`mpv-version`) | `mpv v0.41.0-718-g1d82932cc` |
| `mpv_client_api_version()` | `0x20005` (**2.5** — satisfies >= 2.1) |
| Build date | 2026-06-04 |
| `libmpv-2.dll` size | 117,933,056 bytes |
| `libmpv-2.dll` SHA-256 | `5f8ab38629ed99d9612f7a274f8c157337bbb6c14134b058abcbe60b9082a0b6` |
| Pinned on | 2026-06-05 |

### Note on "stable"

There is **no release-tagged, single-DLL libmpv-2.dll** published for Windows. The official
`mpv-player/mpv` releases (e.g. `v0.41.0`) ship the *player* (`mpv.exe` + ~25 loose dependency DLLs)
and contain **no `libmpv-2.dll`**. The only single self-contained `libmpv-2.dll` comes from the
community `mpv-dev-x86_64` packages (shinchiro / zhongfly), which are dated git-master snapshots.
This pin is therefore a **hash-pinned master snapshot** (mpv 0.41.0 + 718 commits) — "stable" in the
reproducible sense (it never moves), built on the most recent 2026 build at pin time.

To re-pin: drop a newer `mpv-dev-x86_64-*` `libmpv-2.dll` here and update this table
(`Get-FileHash libmpv\win-x64\libmpv-2.dll -Algorithm SHA256`). Phase 5 telemetry property spellings
must be re-verified against whatever build is pinned.
