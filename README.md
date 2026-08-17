# xStation Menu Refiner
<img align="right" src="https://raw.githubusercontent.com/DerekPascarella/xStationMenuRefiner/refs/heads/main/screenshots/screenshot.png" width="265">A utility for customizing how xStation will display game titles.

The application reads an SD card and shows the game list exactly as xStation will render it, in both of the console's menu views, subfolders included. Editing an entry performs whatever renames and CUE sheet edits are needed for that change to appear on real hardware, and the card can be reorganized into folders from inside the application.

As of version 2.0.0, xStation Menu Refiner replaces the command-line xStation Image Renamer with a cross-platform GUI application.

The only thing read from the card's `00xstation` system folder is the first byte of `config.txt`, which tells the application whether Folder Browsing is turned on. That same byte is the only thing ever written back to `00xstation`, and only when the user stages a change to Folder Browsing.

## Table of Contents

- [Current Version](#current-version)
- [Changelog](#changelog)
- [Credits](#credits)
- [Supported Platforms](#supported-platforms)
- [Supported Disc Image Formats](#supported-disc-image-formats)
- [How xStation Names a Game](#how-xstation-names-a-game)
- [Basic Usage](#basic-usage)
  - [Loading an SD Card](#loading-an-sd-card)
  - [The Two Menu Views](#the-two-menu-views)
  - [Reading the Game List](#reading-the-game-list)
  - [Renaming an Entry](#renaming-an-entry)
  - [Organizing the Card into Folders](#organizing-the-card-into-folders)
  - [Repairing Issues](#repairing-issues)
  - [Applying Changes](#applying-changes)
  - [Needs Attention](#needs-attention)
  - [Options](#options)
- [Firmware Limits](#firmware-limits)
- [SD Card Compatibility](#sd-card-compatibility)
- [Legal and Licensing](#legal-and-licensing)
  - [xStation Menu Refiner](#xstation-menu-refiner-1)
  - [Third-Party Components](#third-party-components)

## Current Version
xStation Menu Refiner is currently at version [2.0.0](https://github.com/DerekPascarella/xStationMenuRefiner/releases/tag/2.0.0).

## Changelog
- **Version 2.0.0 (2026-08-17)**
  - Complete rewrite from console application (xStation Image Renamer) to cross-platform GUI (Windows, macOS, Linux).
  - Menu labels are now typed directly into the application, instead of each game folder having to be manually renamed beforehand.
  - Game list is now drawn exactly as xStation will render it, in both the flat menu and the Folder Browsing tree, instead of the card being processed sight unseen. The card's own Folder Browsing setting is read from `config.txt` and either view can be previewed.
  - The card's Folder Browsing setting can now be changed from inside the application, staged and applied like any other change.
  - The card can now be reorganized from inside the application: folders can be created and deleted, games and folders can be moved between folders or dragged onto them, a game can be wrapped into a folder of its own, and a game alone in its folder can be unwrapped out of it.
  - Every rename and CUE sheet edit is now staged and reviewed before anything is written to the card, and each change in the review carries its own checkbox, so a batch can be applied selectively.
  - A folder whose changes fail is now rolled back rather than left half-processed, and every result is verified against the card afterward.
  - Renames now go through a temporary name, so a change of letter case alone actually lands on the card.
  - Bare data tracks and CloneCD CCD/IMG disc images are now read, instead of CUE/BIN alone.
  - Track numbers already on disk are now kept, instead of every multi-track game being renumbered by CUE order.
  - CUE sheets are now patched at the byte level, preserving line endings, indentation, and original encoding, instead of being rewritten as UTF-8.
  - Cards are now checked for problems that break the menu, including track markers xStation will not recognize, folders that split into several menu entries, duplicate labels, unresolved CUE references, and macOS metadata that can stop the game list scan.
  - Labels the menu will trim at 47 characters and disc image paths past the console's 256-character limit are now flagged.
  - 2048-byte-per-sector ISOs, which the console cannot boot, and multi-track CloneCD rips, whose CD audio the console will not play, are now detected and warned about.
  - Most detected problems can be repaired in one click, either on a single entry or across the whole card.
  - Auto-update functionality added for Windows and Linux builds (macOS presently only supports an update notification).
- **Version 1.2 (2024-11-08)**
  - Added support for dragging SD card directly onto executable for ease of use.
- **Version 1.1 (2022-09-15)**
  - Added proper CUE parsing to ignore files that aren't associated with disc image, as well as correctly process track files that aren't in alphanumeric order.
- **Version 1.0 (2022-09-14)**
  - Initial release.

## Credits

- **Programming**
  - Derek Pascarella (ateam)
- **Testing**
  - agarpac
  - CutThroatCody
  - Glitchez

## Supported Platforms

| Platform | Architecture | Download |
|----------|-------------|----------|
| Windows | x64 | `.zip` |
| Windows | x86 | `.zip` |
| macOS | Apple Silicon | `.tar.gz` (`.app` bundle) |
| macOS | Intel | `.tar.gz` (`.app` bundle) |
| Linux | x64 | `.tar.gz` |

Every build is self-contained, with no runtime to install.

## Supported Disc Image Formats

| Format | Extension(s) | Notes |
|--------|-------------|-------|
| CUE/BIN | `.cue` (+ `.bin`) | Single or multiple BIN files per disc |
| Bare data track | `.bin`, `.img` | One image, no CUE sheet needed |
| CloneCD | `.ccd`, `.img`, `.sub` | Two or three-file set, data track only |

A CUE sheet is only needed to bind several tracks together. A single data track boots on its own, so a lone `.bin` or `.img` sitting in a folder with no sheet beside it is left alone. Several images that read as tracks of one game with nothing binding them are flagged instead.

What xStation boots is a raw 2352-byte-per-sector image, which in practice means a BIN or an IMG. The extension is not what decides, so a raw rip that happens to be named `.iso` is read as a data track like any BIN, but a true ISO 9660 image, the 2048-byte-per-sector kind most ripping tools produce, lists in the menu and then fails to boot. The application detects that from the file's content rather than its name and raises a warning recommending a CUE/BIN replacement. The console also never reads CCD sheets, booting a CloneCD image as a single bare data track, so a rip whose CCD lists further tracks (usually CD audio) is warned about the same way.

## How xStation Names a Game

xStation builds each menu entry from the file name of the disc's first data track.

```
label = base name of the first data track image
        minus its extension
        minus a trailing (Track NN) marker
```

So a disc whose first data track is `Final Fantasy VII (USA) (Disc 1) (Track 01).bin` appears in the menu as `Final Fantasy VII (USA) (Disc 1)`.

The folder name has no effect on the label, and neither does the CUE sheet's own file name. A folder called `My Games` holding `Tomb Raider (Track 01).bin` and `Completely Different.cue` shows up as `Tomb Raider`. This trips users up regularly, because on a tidy card the folder, CUE sheet, and BIN files all share the same base name, which makes it look as though the folder or the CUE sheet is in charge.

With Folder Browsing turned on, the rules change. Folder rows show the folder name, and a game row shows the data track's file name in full, extension and `(Track NN)` marker included. The same disc that reads `Final Fantasy VII (USA) (Disc 1)` in the flat menu reads `Final Fantasy VII (USA) (Disc 1) (Track 01).bin` when browsed to.

The track marker is only recognized in that exact shape, with real parentheses and a capital T. Both `(Track 1)` and `(Track 01)` qualify. Files named `Resident Evil 2 Track 1.bin` and `Resident Evil 2 Track 2.bin` are therefore not grouped, and xStation lists them as two separate games. xStation Menu Refiner draws that folder exactly as the card will, one row per entry, marks each row with how many entries the folder splits into, and offers a one-click fix.

## Basic Usage

### Loading an SD Card
Select the card from the dropdown in the toolbar, or click **Browse…** to open any folder and treat it as a card. The dropdown lists every removable volume, along with any fixed volume carrying an `00xstation` folder, so cards that Windows reports as fixed drives are still found. Volumes holding an `00xstation` folder are preferred on startup. Loading anything without that folder at its root shows a warning, since it may not be a card. A folder path can also be passed on the command line.

Click **Rescan** to read the card again.

### The Two Menu Views
xStation draws its menu one of two ways, controlled by the Folder Browsing setting in the console's options menu. With it off, the menu is one flat, card-wide list of games. With it on, the menu is the card's directory tree, entered folder by folder.

xStation Menu Refiner reads the card's Folder Browsing setting from `config.txt` and opens in the matching view, so what is on screen is what the console will draw. A card with no readable `config.txt` opens in the folders view, matching the firmware's built-in default. The selector in the status bar switches between **Flat list** and **Folders** at any time without rescanning. Switching to the view the card is not set to offers to stage a change to Folder Browsing in `config.txt`, applied the same way as any other change.

Issue counts follow the previewed view. Two folders that collide in the flat menu are not a problem when browsing, and a file name the tree draws too long is not a problem in the flat list, so each view reports what affects it.

### Reading the Game List
In the flat view, games appear in one alphabetical list with the exact label xStation will show. In the folders view, the tree mirrors the card: folders first, then games, each level alphabetical, empty folders included, because the console draws all of it. Folder rows carry how many games they hold.

Each game row carries the disc image format, its track count, and its total size, and any row needing attention is marked with a colored dot whose tooltip explains why.

Selecting a row fills the panel on the right with the menu label, the folder name on disk, the CUE sheet or control file name, the disc image format, every track with its file name and size, and anything about that entry needing attention.

The **Search** box filters the list by label, keeping parent folders visible while anything under them still matches.

### Renaming an Entry
Double-click a row to rename it in place, or edit the **Menu Label** box on the right and press Enter. Nothing is written to the card while typing. A staged row is marked with a green bar at its left edge and a bold label, and the status bar tracks how many changes are pending.

**Stage Change** queues the rename and only lights up while the box holds something different from the row. **Revert Entry** puts the selected entry back to what is on the card, dropping anything staged for it. **Discard All** in the status bar throws away every pending change at once.

Renaming a game plans a rename of the folder, the CUE sheet, and every disc image, then patches the CUE sheet so its `FILE` lines point at the new names. Renaming a menu folder renames just that folder.

Names that cannot exist on the card are refused before anything is staged. A forward slash in particular does not even error. Windows silently creates a nested folder instead, which makes `Chrono Cross (Disc 1/2)` impossible as a name.

### Organizing the Card into Folders
Right-clicking a row opens a menu of folder commands, each shown only when it applies to that row.

- **New Folder Inside…** makes an empty folder inside the selected one, and **New Folder at Card Root…** in the **Tools** menu does the same at the top level. Both happen right away rather than waiting for Apply Changes, so the new folder can be used as a destination immediately.
- **Move To…** opens a picker of every folder the entry can go to, the card root included. Destinations the move would break, such as a folder inside the thing being moved or one already holding the same name, are not offered. A game that owns its folder moves as that folder, and a game sharing its folder with other games moves as its own CUE and BIN files.
- **Wrap in Folder** takes a game whose files sit loose beside other games' files and puts them in a new folder named after it, so browsing shows a single folder row.
- **Unwrap Folder** does the opposite, moving a game's files up one level and removing the emptied folder, so browsing shows the game row directly.
- **Delete Folder** removes a folder right away, behind a confirmation. It is refused unless the folder is empty.

Rows can also be dragged onto folders, which stages the same move the **Move To…** command stages. A drop the move rules would refuse is rejected before anything happens.

Moves, wraps, and unwraps are staged like renames and wait for **Apply Changes**. Only creating and deleting empty folders happen immediately.

### Repairing Issues
A folder whose image names do not agree with each other shows up on the card as several menu entries. Renaming such a folder repairs it, because the track number is read out of the old name whatever shape it was in.

**Repair All Fixable Issues…** in the **Tools** menu queues every repair the card needs without changing any label: track markers xStation will not recognize, folders that split into several menu entries, CUE sheets whose `FILE` lines disagree with the disc images on letter case, and, in the folders view, rows whose track marker alone pushes them past the menu's trim. The **Repair This** button on an individual entry queues just that folder.

In the folders view, a multi-track game's row ends in `(Track 01).bin`, which is often what pushes it past the menu's trim. **Shorten Menu Entry** on the row's right-click menu renames the files so the row reads `Game.bin` instead.

### Applying Changes
Clicking **Apply Changes** opens a review window listing every operation grouped by folder, old name to new name, including each CUE sheet patch. Each folder's change carries a checkbox, with **Select All** and **Clear All**, so a batch can be applied selectively. The apply button counts what is checked, and nothing touches the card until it is clicked. A change that cannot run, such as one whose target collided with something added since staging, cannot be checked and is reported instead.

Each folder is processed as a unit. Every rename is performed through a temporary name, so a change that only alters letter case actually happens instead of silently doing nothing. Every result is verified against the card afterward, letter case included. If any operation in a folder fails, that folder is rolled back and reported as failed rather than counted as processed.

When the run finishes, a summary shows how many folders were processed, failed, or skipped, along with the specific error for anything that did not succeed. After a successful apply, the application shows a reminder to run **Refresh Game List** in the xStation options menu, because the console caches its game list and keeps showing the old names until it is refreshed.

### Needs Attention
The **Issues** button opens a window listing everything on the card that needs attention, grouped by kind and filterable to errors only. Each entry can be selected in the main list with **Show**.

- Folders that will appear as several menu entries because their image names do not agree.
- Track numbers xStation will not recognize.
- CUE sheets pointing at a file that is missing from the folder.
- CUE sheets naming a file whose letter case does not match the disc image on disk.
- Disc images a folder's CUE sheet does not reference.
- Two CUE sheets in one folder naming the same disc image.
- Disc images that read as tracks of one game with no CUE sheet to bind them.
- Entries that produce the same menu label, across the card in the flat menu, or within one folder when browsing.
- Labels the menu will trim, and paths past the limits described below.
- Disc images storing 2048 bytes per sector, which the console cannot boot.
- CloneCD sheets listing tracks the console will never play.
- Names the filesystem cannot hold.
- macOS metadata that can stop the game list scan part way through.
- Temporary files left behind by an interrupted rename.

### Options
- **Rename Folder with Label** - Keeps each game folder named the same as its menu label. On by default. Folder names have no effect on the menu, so this is purely about keeping the card tidy.
- **Pad Track Numbers to Two Digits** - Writes `(Track 01)` instead of `(Track 1)`. On by default.
- **Check for Updates at Startup** - Looks for a newer release each time the application opens. On by default.

## Firmware Limits

The firmware imposes two limits, and both are flagged automatically.

The menu trims a label at **47 characters**. From 57 characters up, the menu keeps the last 8 characters after two dots rather than cutting the end off, so `70's Robot Anime - Geppy-X - The Super Boosted Armor (JP) (Disc 1)` draws as `70's Robot Anime - Geppy-X - The Super Boosted ..(Disc 1)`. Anything longer than 47 still works, it is trimmed on screen. A card assembled from Redump names runs into this often.

The path to a disc image, measured from the card root, cannot exceed **256 characters**. A game past that still appears in the menu but fails at launch with a path error on the console, so the application flags it as an error.

Metadata that macOS writes to external media (`.DS_Store`, `._` prefixed files, `.Spotlight-V100`, `.fseventsd`, `.Trashes`) can cause the xStation game list scan to stop part way through a directory, which silently drops games from the menu. The **Issues** window reports anything of the sort it finds.

## SD Card Compatibility

xStation Menu Refiner works with any existing xStation SD card out of the box, regardless of how it was set up. Flat cards, cards organized into subfolders at any depth, and cards previously managed by the command-line renamer are all read the same way. No migration or preparation is needed.

Any folder can also be opened as though it were a card, which makes it possible to prepare a collection on a hard drive before copying it across.

## Legal and Licensing

### xStation Menu Refiner
**Copyright (C) 2026, Derek Pascarella (ateam)**

Licensed under the GNU General Public License v3.0 (GPL-3.0).

For the full license text, see `LICENSE`.

### Third-Party Components
- [Avalonia UI](https://avaloniaui.net/) (MIT) - cross-platform GUI framework
- [MessageBox.Avalonia](https://github.com/AvaloniaCommunity/MessageBox.Avalonia) (MIT) - modal dialog helpers
