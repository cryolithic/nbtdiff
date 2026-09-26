# nbt-diff

Compares Minecraft NBT data: two files at tag level, or two Java Edition world directories
recursively in a Beyond Compare style two-pane view. Read-only. Runs on Windows and Linux.

A world scan never reports a difference that is not a content difference: chunk timestamps,
sector layout and recompression are ignored (a fast byte-level pass first, then only the files
whose bytes differ are parsed and compared canonically). Corrupt files show as errors instead of
aborting the scan.

## Usage

```text
nbtdiff                              empty folder view
nbtdiff <left-world> <right-world>   folder compare, scan starts immediately
nbtdiff <left.mca> <right.mca>       32×32 chunk grid; open a chunk for its tag diff
nbtdiff <left.dat> <right.dat>       aligned tag tree (also .nbt, .snbt incl. FTB's dialect, schematics)
nbtdiff <left.json> <right.json>     side-by-side line diff (also .txt .properties .log …)
```

One side may be a path that does not exist; it is shown as entirely missing.

Folder view row states: `=` same · `≠` different · `≠` dimmed = bytes differ, content not yet
verified · blue = left only · purple = right only · struck through = unreadable. Folders take the
"worst" state of their contents. The Differences filter shows changed and one-sided rows; unreadable
files are under the Errors filter (and All).

### Keyboard

| Key | Folder view | Region grid | Tag / text view |
| --- | --- | --- | --- |
| `Enter` / double-click | open file, toggle folder | open chunk | toggle node |
| `→` / `←` | expand / collapse | move selection | expand / collapse |
| `↑` `↓` | move | move selection | move |
| `F8` / `F7` | — | — | next / previous change (wraps) |
| `Ctrl+F8` / `Ctrl+F7` | — | — | next / previous changed chunk of the region (chunk views only) |
| `Alt+→` / `Alt+←` | — | — | copy the selected row's value or subtree to the other side |
| `Ctrl+S` | — | — | save edited side(s) back to their files / region (tag view) |
| `◀ Back` button | | return to the previous view; a folder scan keeps its state and the row you opened is re-selected and scrolled into view | |

The startup folder view also compares two files: type or browse two file paths (the `…` button
picks a file once the box holds a file path) and press Compare.

In the tag view, `◀ Copy to left` / `Copy to right ▶` copy the selected row — a single value or a
whole subtree, into an existing or a missing side alike — from one side to the other, WinMerge-style,
and the diff refreshes immediately. Edits live in memory until `Ctrl+S` writes each edited side back:
`.dat`/`.snbt` files are rewritten in their original compression (a one-time `.bak` keeps the
pre-nbtdiff copy), and a chunk is re-appended to its region file the way Minecraft itself saves (dead
sectors are left behind; the chunk keeps its compression scheme). Going Back, stepping to another
chunk, or closing the window with unsaved copies asks before discarding them. Bedrock files are
compared but not written.

Chunks re-saved by Minecraft with only their `LastUpdate` tick changed are not differences: the
"Ignore tags" box lists tag paths that are not content (default `LastUpdate, Level/LastUpdate`;
add `InhabitedTime` if you want that ignored too). Reordered entity / block-entity / item lists are
not differences either: "Match list items by key" (on by default) pairs entities by UUID, block
entities by position and items by id, in the scan, the chunk grid and the tag view alike.

The status bar says which pass is running: `pass 1 of 2 — hashing bytes` reads every file once
without decompressing; `pass 2 of 2 — verifying content n/m` parses only the files whose bytes
differed. Until pass 2 has settled a row (or with Deep verify off) it shows `≠?` — "bytes differ,
content not verified" — never `≠`; opening such a region verifies it and says so in the header when
every chunk turns out identical.

Options (deep verify, key-order sensitivity, matching list items by UUID/id, exclude globs,
ignored tags, window placement, recent pairs) persist in `%APPDATA%\nbtdiff\settings.json` on Windows and
`~/.config/nbtdiff/settings.json` on Linux.

## Build

Requires the .NET 10 SDK.

```bash
dotnet build          # warnings are errors
dotnet test
dotnet run --project src/NbtDiff.App -- <left> <right>
```

## Publish

Single-file, framework-dependent binaries (the target machine needs the .NET 10 runtime):

```bash
dotnet publish src/NbtDiff.App -p:PublishProfile=win-x64     # → publish/win-x64/nbtdiff.exe
dotnet publish src/NbtDiff.App -p:PublishProfile=linux-x64   # → publish/linux-x64/nbtdiff
```

CI builds and tests on Windows and Ubuntu on every push and pull request. Pushes to `master`, `v*`
tags and manual runs also publish `nbtdiff-win-x64` and `nbtdiff-linux-x64` as workflow artifacts. Pushing a `v*` tag also creates a GitHub release with
both binaries attached.

Design: `docs/DESIGN.md`. Stages: `docs/PLAN.md`. Third-party attribution: `third_party/NOTICE`.
