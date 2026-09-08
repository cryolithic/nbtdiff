# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Status

**This repository is empty.** Nothing has been written yet. This file records the decisions and
research that precede the first commit, so the first implementation session does not have to
re-derive them. Update it — especially "Planned layout" and the build commands — as soon as real
code lands.

## Where to look first

- `docs/DESIGN.md` — architecture and the public contracts each project exposes.
- `docs/PLAN.md` — build stages S0–S8 with dependencies, deliverables, and acceptance tests. Pick
  up the lowest unfinished stage; check its "Needs" line before starting.

## Project Overview

nbt-diff compares Minecraft NBT data. Two use cases drive the design:

1. **File diff** — two `.dat`/`.mca`/`.snbt` files, shown as a tag-level tree diff.
2. **Directory diff** — two save/world directories, compared recursively, modeled on Beyond
   Compare's folder diff.

The directory diff is the hard case. A world holds millions of tags, so the folder scan must do a
**cheap equality check only** (size, then a content hash) and render a two-pane file tree with
per-row states — same / differ / left-only / right-only. Expensive tag-level diffing happens
lazily, only when the user opens one row.

This is why the app is not a pure CLI: dumping a world diff to stdout is unusable.

## Decisions already made

| Decision | Choice | Why |
| --- | --- | --- |
| UI | **Avalonia** | Must run on Windows *and* Linux. Closest to Beyond Compare (split panes, sortable columns, colored row states). |
| Language/runtime | C# / .NET | Forced by reusing nbt-studio. Target `net10.0` (plain, never `-windows`). Only the .NET 10 SDK/runtime is installed here; there is no .NET 9 runtime, so do not target it. |
| nbt-studio reuse | **Vendor a trimmed copy** | Gives a headless data layer with no WinForms coupling. Cost: no upstream updates; that is accepted. |

Rejected: WinForms (Windows-only), Terminal.Gui (lower fidelity than the Beyond Compare model),
`ProjectReference`/submodule against nbt-studio (drags in `NbtStudio.csproj`'s WinForms dependency).

## Planned layout

```
src/
  NbtDiff.Nbt/    vendored fNbt + trimmed loaders (NbtFile, NbtFolder, RegionFile, Chunk)
  NbtDiff.Core/   scan, hash, diff engine — no UI references
  NbtDiff.App/    Avalonia shell
tests/
third_party/NOTICE  attribution for tryashtar (nbt-studio, fNbt fork)
```

`NbtDiff.Core` must stay UI-free so the engine is testable headlessly and a scriptable entry point
stays possible later.

## Build and run

```bash
dotnet build                                  # warnings are errors (Directory.Build.props)
dotnet run --project src/NbtDiff.App -- <left> <right>
dotnet test
dotnet test tests/NbtDiff.Core.Tests          # one project
dotnet test --filter "FullyQualifiedName~RegionFileTests"   # single test class
dotnet test --filter "FullyQualifiedName~RegionFileTests.LoadsLazily"   # single test
```

## Vendoring from nbt-studio

Upstream is checked out at `K:/git/nbt-studio` (this path is a local convenience, not a build
dependency — vendored code lives in this repo). Findings from inspecting it:

**What was copied (S1, done).** Only `fNbt/fNbt/*.cs` → `src/NbtDiff.Nbt/fNbt/`, with the undo
machinery stubbed out (DESIGN §3.1). Everything else under `src/NbtDiff.Nbt/` is original.

**What NOT to copy.** `NbtStudio/NbtUtil.cs` is the one file in the loading layer that references
`System.Windows.Forms`/`System.Drawing`. Everything under `NbtObjects/` is already WinForms-free —
verified by grep — which is what makes this vendoring cheap. Also skip `Models/`, `UI/`, `HexBox/`,
`TreeViewAdv/`: all WinForms.

**Gotchas found in the upstream source, each of which needs fixing while trimming:**

- `RegionFile.Load()` (`NbtObjects/RegionFile.cs`) calls `Chunks[x,z].Load()` for **every** chunk
  inside the scan loop. A commented-out `if (ChunkCount == 1)` shows this was once a
  load-the-first-chunk-only sanity check. As written it decompresses all ~1024 chunks just to open a
  region file. **This must be made lazy** or the folder scan is dead on arrival — the whole point of
  the scan phase is to avoid decompressing anything.
- `RegionFile.Load()` throws `FormatException` when `ChunkCount == 0`. A valid but empty region file
  is a legitimate thing to find in a world; it must not surface as a parse error in a diff.
- The vendored fNbt is a **fork**, not upstream fNbt — it carries `UndoableAction.cs` and editing
  hooks woven into the tag model. A diff tool needs no undo; strip it, but check what
  `NbtObjects` still calls before deleting.
- fNbt's `OrderedDictionary.cs` means `NbtCompound` preserves key order. Decide deliberately whether
  reordered-but-equal compounds count as a difference (they probably should not; NBT lists, by
  contrast, are genuinely ordered).
- **Licensing, verified 2026-09-08:** the fNbt fork carries upstream fNbt's BSD-3-Clause
  (`fNbt/docs/LICENSE`, recorded in `third_party/NOTICE`). `nbt-studio`, `utils.nbt`, and
  `utils.utility` publish **no license at all** — no LICENSE file, GitHub detects none — so by
  default they are all-rights-reserved. Consequences: the loaders (`NbtDocument`, `RegionFile`,
  `ChunkRef`) are written fresh, not copied; copying `SnbtParser`/`SnbtMaker` or `utils.utility`
  verbatim is **not** cleared. S1 should either write the SNBT parser (the grammar is small) or
  the user obtains permission from tryashtar first.

## Diff engine notes

- **Region files** (`.mca`) are 4096 bytes of chunk locations, then 4096 bytes of timestamps, then
  chunk data. **Exclude the timestamp table from content comparison** — timestamps change when a
  chunk is rewritten with identical content, which would report every visited chunk as different.
  The same reasoning applies to filesystem mtimes.
- Chunk sector offsets shift when a world is defragmented, so raw byte-hashing an `.mca` produces
  false differences. Hash **per-chunk decompressed payloads**, and treat a region as equal when
  every chunk payload matches.
- Region coordinates come from the `r.<x>.<z>.mca` filename (`RegionFile.CoordsRegex`); pair files
  across directories by name, not by scan order.
- nbt-studio's loaders return `IFailable<T>` rather than throwing. Worlds routinely contain corrupt
  files, so the diff must render a per-row error state instead of aborting the scan.

## Findings

Things discovered while building that are not visible from the code. Append here; newest last.

- 2026-09-08 (S0): Only the .NET 10 runtime is installed (plus 6 and 8); there is no 9. Target
  `net10.0`.
- 2026-09-08 (S0): Avalonia is at 12.x. `Avalonia.Controls.TreeDataGrid` 12.x is a commercial
  product — the build fails with `AVLIC0001` without a license key — and even 11.3.2 ships no
  license metadata. Avalonia 12 core, `Avalonia.Controls.DataGrid`, and the Fluent theme are MIT.
  The design uses `DataGrid` over a flattened tree (`DESIGN.md` §5.0) instead.

- 2026-09-08 (S1): `HashCode.Combine` is randomized per process. Test fixtures that must be
  deterministic across runs use their own `StableHash`; never seed fixture RNGs from it.
- 2026-09-08 (S1): fNbt `NbtByte.Value` is unsigned; Minecraft bytes are signed. Display, hash
  and SNBT code must go through `(sbyte)` or a `-1b` becomes `255b`.
- 2026-09-08 (S1): a truncated compressed chunk can still parse (DeflateStream tolerates cut-off
  input; fNbt stops at the first TAG_End). Do not use "parses OK" as a corruption check.
- 2026-09-08 (S1): xunit 2.9.3 runs test classes in parallel; fixtures must not share temp paths
  (`TempDir` gives each test its own directory).

## Related local repos

- `K:/git/nbt-studio` — the vendoring source (see above).
- `K:/git/minecraft_save_checker` — sibling C# CLI over fNbt that walks world folders and validates
  `level.dat`, `.mca`, and player data. Its `WorldScanner` (priority-sorted validators, concurrency
  bounded by a `SemaphoreSlim`) is a working example of parallel world traversal; worth reading
  before writing the scan phase.
- `K:/git/Amulet-Map-Editor`, `K:/git/RegionSearch` — other local Minecraft-format references.
