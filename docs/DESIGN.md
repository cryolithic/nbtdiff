# nbt-diff — Design

Companion to `CLAUDE.md` (decisions, vendoring findings) and `docs/PLAN.md` (build stages).
This document is the contract the stages build against. Change it deliberately; when a stage
discovers the contract is wrong, fix the document in the same change.

## 1. Goals and non-goals

**Goals**

- Diff two NBT files at tag level.
- Diff two Java Edition world directories recursively, Beyond Compare style: a two-pane folder
  tree with per-row state, cheap to compute, with tag-level detail available on demand.
- Never report a difference that is not a content difference: timestamps, sector layout, and
  recompression must not show up as changes.
- Never abort a world scan because one file is corrupt.
- Runs on Windows and Linux.

**Non-goals (explicitly out of scope, all stages)**

- Editing, merging, or saving. Read-only tool.
- Bedrock worlds (LevelDB). Individual Bedrock `.dat`/`.nbt` files are supported because the
  vendored loader already detects little-endian and the 8-byte Bedrock header.
- Three-way diff.

## 2. Solution layout

```
nbtdiff.sln
Directory.Build.props        net10.0, nullable, implicit usings, warnings-as-errors, LangVersion latest
src/
  NbtDiff.Nbt/               vendored fNbt fork + SNBT + trimmed loaders. No dependency on Core/App.
  NbtDiff.Core/              classification, hashing, scanning, diffing. Depends only on Nbt.
  NbtDiff.App/               Avalonia 12 shell. Depends on Core.
tests/
  NbtDiff.TestFixtures/      deterministic world/file generators shared by every test project
  NbtDiff.Nbt.Tests/
  NbtDiff.Core.Tests/
  NbtDiff.App.Tests/         view-model tests only; no UI automation
third_party/NOTICE
docs/
```

Rules enforced by project references (a Core→App or Nbt→Core reference is a build error):

- `NbtDiff.Core` never references Avalonia or anything under `App`.
- `NbtDiff.Nbt` never references `Core`.
- Nothing in `src/` references `tests/`.

Packages: `System.IO.Hashing` (XxHash64), `Avalonia` 12, `Avalonia.Desktop`,
`Avalonia.Themes.Fluent`, `Avalonia.Fonts.Inter`, `Avalonia.Controls.DataGrid`, `CommunityToolkit.Mvvm`,
`xunit`. **Not** `Avalonia.Controls.TreeDataGrid`: from 12.x it is a commercial product that fails the
build without a license key (`AVLIC0001`). Trees are rendered as a flattened, virtualized `DataGrid`
(see §5.0).

## 3. NbtDiff.Nbt — the data layer

Only the fNbt fork is vendored (`src/NbtDiff.Nbt/fNbt/`, BSD-3-Clause). nbt-studio's own loaders,
`utils.nbt` (SNBT) and `utils.utility` carry no license and are **not** copied; everything else in
this project is original code, informed by reading upstream.

### 3.1 What was changed in the fNbt fork

- `UndoableAction.cs`, the `OnChanged`/`ActionPerformed` events and `RaiseChanged*` are gone.
  `NbtTag.PerformAction` is a stub that just runs the action, and `DescriptionHolder` survives as an
  empty vestigial type so the ~20 vendored call sites in the tag setters compile untouched.
- Every vendored file starts with `#nullable disable` / `#pragma warning disable`; upstream is not
  nullable-annotated and the solution builds with warnings-as-errors.
- Kept: readers/writers, `NbtCompression` autodetect, `BigEndian`, `OrderedDictionary`,
  `TagSelector`, all tag types, `JetBrains.Annotations` (Apache-2.0, see NOTICE).
- Gotcha: `NbtByte.Value` is an unsigned `byte`; Minecraft and SNBT treat bytes as signed. Convert
  with `(sbyte)` at every boundary that shows or parses a byte (the SNBT code does).

### 3.2 Public surface (as shipped in S1)

```csharp
namespace NbtDiff.Nbt;

public enum NbtFormat { Snbt, JavaNbt, BedrockNbt, BedrockLevelDat }
public sealed record NbtFormatInfo(NbtFormat Format, NbtCompression Compression, bool BigEndian);

/// A parsed standalone NBT file. Immutable after load.
public sealed class NbtDocument
{
    public string Path { get; }
    public NbtCompound Root { get; }
    public NbtFormatInfo Format { get; }
    /// Tries SNBT, Java, Bedrock, Bedrock-with-8-byte-header in that order; first non-suspicious
    /// parse wins, else first success, else an aggregate failure with one Attempt per strategy.
    public static LoadResult<NbtDocument> Load(string path);
    public static LoadResult<NbtDocument> Load(Stream seekable, string displayPath);
}

/// Header-only view of an Anvil/.mcr region file. Open reads exactly 8 KiB. Thread-safe reads.
public sealed class RegionFile : IDisposable
{
    public const int SectorSize = 4096, HeaderSize = 8192;
    public string Path { get; }
    public RegionCoords? Coords { get; }            // from r.<x>.<z>.<ext>; null otherwise
    public long Length { get; }
    public IReadOnlyList<ChunkRef> Chunks { get; }  // occupied slots incl. corrupt-header ones, (z, x) order
    public int ChunkCount { get; }
    public ChunkRef? this[int x, int z] { get; }    // 0..31, throws outside
    public static LoadResult<RegionFile> Open(string path);                      // SafeFileHandle + RandomAccess
    public static LoadResult<RegionFile> Open(Stream stream, string displayPath); // takes ownership
}

public sealed class ChunkRef
{
    public int X { get; } public int Z { get; }
    public long Offset { get; } public int SectorCount { get; }
    public uint Timestamp { get; }                  // never part of equality
    public string? HeaderError { get; } public bool IsCorruptHeader { get; }
    public (int X, int Z)? WorldCoords { get; }
    // The three below are null until a Read* call has looked at the 5-byte prefix.
    public byte? SchemeByte { get; }                // 1 GZip, 2 ZLib, 3 None, 4 LZ4, 127 custom
    public bool? IsExternal { get; }                // payload in c.<x>.<z>.mcc
    public NbtCompression? Compression { get; }     // null for LZ4/custom
    public LoadResult<byte[]> ReadCompressedPayload();   // no decompression
    public LoadResult<NbtCompound> ReadNbt();            // decompress + parse; fails for LZ4
}

public readonly record struct RegionCoords(int X, int Z)
{
    public (int X, int Z) ChunkAt(int localX, int localZ);
    public static RegionCoords? FromFileName(string path);
}

public sealed record LoadFailure(string Description, Exception? Exception = null)
{
    public IReadOnlyList<LoadFailure> Attempts { get; init; }
    public string ToShortString(); public string ToDetailedString();
}
public readonly record struct LoadResult<T>(T? Value, LoadFailure? Failure) where T : class
{
    public bool Ok { get; }
    public static LoadResult<T> Success(T v); public static LoadResult<T> Fail(string d, Exception? e = null);
    public static LoadResult<T> Try(string description, Func<T> load);
    public T ValueOrThrow(); public LoadResult<U> Map<U>(Func<T, U> f);
}

namespace NbtDiff.Nbt.Snbt;
public sealed class SnbtParser { public static NbtTag Parse(string text); public static bool TryParse(...); }
public static class SnbtWriter { public static string Write(NbtTag tag, SnbtOptions? options = null); }
public sealed record SnbtOptions(string? Indent = null) { Compact; Pretty; }
```

Behavior that differs from upstream nbt-studio (each is a test in `NbtDiff.Nbt.Tests`):

- `RegionFile.Open` reads only the header. A counting stream sees exactly 8192 bytes.
- Zero occupied slots is `Ok` with `ChunkCount == 0`. A file shorter than 8192 bytes fails.
- A bad header slot (inside the header, past EOF, zero sectors) is listed with `HeaderError`; reads
  of it fail; siblings are unaffected.
- External chunks resolve `c.<x>.<z>.mcc` from the region's world coords; a region without coords
  in its name fails that read with an explanatory message.
- Truncation is **not** reliably detectable by parse failure: `DeflateStream` accepts a truncated
  stream and fNbt stops at the first `TAG_End`, so a cut-off chunk can parse as a shorter compound.
  Corruption tests break the zlib header instead.

SNBT: the parser accepts Java Edition syntax up to 1.21.4 — quoted/unquoted keys, both quote
styles with `\\ \" \' \n \t \r \b \f \s \uXXXX` escapes, numeric suffixes, `true`/`false`,
typed arrays (integer literals of any width, range-checked). Not supported: 1.21.5+ extensions
(heterogeneous lists, hex/binary literals, `bool()`-style operations). The writer emits compact or
indented text; arrays always stay on one line.

## 4. NbtDiff.Core

### 4.1 File classification

```csharp
public enum FileKind { Region, Nbt, Snbt, Json, Text, Binary, Directory }

public static class FileClassifier
{
    // Extension first, then a sniff of the first bytes for ambiguous cases.
    // .mca .mcr → Region      (.mcc is Binary; it is reached through its region file)
    // .dat .dat_old .nbt .schematic .litematic → Nbt
    // .snbt → Snbt; .json → Json; .txt .lock .properties .log → Text; else Binary
    public static FileKind Classify(string path);
}
```

Java world layout, so the scan knows what it will meet:

```
level.dat, level.dat_old, session.lock, icon.png
region/r.X.Z.mca              blocks       (entities/ is 1.17+, poi/ is 1.14+)
entities/r.X.Z.mca
poi/r.X.Z.mca
DIM-1/{region,entities,poi}/  nether       DIM1/ end
dimensions/<ns>/<name>/...    custom dimensions, same shape
playerdata/<uuid>.dat         stats/<uuid>.json   advancements/<uuid>.json
data/*.dat                    maps, raids, scoreboard, idcounts
datapacks/, serverconfig/     arbitrary files
```

### 4.2 Fingerprints — the two-tier comparison

The folder scan must be fast, so it never decompresses. But recompression (a newer Minecraft
saving the same chunk) changes compressed bytes without changing content, so a byte-level answer of
"different" is only provisional. Two tiers:

| Tier | Name | What it hashes | Cost | Answer |
| --- | --- | --- | --- | --- |
| 1 | **Quick** | Region: XxHash64 per chunk over the compressed payload (length + compression byte + bytes); timestamps and sector offsets excluded. Others: XxHash64 of the whole file. | One sequential read per file | `Same` is final. `Different` is provisional. |
| 2 | **Deep** | Region: canonical NBT hash per chunk (§4.3). Nbt/Snbt: canonical NBT hash of the root. Json/Text: hash with normalized line endings. Binary: quick hash is already final. | Decompress + parse | Final. |

Scan runs Tier 1 across everything, then Tier 2 only on files Tier 1 marked `Different`. Rows show
`ProbablyDifferent` until Tier 2 resolves them to `Different` or `Same`. Tier 2 is skippable via a
compare option (`DeepVerify = false`) for very large worlds.

```csharp
public sealed record RegionFingerprint(IReadOnlyDictionary<(int x, int z), ulong> ChunkHashes);
public sealed record FileFingerprint(FileKind Kind, long Size, ulong Hash, RegionFingerprint? Region);

public interface IFingerprinter
{
    ValueTask<FileFingerprint> QuickAsync(string path, FileKind kind, CancellationToken ct);
    ValueTask<FileFingerprint> DeepAsync(string path, FileKind kind, CancellationToken ct);
}
```

### 4.3 Canonical NBT hashing

`NbtCanonicalHasher.Hash(NbtTag)` walks the tree and feeds XxHash64 with: tag type byte, name,
then children. **Compound children are visited in ordinal-sorted key order**, so key order never
affects the hash. Lists and arrays are visited in stored order. Floats/doubles are hashed by their
IEEE bits (so `-0.0 != 0.0` and NaN is stable). The same walk order is used by the differ, so
"hash equal" and "diff empty" always agree — there is one test asserting exactly that over the
fixture corpus.

### 4.4 Directory scan

```csharp
public enum RowStatus { Pending, Same, ProbablyDifferent, Different, LeftOnly, RightOnly, Error }

public sealed class CompareRow
{
    public string RelativePath { get; }      // forward slashes, root = ""
    public string Name { get; }
    public FileKind Kind { get; }
    public FileSide? Left { get; }           // null when missing on that side
    public FileSide? Right { get; }
    public RowStatus Status { get; }         // mutated by the scanner, observed by the UI
    public string? Error { get; }
    public IReadOnlyList<CompareRow> Children { get; }   // directories only
    public RowCounts Counts { get; }         // aggregated over descendants; folders derive Status from this
}
public sealed record FileSide(string FullPath, long Size, DateTime Modified, FileFingerprint? Fingerprint);

public sealed record CompareOptions(
    bool DeepVerify = true,
    bool CompoundOrderMatters = false,
    IReadOnlyList<string>? ExcludeGlobs = null,   // default: session.lock
    int MaxParallelism = 0);                       // 0 → ProcessorCount

public sealed class DirectoryComparer
{
    public DirectoryComparer(IFingerprinter fp, CompareOptions options);

    /// Builds the full tree synchronously (directory listing only, cheap), then streams status
    /// updates as fingerprints complete. The returned root is live: rows mutate in place and
    /// raise RowChanged. Folder rows update their Counts as children resolve.
    public CompareRoot Start(string leftRoot, string rightRoot, CancellationToken ct);
}

public sealed class CompareRoot
{
    public CompareRow Root { get; }
    public event Action<CompareRow> RowChanged;
    public IProgress<ScanProgress> Progress { get; }
    public Task Completion { get; }
}
```

Pairing rule: rows are keyed by relative path, compared **ordinally and case-sensitively** (Linux
is case-sensitive; a Windows world copied to Linux must not silently merge `Region` and `region`).
Files are sorted with a logical/natural comparer so `r.2.0` sorts before `r.10.0`.

Pipeline: `Channel<CompareRow>` fed by the tree walk → N workers (`MaxParallelism`) running Tier 1
→ a second channel for Tier 2 → `RowChanged` raised on a worker thread. The UI does the dispatcher
hop; Core stays thread-agnostic. Cancellation leaves rows `Pending`, never half-written.

### 4.5 Tag diff

```csharp
public enum DiffKind { Unchanged, Added, Removed, ValueChanged, TypeChanged }

public sealed class DiffNode
{
    public string Name { get; }              // compound key, or "[i]" for list index
    public NbtTagType? LeftType { get; }
    public NbtTagType? RightType { get; }
    public NbtTag? Left { get; }
    public NbtTag? Right { get; }
    public DiffKind Kind { get; }
    public IReadOnlyList<DiffNode> Children { get; }
    public int ChangedDescendants { get; }   // 0 ⇒ subtree can be collapsed as unchanged
}

public static class NbtDiffer
{
    public static DiffNode Diff(NbtTag? left, NbtTag? right, DiffOptions options);
}
```

Matching rules:

- **Compound**: children matched by key. Missing on one side → `Added`/`Removed`. Present on both
  with different tag type → `TypeChanged` (no recursion). Key order ignored unless
  `CompoundOrderMatters`.
- **List**: matched by index (v1). Length mismatch → trailing items are `Added`/`Removed`. A
  pluggable `IListAligner` exists from the start, with `IndexAligner` as the only implementation;
  `KeyedAligner` (match compounds inside a list by a chosen child such as `UUID` or `id`) is a
  later stage and slots in without touching the differ.
- **Arrays** (byte/int/long): leaf values. `ValueChanged` carries the first differing index and
  the length pair so the UI can summarize without materializing a per-element diff.
- **Scalars**: `ValueChanged` on inequality. Floats compare by bits, matching the hasher.

Region-level diff:

```csharp
public sealed record ChunkDiffCell(int X, int Z, RowStatus Status);   // 32×32 grid, sparse
public static class RegionDiffer
{
    public static IReadOnlyList<ChunkDiffCell> Diff(RegionFile? left, RegionFile? right, CompareOptions o);
    public static DiffNode DiffChunk(RegionFile? left, RegionFile? right, int x, int z, DiffOptions o);
}
```

`RegionDiffer.Diff` reuses the fingerprints already computed by the scan when available, so opening
a region row costs nothing extra; `DiffChunk` parses exactly the two chunks requested.

### 4.6 Reporting

`DiffReport.Write(CompareRoot, TextWriter, ReportFormat.{Text,Json})` — a flat list of non-`Same`
rows with counts. Used by the export command and, later, by any CLI entry point.

## 5. NbtDiff.App — Avalonia shell

MVVM with `CommunityToolkit.Mvvm`. One window, a navigation stack of three views.

### 5.0 Trees are flattened lists

Every tree in the UI (folder rows, diff nodes) is shown through `Avalonia.Controls.DataGrid`,
which virtualizes rows but is not hierarchical. A `FlatTreeSource<T>` in the App project projects a
tree into an `ObservableCollection` of visible rows — each row carries `Depth`, `IsExpanded`,
`HasChildren` — and rebuilds the affected slice on expand/collapse. The first column is a template
with an indent proportional to `Depth` and an expander glyph. Because both folder panes bind to the
**same** flat collection, they always have identical row counts, so synchronizing scroll and
expansion is just sharing the source and mirroring `ScrollViewer.Offset`.

### 5.1 FolderCompareView

Two `DataGrid` panes bound to the **same** `FlatTreeSource<CompareRow>`; left pane shows `Left`
columns, right pane shows `Right` columns. Expand/collapse and vertical scroll are synchronized
through the shared source (§5.0). Columns: Name, Size, Modified, Status glyph.

Row colors: Same = default, Different = red, ProbablyDifferent = red with a dimmed glyph,
LeftOnly/RightOnly = blue/purple with an empty placeholder row on the other side, Error =
strikethrough with tooltip.

Toolbar: left path, right path, browse buttons, Compare/Cancel, filter chips (Show: All /
Differences / Same / Orphans), Deep-verify toggle, Export report.
Status bar: `12 differ · 340 same · 3 left-only · 1 right-only · scanning region/ (43%)`.

Double-click or Enter on a file row opens FileCompareView or RegionCompareView by `FileKind`.
`Json`/`Text` rows open a plain side-by-side text view (read-only, line-diff via a small LCS —
stage 7 only). `Binary` rows show sizes and hashes only.

### 5.2 RegionCompareView

A 32×32 grid of cells colored by `ChunkDiffCell.Status`, plus the region coords and counts. Click
a cell → FileCompareView for that chunk pair. Arrow keys move the selection; Enter opens.

### 5.3 FileCompareView

A **single aligned tree** rather than two panes: one `DataGrid` over `FlatTreeSource<DiffNode>` with columns
Name · Type · Left value · Right value · Status. Unchanged subtrees are collapsed by default;
"Show unchanged" expands them. F7/F8 = previous/next changed node (depth-first over
`ChangedDescendants > 0`). Arrays render `int[4096] · differs at [17]` and, on selection, a
detail pane showing a hex/element view of both sides around the first difference.

### 5.4 Startup

`nbtdiff <left> <right>`: if both are directories → folder compare starts immediately; both files →
file/region compare; mismatched → error dialog. No args → empty folder view.

## 6. Test fixtures

`NbtDiff.TestFixtures` builds worlds in a temp directory with deterministic content, using the
vendored fNbt writer only (no real Minecraft data checked in beyond a few tiny hand-made files):

```csharp
var w = new WorldBuilder(seed: 42).WithRegion(0, 0, chunks: 40).WithLevelDat().WithPlayer(guid);
w.Write(leftDir);
w.Mutate(m => m.Chunk(0, 0, 3, 1).SetPath("InhabitedTime", 999L)).Write(rightDir);   // 40 chunks fill rows z=0..1
w.Recompress(NbtCompression.GZip).Write(rightDir2);   // same content, different bytes
w.TouchTimestamps().Write(rightDir3);                  // same content, different timestamps
w.Defragment().Write(rightDir4);                       // same content, different sector offsets
```

These four mutations are the acceptance corpus for "no false differences".

## 7. Performance targets

Measured on a real ~2 GB world (not checked in; path configured locally):

- Tier 1 scan: bounded by disk read throughput; no decompression, no allocation per chunk beyond
  a pooled buffer.
- Memory: the scan holds `CompareRow` + fingerprint per file (a few hundred bytes × file count),
  never tag trees. Tag trees exist only for the one file pair currently open.
- UI: `DataGrid` row virtualization over the flattened list keeps 100k+ visible rows scrollable;
  collapsed subtrees cost nothing. `RowChanged` events are batched
  on the dispatcher (coalesce to ~30 Hz) so a fast scan does not starve rendering.
