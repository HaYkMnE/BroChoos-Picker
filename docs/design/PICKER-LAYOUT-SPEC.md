# Picker layout spec (tile picker)

Maintainer decisions (closed): horizontal row by default, centred on the monitor under the cursor;
vertical is a Settings option; a tile shows the icon and the name, nothing else; no number badge
(digits 1-9 still work); the window stays calm.

Sources: maintainer brief; design research on icons and on comparable pickers
(Velja, Choosy, Hurl, BrowserSelect, Browserosaurus, Alt+Tab), October 2026.

## Window

- Surface: `bc.bg.base`, no chrome (`SystemDecorations=None`), padding 12 DIP (`bc.pad.4`),
  8 DIP gap between rows of content (`bc.space.3`).
- Width = 24 + 96 x (tiles in the widest balanced row, see Wrapping) DIP, floor 360 DIP (so
  first run, status lines and the repair notice stay readable with 1-3 browsers); 460 DIP for
  the first-run screen. 3 tiles = 360, 5 = 504, 8 = 792, 9 (5+4) = 504, 16 (8+8) = 792.
- Height is measured from content (existing `ApplyMeasuredHeight`), and the tile list
  scrolls vertically only when the work area is too short (existing `FitCardListToWorkArea`).
- Position: centred in the work area of the monitor under the cursor
  (`CursorPlacement.ComputeCentered`), then clamped so the top-left corner stays on screen.
- Physical size = DIP x monitor scale (100/125/150 %): 792 DIP = 792 / 990 / 1188 px.
  1188 px fits a 1280 px-wide work area at 150 %.
- Narrow work areas: the tiles per row shrink to `floor((work area - 24) / 96)`, clamped to 3..8
  (`PickerWindow.TilesPerRowFor`), so rows of tiles stay inside the screen down to a work area of
  360 DIP (3 tiles need 312 DIP, but the window is floored at 360). The first-run screen is
  460 DIP (`FirstRunWidthDip`) and ignores the work area. Known limitation: there is no fallback
  below the 360 DIP floor. `PickerWindow.WidthForTileCount` applies `MinimumWidthDip` without
  checking the work area, and `CursorPlacement.ComputeCentered` only keeps the top-left corner on
  screen, so on a work area narrower than 360 DIP (or 460 DIP on the first-run screen) the right
  edge of the window is clipped.

## Tile

| Part | Value |
| --- | --- |
| Tile | 88 x 104 DIP, margin 4 (slot 96), radius 8 (`bc.radius.card`) |
| Inner padding | 8 at rest, 7 when focused (border grows 1 -> 2, content does not move) |
| Icon plate | 48 x 48 DIP, radius 8, `bc.bg.card` fill, 1 DIP `bc.border.subtle` |
| Icon | 48 DIP logical; request 48/60/72 px at 100/125/150 % (phase 2) |
| Gap icon -> name | 8 DIP |
| Name | 12 DIP caption, line height 16, centred, max width 70, at most 2 lines, ellipsis; full name in the tooltip |

- At rest the tile has no fill and no outline: the window is the surface, so there is no
  card inside a card. Hover: `bc.bg.subtle`. Focus: the existing two-token ring
  (`bc.focus.outer` 2 DIP border on a `bc.focus.inner` fill).
- Placeholder (phase 1, and the fallback later): the first letter of the name on the plate,
  20 DIP SemiBold, `bc.text.secondary`. Phase 2 swaps in the program icon on the same plate.
- Removed on purpose: number badge, browser id line, reason line.

## Profiles

A tile of Edge, Chrome or Brave (browser ids `edge-`/`chrome-`/`brave-`, see
`LaunchHelperArguments.ResolveFamily`) names the profile it launches
(`ChoiceCardViewModel.LaunchProfileId`, the `--profile-directory` value for that tile), on a
second muted 12 DIP line under a one-line browser name. The line appears only when the browser
has more than one profile and that profile has a visible name (Chromium
`profile.info_cache.<dir>.name`). All other tiles show no profile line: Firefox, custom and
portable tiles, and any other Chromium-based browser, because the launcher passes a profile
switch to those three ids only. Names are trimmed and cut to 64 characters.

The same rule drives all three outputs, through `ChoiceCardViewModel.ProfileName`:

- Visible line: the profile name, followed by "+N" when N more profiles of that browser did not
  fit the nine slots (the count sits on the browser's last tile and is never trimmed). A tile
  with hidden profiles shows the +N count even when it has no profile name of its own.
- `ChoiceCardViewModel.AccessibleName`: `<browser>, <profile>` (for example `Chrome, Work`). When profiles are
  hidden, the count is added in words after the name. A tile without a profile line reads just
  the browser name.
- Tooltip: `ChoiceCardViewModel.TooltipText` (localized, key `Card.Tooltip`; English
  `<AccessibleName> — key <n>`), so it follows the accessible name.

Chromium profiles already expand to one tile per profile (`RankingEngine.ExpandChromiumProfiles`).

Profile names are personal data: shown in the UI only, never logged, never in the problem log
or diagnostics. A test keeps every other source file from mentioning them.

## Link line

One muted 12 DIP line at the top with the link's host (`Link.Host`), trimmed with an
ellipsis. Its tooltip shows the readable short URL. The `?` key cap sits on the same line,
right-aligned. The large host title and the second URL line are removed.

## Status lines and notices

Unchanged and still live regions, below the tiles: launch failure (danger text), info status
("another link is waiting", copied, and so on), and the config repair notice with its action.
They wrap inside the window width.

## Keys and help

- 1-9 pick a tile (the number is not drawn). Left/Right (and Up/Down) move focus.
  Enter opens, Esc cancels, F opens the system default browser, Ctrl+C copies.
- The always-visible key-hint line is removed. F1 or `?` opens the full shortcut list.

## Screen reader

Tile name = `ChoiceCardViewModel.AccessibleName` = browser display name, plus the profile name and the
hidden-profile count when the tile shows them (see Profiles). The icon plate is hidden from the
automation tree (`AccessibilityView=Raw`).

## Wrapping

Design rule: the row grows outwards from the centre, and rows are split evenly, never filled
and spilled. `BalancedRowsPanel`:

- `maxPerRow` = floor((work area - 24) / 96), clamped to 3..8.
- `rows` = ceil(n / maxPerRow), `perRow` = ceil(n / rows); every row has `perRow` or
  `perRow - 1` tiles, the larger rows on top.
- With maxPerRow 8: 9 -> 5+4, 10 -> 5+5, 12 -> 6+6, 16 -> 8+8, 17 -> 6+6+5.
  With maxPerRow 6 (1024 px at 150 %): 9 -> 5+4, 7 -> 4+3.
- Every row is centred in the full panel width, so the group's centre is the window's centre,
  also when the window is wider than the tiles (the 360 DIP floor, a long status line).
- The window width follows `perRow`, not `maxPerRow`: 9 tiles = 24 + 5 x 96 = 504 DIP.
- Digit order and Left/Right run row by row, left to right. No horizontal scrolling; the
  existing height cap scrolls the rows vertically on a short screen. The vertical layout is
  unchanged.

## Vertical variant

The same tile (icon over name) in one centred column. The window width is 24 + 96 = 120 DIP,
raised to the 360 floor. Arrows follow the layout: Left/Right in the row, Up/Down in the column.
The other pair is swallowed and does nothing. Setting: "Picker layout: Horizontal (default) /
Vertical", in Settings > Appearance, saved as `pickerLayout` in settings.json. The picker reads
that file once before its UI thread starts (measured 0.11 ms for the 132-byte file), and falls
back to Horizontal if it can't read it.

## Themes

All colours come from `PickerTokens.axaml`: Light, Dark, and the high-contrast overlay, which
maps to the platform Fluent brushes. No new colours were added. The tile adds no shadow, and
elevation stays zero in high contrast.
