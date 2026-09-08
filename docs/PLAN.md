# nbt-diff — Build Plan

Stages are sized to be handed to one agent each. Every stage states what must already exist, what
it produces, and the tests that define "done". `docs/DESIGN.md` holds the contracts; a stage that
needs to change a contract updates DESIGN.md in the same change and says so in its report.

## Dependency graph

```
S0 scaffold
 └─ S1 vendor Nbt layer ──┬─ S2 classify + fingerprint ── S3 directory scan ──┐
                          │                                                   ├─ S5 folder view
                          └─ S4 tag/region diff ─────────────────────────────┤
                                                                              └─ S6 file/region views
                                                                                   └─ S7 polish + packaging
                                                                                        └─ S8 optional extras
```

Parallelizable once S1 lands: **S2→S3** and **S4** are independent of each other. **S5** can start
against S3's interfaces with a fake `IFingerprinter` before S3 is complete. S6 needs both S4 and S5.

## Rules for every stage

- Work only inside the projects the stage names. Touching another project is a contract change and
  must be called out.
- `dotnet build` with zero warnings and `dotnet test` green on the stage's test projects before
  reporting done.
- No new NuGet packages beyond those listed in DESIGN.md §2 without saying why.
- Public types get XML doc comments only where the behavior is not obvious from the name; match
  the density of the surrounding vendored code otherwise.
- Report back: files added/changed, any DESIGN.md deviations, anything discovered that later
  stages need to know (append it to CLAUDE.md under a "Findings" heading).

---

## S0 — Repository scaffold

**Status:** done 2026-09-08.

**Needs:** nothing.

**Produces**

- `nbtdiff.sln`, `Directory.Build.props` (net10.0, `Nullable` + `ImplicitUsings` enabled,
  `TreatWarningsAsErrors`, `LangVersion latest`), `.editorconfig`, `.gitignore` for .NET.
- Empty projects: `src/NbtDiff.Nbt`, `src/NbtDiff.Core`, `src/NbtDiff.App` (Avalonia desktop
  template, Fluent theme, one blank window), `tests/NbtDiff.TestFixtures`, `tests/NbtDiff.Nbt.Tests`,
  `tests/NbtDiff.Core.Tests`, `tests/NbtDiff.App.Tests` (xunit). Project references exactly as
  DESIGN.md §2.
- `third_party/NOTICE` with the license text for nbt-studio and the fNbt fork, fetched from
  upstream GitHub (the local checkout has no LICENSE file — verify what upstream actually uses,
  do not assume).
- `.github/workflows/ci.yml`: build + test on `windows-latest` and `ubuntu-latest`.
- `README.md`: one paragraph and the build commands. Replace the placeholder commands in
  CLAUDE.md with the real ones.

**Done when:** `dotnet build` and `dotnet test` succeed on a clean clone; CI passes on both OSes;
`dotnet run --project src/NbtDiff.App` opens a window on Windows.

---

## S1 — Vendor the NBT data layer

**Status:** done 2026-09-08. Deviations: SNBT written fresh (upstream has no license); no
`Utility/` directory (same reason); `ChunkRef` exposes `SchemeByte`/`IsExternal`/`Compression` as
nullable-until-read and `ReadCompressedPayload` returns `LoadResult<byte[]>`. DESIGN §3 updated.

**Needs:** S0. Source: `K:/git/nbt-studio` (read-only; never reference it from the build).

**Produces** in `src/NbtDiff.Nbt/`

- `fNbt/` — copied from `nbt-studio/fNbt/fNbt/*.cs` with the changes in DESIGN.md §3.1.
  Keep the `fNbt` namespace.
- `Snbt/` — `SnbtParser`, `SnbtWriter`, `SnbtOptions`, `SnbtParseException`: written from the
  format, not copied (upstream `utils.nbt` is unlicensed).
- `NbtDocument.cs`, `RegionFile.cs`, `ChunkRef.cs`, `RegionCoords.cs`, `NbtFormat.cs`,
  `LoadResult.cs` — original code implementing DESIGN.md §3.2. The detection heuristics
  (try several formats, prefer a non-suspicious parse) and the compression-byte handling
  (bit 7 = external `.mcc`) follow upstream's behavior.

**Produces** in `tests/NbtDiff.TestFixtures/`

- `NbtFixtures`: builders for standalone documents in every `NbtFormat`.
- `RegionWriter`: writes a valid `.mca` from a set of `(x, z, NbtCompound)` with control over
  compression, timestamps, sector placement (to simulate defragmentation), and zero-chunk files.
- `WorldBuilder` as in DESIGN.md §6, including `Mutate`, `Recompress`, `TouchTimestamps`,
  `Defragment`.

**Tests** (`NbtDiff.Nbt.Tests`)

- Round-trip each `NbtFormat` through the fixtures and `NbtDocument.Load`; assert `Format` is
  detected correctly, including the Bedrock header case.
- `RegionFile.Open` on a 1024-chunk region performs no chunk reads: assert via a counting
  `Stream` wrapper that exactly 8192 bytes are read.
- Zero-chunk region → `Ok`, `ChunkCount == 0`.
- One corrupt slot (offset past EOF) → file opens, that `ChunkRef.ReadNbt()` fails, siblings read.
- External chunk → `IsExternal` true; `ReadNbt` reads the `.mcc`.
- `.mcr` extension opens.

**Done when:** tests green; `NbtDiff.Nbt` has no reference to `System.Windows.Forms`,
`System.Drawing`, or `UndoableAction` (grep is the test).

---

## S2 — Classification and fingerprinting

**Needs:** S1.

**Produces** in `src/NbtDiff.Core/`

- `FileClassifier` (DESIGN §4.1).
- `NbtCanonicalHasher` (§4.3).
- `Fingerprinter : IFingerprinter` (§4.2) with `QuickAsync` and `DeepAsync`. Region quick pass
  uses `ChunkRef.ReadCompressedPayload` with a pooled buffer; nothing is decompressed.
  Deep pass uses `ChunkRef.ReadNbt` + canonical hash.

**Tests** (`NbtDiff.Core.Tests`)

- Classification table test over the world layout in §4.1.
- Canonical hash: identical trees with shuffled compound key order hash equal; a single scalar
  change anywhere changes the hash; list reorder changes the hash; `0.0` vs `-0.0` differ.
- Quick fingerprint of a region is unchanged by `TouchTimestamps` and `Defragment`; changed by
  `Recompress`. Deep fingerprint is unchanged by all three; changed by `Mutate`.
- Quick fingerprint of a region never decompresses (counting-stream assertion as in S1).

**Done when:** tests green.

---

## S3 — Directory scan and comparison tree

**Needs:** S2.

**Produces** in `src/NbtDiff.Core/`

- `CompareRow`, `FileSide`, `RowCounts`, `RowStatus`, `CompareOptions`, `ScanProgress` (§4.4).
- `DirectoryComparer` + `CompareRoot`: synchronous tree build, then the two-tier channel
  pipeline with bounded parallelism, cancellation, progress, and `RowChanged`.
- `DiffReport` (§4.6) for `Text` and `Json`.
- Glob exclusion (`session.lock` by default).

**Tests**

- The four fixture worlds from DESIGN §6: `Mutate` yields exactly one `Different` region row and
  the right folder counts; `Recompress`/`TouchTimestamps`/`Defragment` yield **zero** non-`Same`
  rows after completion (with `DeepVerify = true`), and `Recompress` yields `ProbablyDifferent`
  rows before Tier 2 finishes.
- Left-only / right-only files and directories propagate counts to the root.
- A corrupt file becomes `Error` without stopping the scan.
- Case-sensitive pairing: `Region/` and `region/` are two rows.
- Cancellation mid-scan: `Completion` completes, no row is left in a state other than
  `Pending`/final, no unobserved exceptions.
- `DiffReport` text and JSON snapshots for the `Mutate` world.

**Done when:** tests green; a manual run against a real world (a small throwaway harness under
`tests/` is fine, not shipped) completes Tier 1 without decompressing.

---

## S4 — Tag diff and region diff

**Needs:** S1 (and S2 for `NbtCanonicalHasher`; take it as a dependency, do not reimplement).

**Produces** in `src/NbtDiff.Core/`

- `DiffKind`, `DiffNode`, `DiffOptions`, `NbtDiffer` (§4.5).
- `IListAligner`, `IndexAligner`.
- `ChunkDiffCell`, `RegionDiffer` (§4.5), reusing `RegionFingerprint` when supplied.

**Tests**

- Table-driven cases for every `DiffKind` on every tag type; nested compound/list combinations;
  type change stops recursion; `ChangedDescendants` counts.
- Property test: for every fixture document pair, `NbtCanonicalHasher` equality ⇔
  `NbtDiffer.Diff(...).ChangedDescendants == 0` (the agreement guarantee in §4.3).
- `CompoundOrderMatters = true` reports reorder; default does not.
- Arrays: first differing index and lengths are reported; equal-length equal-content arrays are
  `Unchanged`.
- `RegionDiffer.Diff` on the `Mutate` world: exactly one cell `Different`; `DiffChunk` on that
  cell shows the single `ValueChanged` path `InhabitedTime`.

**Done when:** tests green.

---

## S5 — Avalonia shell and folder compare view

**Needs:** S3 interfaces (implementation may still be in progress — build against
`IFingerprinter` with a fake if needed) and S0's App project.

**Produces** in `src/NbtDiff.App/`

- `App.axaml`, `MainWindow` hosting a navigation stack.
- `FlatTreeSource<T>` per DESIGN §5.0, with unit tests for expand/collapse slices.
- `FolderCompareView` + `FolderCompareViewModel` per DESIGN §5.1: two synchronized `DataGrid`s
  over one `FlatTreeSource<CompareRow>`, row status colors, filter chips,
  toolbar, status bar with live counts and progress, Cancel.
- `RowChanged` → dispatcher marshaling with coalescing (§7).
- Startup argument handling (§5.4) for the directory case; file case shows a "not yet" placeholder.
- Folder pickers via `IStorageProvider`.
- Double-click/Enter raises a navigation request the view model exposes; S6 wires the targets.

**Tests** (`NbtDiff.App.Tests`, view models only)

- Filter chips hide/show rows correctly given a hand-built `CompareRow` tree.
- Counts in the status bar track `RowChanged` events.
- Coalescer never drops the final state of a row.

**Done when:** `nbtdiff <left> <right>` on the `Mutate` fixture world shows the tree with the
one red region row and correct counts, on Windows and Linux; scrolling a 100k-row synthetic tree
stays responsive.

---

## S6 — File and region compare views

**Needs:** S4, S5.

**Produces** in `src/NbtDiff.App/`

- `RegionCompareView` (§5.2): 32×32 grid, keyboard navigation, click → chunk diff.
- `FileCompareView` (§5.3): aligned `DiffNode` tree, collapsed-unchanged default, Show-unchanged
  toggle, F7/F8 navigation, array detail pane.
- Navigation from folder rows by `FileKind`; back navigation; window title shows the pair.
- Startup file/region argument case.

**Tests** (view models)

- Next/previous-change navigation order over a hand-built `DiffNode` tree.
- Collapsed-unchanged projection hides subtrees with `ChangedDescendants == 0`.

**Done when:** opening the `Mutate` world's region row shows one highlighted cell, and opening it
shows `Level/InhabitedTime` as the only change, with unchanged siblings collapsed.

---

## S7 — Polish, performance, packaging

**Needs:** S6.

**Produces**

- Settings persistence (recent pairs, compare options, window layout) in the platform config dir.
- Export report command (uses `DiffReport`).
- Text side-by-side view for `Json`/`Text` rows (small LCS line diff).
- Profiling pass against a real large world; fix whatever falls outside DESIGN §7. Record numbers
  in CLAUDE.md.
- `dotnet publish` profiles: `win-x64` and `linux-x64`, single-file, framework-dependent; CI
  uploads both as artifacts on tag.
- Keyboard reference in README.

**Done when:** published binaries run on both OSes from a clean machine; the large-world scan
meets §7.

---

## S8 — Optional extras (each independent)

- `KeyedAligner` for lists of compounds (match by `UUID`, then `id`, then index) with a UI
  option; expected to make entity/block-entity diffs readable.
- `NbtDiff.Cli` project: `nbtdiff a b --summary [--json]`, exit code 1 on differences — a thin
  wrapper over `DirectoryComparer` + `DiffReport`.
- SNBT text view of a single tag on both sides for copy/paste.
- Chunk-level "open in region view" from a world-coordinate input box.
