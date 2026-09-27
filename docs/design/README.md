# UI design mockups

Mockups for the UI refresh tracked in #8. Each mockup is a spec for a change in the Avalonia views,
not code to ship. The issues hold the task lists; this folder holds the visuals and exact values.

| Mockup | Render | Issue | Current screen |
| --- | --- | --- | --- |
| [`mockups/Folder.dc.html`](mockups/Folder.dc.html) | [`folder.png`](mockups/folder.png) | #10 | [`folder-compare.png`](../screenshots/folder-compare.png) |
| [`mockups/Region.dc.html`](mockups/Region.dc.html) | [`region.png`](mockups/region.png) | #11 | [`region-grid.png`](../screenshots/region-grid.png) |
| [`mockups/Tag.dc.html`](mockups/Tag.dc.html) | [`tag.png`](mockups/tag.png) | #12, #7 | [`chunk-diff.png`](../screenshots/chunk-diff.png), [`snbt-diff.png`](../screenshots/snbt-diff.png) |

Shared colours and components: #9. Breadcrumb bar (top of the region and tag mockups): #13.

## Viewing

Open a `.dc.html` file in a browser. `mockups/support.js` renders its template statically (holes,
loops, conditionals); nothing is interactive except the breadcrumb and "Open chunk" links between
mockups. Fonts are Inter and JetBrains Mono from Google Fonts, with system fallbacks offline (the
PNGs were rendered with the fallbacks).

Row data sits in each file's `renderVals()`; layout and colours are inline styles in the markup.

## State palette (dark)

| Token | Value | Used for |
| --- | --- | --- |
| Background | `#111317` | Window |
| Surface | `#181b21` | Bars, panels |
| Control | `#1f232b`, border `#2f3540` | Buttons, inputs |
| Divider | `#2a2f39` | |
| Text | `#e6e6e3`; muted `#9aa0aa`; dim `#7d838d` | |
| Different | `#ef4b4b`; row tint `rgba(239,75,75,0.14)`; probably-different tint `0.08` | |
| Left only | `#3d8fe6`; tint `rgba(61,143,230,0.16)` | |
| Right only | `#a060e8`; tint `rgba(160,96,232,0.16)` | |
| Same (region cells) | `#4f7359` | |
| Empty (region cells) | `#1a1d23` | |
| Error | `#e0a020` | |
| Primary button | `#e6e6e3` on `#111317` text | Neutral, so blue/purple/red only mean state |
| Missing side | hatch `#191c22` / `#121418`, 135°, 10 px period | Empty side of a one-sided row |

## Rules the mockups encode

- Leaf rows are tinted by state. Folder and container rows are tinted **only while collapsed**;
  when expanded they show a roll-up dot and inline counts, and their children carry the colour.
- Gutter chips: filled `≠` different, dashed `≠?` probably different, filled `◀`/`▶` in the side
  colour, `…` pending, a near-invisible `=` for same.

## Sample data

Rows come from the README screenshots. Exceptions: the paths in the folder path bar, and the two
`block_entities` rows in `Tag.dc.html`, which are made up to show added-row and entry-count
handling (the real chunk (2, 3) has one difference).
