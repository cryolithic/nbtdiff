# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Status

Stages S0–S7 of `docs/PLAN.md` are done (2026-09-08); the app scans worlds, diffs regions, chunks,
NBT files and text, persists settings, and publishes as a single file. Open items are listed under
S8 in the plan. Tests: 532 (156 Nbt / 283 Core / 93 App).

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

## Layout

```
src/
  NbtDiff.Nbt/    vendored fNbt (fNbt/) + original loaders, SNBT parser/writer
  NbtDiff.Core/   FileClassifier, Fingerprinter, DirectoryComparer, Diff/ (NbtDiffer, RegionDiffer, aligners, LineDiffer)
  NbtDiff.App/    Avalonia 12: Tree/ (FlatTreeSource, ChangeCoalescer), Services/, ViewModels/, Views/
tests/            TestFixtures (WorldBuilder, RegionWriter), one test project per src project
third_party/NOTICE
```

`NbtDiff.Core` must stay UI-free so the engine is testable headlessly and a scriptable entry point
stays possible later.

## Build and run

```bash
dotnet build                                  # warnings are errors (Directory.Build.props)
dotnet run --project src/NbtDiff.App -- <left> <right>
dotnet test
dotnet test tests/NbtDiff.Core.Tests          # one project
dotnet publish src/NbtDiff.App -p:PublishProfile=win-x64   # publish/win-x64/nbtdiff.exe (also linux-x64)
NBTDIFF_DEMO_DIR=<dir> dotnet test tests/NbtDiff.App.Tests --filter DemoWorld   # demo world pair
NBTDIFF_PERF_DIR=<dir> dotnet test tests/NbtDiff.Core.Tests --filter PerfScan   # perf harness
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

- 2026-09-08 (S2): an empty `NbtList` reads back with `ListType == End` from binary but
  `Unknown` from SNBT. The canonical hasher ignores the element type of empty lists; the differ
  (S4) must do the same or hash-equal trees will show a diff.

- 2026-09-08 (S3): `CompareRoot.RowChanged` fires for the changed file row **and every
  ancestor** (counts change even when the ancestor's status doesn't), on worker threads. The UI
  coalescer must dedupe by row. Use `Prepare` → subscribe → `Run`, not `Start`, or early events
  are missed. `GatedFingerprinter` in `tests/NbtDiff.Core.Tests/ScanSupport.cs` freezes the
  pipeline mid-scan for deterministic `ProbablyDifferent` assertions; reuse it for view-model tests.
- 2026-09-08 (S4/S8): hash⇔diff agreement holds only with `IndexAligner`; `KeyedAligner`
  reports equality the hasher does not (pinned by a test). `DiffNode.Path` is slash-joined with
  list indices as their own segment: `Entities/[1]/Health`. Under `KeyedAligner` a right-only
  item is named by its *right* index, so paths are **not unique** — S6 navigation must key on
  node identity, not `Path`.
- 2026-09-08 (S3+S4 ran as parallel agents in git worktrees; both merged without conflict because
  S4 was told to define `ChunkDiffStatus` rather than depend on S3's `RowStatus`.)

- 2026-09-08 (S5): to see the app on a real world pair: `NBTDIFF_DEMO_DIR=<dir> dotnet test
  tests/NbtDiff.App.Tests --filter DemoWorld` writes `<dir>/left` and `<dir>/right`, then
  `nbtdiff.exe <left> <right>`. Screen capture on this 200 % display needs
  `SetProcessDpiAwarenessContext(-4)` + `PrintWindow(hwnd, hdc, 2)`; `CopyFromScreen` returns black.
- 2026-09-08 (S5): Avalonia 12 marks `TextBox.Watermark` obsolete (warning → build error here);
  use the replacement the XAML compiler names. `App.Tests` has `ImmediateUiDispatcher` (runs
  `Post` inline, `Tick()` simulates a coalescer flush) and `FakeDialogService`.

- 2026-09-08 (S6): injected input (`SendKeys`, `keybd_event`, `mouse_event`) never reaches the app
  here — `SetForegroundWindow` is refused — but `PostMessage(WM_KEYDOWN / WM_LBUTTONDOWN)` straight
  to the hwnd works. Script: scratchpad `s6-launch.ps1` (`-SendKeys "key:0x77;click:x,y"`,
  window-relative pixels at 200 %). Views must focus their grid on `Loaded` or keys are dead.
- 2026-09-08 (S6): the `DataGrid` selection brush overrides the row status tint on the selected
  row (cosmetic; the glyph still shows). `RegionCompareViewModel` keeps both `RegionFile`s open
  while a chunk view is on top of it; `MainWindowViewModel.Back` disposes `IDisposable` views.

- 2026-09-08 (S7): `CopyDebugSymbolFilesFromPackages=false` does not remove SkiaSharp/HarfBuzz
  native `.pdb`s (105 MB, they are native assets); `NbtDiff.App.csproj` strips them with a target
  after `ComputeResolvedFilesToPublishList`. Single-file output is 31 MB (win) / 26 MB (linux).
- 2026-09-08 (S7): perf numbers live in DESIGN §7; Tier 1 runs at page-cache speed (up to 3 GB/s
  on regions) with ~600 B/chunk retained. Launch/screenshot script for the UI:
  scratchpad `s7-launch.ps1` (`-Delay` ≥ 4 s or the window is not up; `PostKey` VK codes, PageDown
  0x22; `End` only moves columns in a DataGrid).

- 2026-09-08: real worlds contain 0-byte `.mca` files. `RegionFile.Open` treats them as empty
  regions (not errors) so they compare `Same`; only 1..8191-byte files are "truncated".

- 2026-09-08: chunk NBT carries `LastUpdate` (save tick), rewritten on every save. It is ignored
  by default through `TagIgnoreSet` (hasher, differ, region differ all consult the same set —
  DESIGN §4.3). `InhabitedTime` is *not* ignored (it only moves when players are nearby); users
  can add it in the "Ignore tags" box.

- 2026-09-08: keyed list matching is on by default everywhere (`CompareOptions.KeyedLists`,
  `AppSettings.UseKeyedAligner`); the hasher takes the same `KeyedAligner` so scan / grid / tag view
  agree. If a real world still shows a reordered list as changed, the item type is probably missing
  from `KeyedAligner.DefaultKeyNames` — extend the key list, don't special-case the differ.

## Related local repos

- `K:/git/nbt-studio` — the vendoring source (see above).
- `K:/git/minecraft_save_checker` — sibling C# CLI over fNbt that walks world folders and validates
  `level.dat`, `.mca`, and player data. Its `WorldScanner` (priority-sorted validators, concurrency
  bounded by a `SemaphoreSlim`) is a working example of parallel world traversal; worth reading
  before writing the scan phase.
- `K:/git/Amulet-Map-Editor`, `K:/git/RegionSearch` — other local Minecraft-format references.
