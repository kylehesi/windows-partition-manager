# Architecture

This document describes how Windows Partition Manager is built: the projects and their
responsibilities, how an operation travels from a click to the disk, the layers of protection
around every write, how the code is tested, and the Windows behaviours the design has to work
around. For what the program does and how to use it, see the [README](../README.md).

## Contents

1. [Principles](#principles)
2. [Solution layout](#solution-layout)
3. [Core](#core-windowspartitionmanagercore)
4. [Platform layer](#platform-layer-windowspartitionmanagerplatformwindows)
5. [Desktop app](#desktop-app-windowspartitionmanagerapp)
6. [Command-line tool](#command-line-tool-windowspartitionmanagercli)
7. [Anatomy of an operation](#anatomy-of-an-operation)
8. [The pending operations queue](#the-pending-operations-queue)
9. [Safety model](#safety-model)
10. [Files the program writes](#files-the-program-writes)
11. [Testing](#testing)
12. [Packaging](#packaging)
13. [Known limitations](#known-limitations)
14. [Windows behaviours worth knowing](#windows-behaviours-worth-knowing)

## Principles

- **Never write raw sectors.** Every change to a disk goes through the Windows Storage
  Management API, the same calls PowerShell's `New-Partition`, `Resize-Partition`,
  `Format-Volume` and `Remove-Partition` make. NTFS journaling and GPT redundancy therefore keep
  a power loss recoverable, and Windows refuses an operation rather than corrupting a volume.
  The program only reads raw structures (allocation bitmap, file layout) and only through
  documented, read-only file-system control codes.
- **Plan, validate, confirm, re-validate, write, log.** Every write is first described as a
  request, checked by a planner in pure code, shown to the user for confirmation, checked again
  against a fresh read of the disk immediately before writing, and recorded in an operation log.
- **Pure logic is separated from the operating system.** Layout arithmetic, validation rules and
  shrink analysis live in a library with no Windows dependency and are unit-tested in isolation.
  Everything that talks to Windows sits behind interfaces in a separate project.
- **Explain, don't just refuse.** When something cannot be done, the user gets the reason and
  what to do about it (which file blocks a shrink, which setting to change, which letter is taken).
- **No third-party runtime dependencies.** Plain WPF with a hand-written MVVM helper; the only
  NuGet package outside the SDK is `System.Management` (WMI).

## Solution layout

```
WindowsPartitionManager.slnx
├─ src/
│  ├─ WindowsPartitionManager.Core/              domain model, layout math, planners, analysis (no OS calls)
│  ├─ WindowsPartitionManager.Platform.Windows/  WMI, IOCTL/FSCTL and virtdisk access; implements Core's interfaces
│  ├─ WindowsPartitionManager.App/               WPF desktop application
│  └─ WindowsPartitionManager.Cli/               command-line tool "wpm"
├─ tests/
│  ├─ WindowsPartitionManager.Core.Tests/        xUnit unit tests for Core
│  ├─ WindowsPartitionManager.App.Tests/         xUnit tests for view models, settings and localization
│  └─ WindowsPartitionManager.Integration.Tests/ elevated end-to-end tests on throwaway VHDX disks
├─ installer/                                    WiX source of the MSI and its license page
├─ build/                                        package.ps1: release build, MSI, zip, checksums, scan
├─ assets/                                       app icon and README banner, and the scripts that draw them
├─ docs/                                         this document, ADDING_A_LANGUAGE.md, images/ for the README
├─ CHANGELOG.md, THIRD-PARTY-NOTICES.md
└─ WindowsPartitionManager-Setup.msi             the installer, rebuilt by build/package.ps1
```

Dependencies only point inward:

```
   App ─────┐
            ├──► Platform.Windows ──► Core
   Cli ─────┘                          ▲
   Core.Tests ─────────────────────────┘
   App.Tests ──► App
   Integration.Tests ──► Platform.Windows, Core
```

Technology: C# (latest language version) on .NET 10 LTS, WPF for the UI, xUnit for tests.
`Directory.Build.props` turns on nullable reference types and the recommended .NET analyzers for
every project; the build is kept warning-free.

## Core (`WindowsPartitionManager.Core`)

Pure .NET with no Windows API calls, so every rule here is unit-testable with in-memory data.

### Model (`Model/`)

| Type | Meaning |
|---|---|
| `Disk` | A physical or virtual disk: number, model, serial, bus type, size, sector sizes, partition style (GPT, MBR, RAW), system/boot/read-only/offline flags, health, Windows' own largest free extent, the backing file of an attached VHD/VHDX (`VirtualDiskPath`), and its partitions ordered by offset. |
| `Partition` | A partition table entry: number, offset, size, kind (`Basic`, `EfiSystem`, `MicrosoftReserved`, `Recovery`, `Extended`, `Ldm`, ...), drive letter, flags and the mounted `Volume`, if any. |
| `Volume` | A mounted file system: GUID path, letter, label, file system, size, free space, health, the NTFS dirty bit and, when elevated, its BitLocker state (`BitLockerStatus`: protection, lock and conversion state with percentage). |
| `DiskHealth` | What a drive reports about itself: media type (SSD, HDD, SCM), Windows' health status and the reliability counters (temperature, wear, read/write errors, power-on hours), each optional because many drives report only some of them. |
| `FreeRegion`, `DiskSegment` | Unallocated space, and the ordered "partition or free space" pieces the disk map and table display. |

All sizes are `ulong` bytes; they are formatted for humans only at the UI edge (`ByteSize`).

### Layout (`Layout/`)

`FreeSpaceCalculator` derives unallocated regions from a partition table:

- usable space starts after the protective MBR and primary GPT (34 sectors) or the MBR sector,
  and ends before the backup GPT (33 sectors) or, on MBR disks, before the last 1 MiB that
  Windows keeps free;
- every region start is aligned up and every usable end aligned down to 1 MiB, the alignment
  Windows uses, so a region never exceeds what Windows will accept;
- gaps under 8 MiB (alignment slivers, EBR gaps) are ignored;
- the MBR extended container is skipped in favour of the logical drives inside it.

`DiskLayout` merges partitions and free regions into the segments shown in the UI.

### Analysis (`Analysis/`)

`ShrinkAnalyzer` answers "how far can this NTFS volume shrink, and what is in the way?" from an
`IVolumeInspection` (geometry, allocation bitmap, file layout):

- **Minimum size without moving anything**: the last used cluster.
- **Minimum size after moving movable files**: the furthest extent of any unmovable file.
- **Minimum size once removable blockers are gone**: the furthest immovable NTFS data
  (`$MFT`, `$MFTMirr`, `$LogFile`, streams NTFS flags as immovable).
- **Blockers** past the target, classified as page file, hibernation file, shadow copy storage,
  NTFS metadata or file-system-immovable, each with a reason and advice.

`ClusterBitmap` counts used clusters with bit tricks; `ShrinkReportFormatter` renders the report
as text for both the CLI and the analysis window.

`SpaceAnalyzer` turns the flat MFT listing (one `SpaceEntry` per file: record number, parent,
name, allocated size) into a folder tree (`SpaceFolder`, `SpaceReport`): sizes are summed bottom
up, every record that is somebody's parent counts as a folder (so folders whose index fits in their
own MFT record are not lost), and records whose parent never appears are attached to the root
rather than dropped. To keep memory flat on large volumes only the 50 largest files per folder and
the 100 largest overall are kept by name; the rest only count towards their folder's total.

`DiskHealthAssessment` turns a `DiskHealth` into a verdict (`Good`, `Attention`, `Bad`, `Unknown`)
with findings. It is deliberately cautious: Windows' own "unhealthy", any uncorrected read or
write error, or SSD wear of 90 % or more mean *back up now*; Windows' "warning", wear of 70 % or
more, or a temperature at the drive's own limit (or a default per media type) mean *attention*. A
drive without counters is *good* only if Windows itself calls it healthy, otherwise *unknown*.

### Operations (`Operations/`)

| Type | Role |
|---|---|
| `CreatePartitionRequest`, `ResizePartitionRequest`, `FormatPartitionRequest`, `DeletePartitionRequest` | Immutable descriptions of a write, including the fingerprints of the disk and partition the user saw. |
| `PartitionPlanner` | Rules for creating (inside free space, aligned, at least 8 MiB, within Windows' largest free extent, FAT32 ≤ 32 GB, label length, letter free, MBR primary limit) and for deleting (never Windows, boot, EFI, MSR or Recovery partitions; on the system disk only basic data partitions). |
| `ResizePlanner` | Rules for shrinking (not below used data, not below Windows' own minimum, warn below 5 % free) and extending (only into unallocated space directly after the partition); NTFS/ReFS only. |
| `ShrinkAndCreatePlanner` | The one-step "shrink and create": validates the resize half against the live layout and the create half against the layout as it will be once the shrink has happened, so both can be confirmed up front. |
| `FormatPlanner` | Rules for formatting in place (basic data only, never the Windows volume, file-system limits, a warning that exFAT/FAT32 cannot be resized later). |
| `SafetyChecks` | Pre-flight rules shared by all writes: power source and battery level, volume health and dirty bit, removable-disk warning. |
| `LayoutGuard`, `DiskFingerprint`, `PartitionFingerprint` | The last check before a write: the disk (number, size, model, serial) and partition (number, offset, size) must still be exactly what the user saw. |
| `UnblockPlanner`, `UnblockRunner` | Turn a shrink report into concrete steps (hibernation, page file, shadow copies), apply them, record what changed after every step, and restore it later. |
| `DriveLetterPlanner`, `LabelPlanner` | Change or remove a drive letter (never the Windows volume's letter, the new letter must be free) and rename a volume (basic data partitions only; at most 11 characters on FAT, 15 on exFAT and 32 on NTFS/ReFS, and no FAT-forbidden characters). |
| `DiskStatePlanner` | Online/offline and read-only/writable for a whole disk; offers only the changes that make sense for its current state and never touches the disk Windows runs from. |
| `ConvertDiskPlanner` | MBR ↔ GPT for an empty disk only (a Microsoft Reserved partition does not count as data); the Windows disk is refused with a pointer to `MBR2GPT`. |
| `VirtualDiskPlanner` | Create, attach and detach requests for virtual disks: a new file must be a full `.vhdx` path that does not exist yet, between 64 MiB and 64 TiB and within the free space of its drive; attach takes VHD and VHDX; detach only disks that really are attached virtual disks. |
| `CheckPlanner`, `CheckResult` | Online scan, spot fix and full offline repair (`CheckMode`). On the Windows volume only a scan runs online; a repair is explained as chkdsk at the next restart. `CheckResult.FromCode` maps `Repair-Volume`'s status codes (0 no errors … 13 needs an offline scan and fix) to outcomes. |
| `OptimizePlanner` | TRIM or defragmentation (`OptimizeMode`); recommends the mode that suits the media type and warns about defragmenting an SSD or trimming a hard disk. |
| `SafetyChecks.Encryption` | BitLocker rules per use (`EncryptionUse`): a locked volume is refused for everything except delete and format, which only warn that its contents are lost for good; a volume still converting is not resized; resizing an encrypted volume reminds the user to have the recovery key at hand. |
| `OperationHistory` | Parses `operations.log` back into entries, newest first, for the history window. |
| `PlannedOperation`, `LayoutSimulator`, `OperationPlan`, `PlanRunner` | The pending operations queue; see [below](#the-pending-operations-queue). |

Planners return a list of `ValidationIssue`s (errors block, warnings inform); the UI shows them
live while the user types, the CLI prints them, and the platform layer runs them again.

`Formatting/` holds `ByteSize` (parsing and formatting sizes) and `DiskJson`, the stable JSON
shape behind `wpm list --json`: camelCase, sizes in bytes, a `schemaVersion` (currently 1) and an
optional `bitLocker` block per volume.

### Abstractions (`Abstractions/`)

`IStorageProvider` (read disks), `IStorageOperations` (every write: initialize, create, format,
delete, resize, drive letter, label, check, optimize, convert, disk state), `IVolumeInspector` and
`IVolumeInspection` (NTFS analysis), `IVolumeSpaceReader` (the MFT listing for the space
analysis), `ISupportedSizeProvider` (Windows' shrink/extend limits), `IVirtualDiskOperations`,
`IDiskHealthProvider`, `IUnblockOperations` (hibernation, page file, shadow copies) and
`IPowerStatusProvider`. Tests implement these with fakes; the platform layer implements them for
real.

## Platform layer (`WindowsPartitionManager.Platform.Windows`)

| Component | What it does |
|---|---|
| `WmiStorageProvider` | Reads `MSFT_Disk`, `MSFT_Partition` and `MSFT_Volume` from the `root\Microsoft\Windows\Storage` WMI namespace and builds the model. The NTFS dirty bit is read live with `FSCTL_IS_VOLUME_DIRTY` because the WMI value is cached. Also calls `MSFT_Partition.GetSupportedSize` (Windows' own shrink/extend limits; slow, so the UI loads it in the background). |
| `WmiStorageOperations` | All writes, each inside `Guarded(...)`: `MSFT_Disk.Initialize`, `CreatePartition`, `Clear` (followed by `Initialize` with the other style to convert an empty disk), `Online`/`Offline` and `SetAttributes`; `MSFT_Volume.Format`, `SetFileSystemLabel`, `Repair` and `Optimize`; `MSFT_Partition.AddAccessPath`/`RemoveAccessPath`, `Resize` and `DeleteObject`. See [Anatomy of an operation](#anatomy-of-an-operation). |
| `Wmi` | Query and invoke helpers; turns Storage Management API return codes (including the `Optimize` errors 43022–43032) and `ExtendedStatus` into readable messages. |
| `WmiDiskHealthProvider` | `MSFT_PhysicalDisk` for the media type and health, and its `MSFT_StorageReliabilityCounter` reached through `GetRelated` (the class cannot be enumerated on its own). The counters need elevation. |
| `BitLockerReader` | `Win32_EncryptableVolume` in `root\CIMV2\Security\MicrosoftVolumeEncryption` (the `ProtectionStatus` property, `GetLockStatus`, `GetConversionStatus`), merged into the volumes. Skipped when not elevated, where the query only times out. |
| `NtfsSpaceReader` | The space analysis's input: one `FSCTL_QUERY_FILE_LAYOUT` pass with names and stream sizes, including streams without allocated clusters, so small files and folders are counted too. |
| `VirtualDiskOperations` | `IVirtualDiskOperations` on top of `VirtualDisk`: create, attach with drive letters, find the disk that belongs to a file, detach through the storage operations so it is logged. |
| `NtfsVolumeInspector` | Read-only NTFS analysis on a raw volume handle (requires elevation): `FSCTL_GET_NTFS_VOLUME_DATA` (geometry, MFT location), `FSCTL_GET_VOLUME_BITMAP` (allocation bitmap) and `FSCTL_QUERY_FILE_LAYOUT`, which returns every MFT record's names, streams, cluster runs and "immovable" flags in one pass without opening a single file. A 190,000-file system volume is analysed in about three seconds. |
| `UnblockOperations` | Hibernation via `powercfg.exe` (full System32 path), page file settings via `Win32_ComputerSystem` / `Win32_PageFileSetting`, shadow copies via `Win32_ShadowCopy`. When the page file is removed from a volume and no other would remain, a system-managed page file is set up on another internal NTFS volume with at least 8 GB free, so the computer is not left without one. The restore record lives in a JSON file. |
| `VirtualDisk` | Creates, opens, attaches and detaches VHDX files through `virtdisk.dll`. Detaching takes the disk offline first, waits until it is gone and lets device-removal notifications settle (see [Windows behaviours](#windows-behaviours-worth-knowing)). Used by `wpm vhd` and the integration tests. |
| `PowerStatusProvider`, `KeepAwake` | `GetSystemPowerStatus` for battery rules; `SetThreadExecutionState` keeps the computer from sleeping during a write. |
| `OperationLog` | Appends one line per write attempt with its outcome; written through to disk so it survives a crash. |
| `LanguagePreference` | Reads the `language` field from `settings.json`, maps it to a culture and applies it, and formats dates; shared by the app and the CLI. |
| `PartitionTypes` | GPT type GUIDs and MBR type bytes to `PartitionKind`, bus type codes to names. |
| `Interop/` | `LibraryImport` declarations (kernel32, advapi32) and privilege handling (`SeManageVolumePrivilege` for permanent VHD attach). |

## Desktop app (`WindowsPartitionManager.App`)

WPF, MVVM without a framework (`RelayCommand`, `INotifyPropertyChanged` view models). The
manifest requests administrator rights because every partition change needs them; a development
manifest (`-p:DevManifest=true`) builds an unelevated copy into `bin/<Configuration>/dev/` for
read-only checks.

**Two layouts.** Both main windows are views of the same `MainViewModel`, so the disks, the
selection and the queue survive a switch. `AppSettings.Layout` (`"Modern"`, the default, or
`"Classic"`) picks one at startup; changing it in Settings swaps the window when Settings closes
(`App.SwitchLayout` creates the other window, carries over its `Language`, shows it and closes the
old one).

- **`ModernMainWindow`**: a sidebar with the disks (`SelectedDisk`, kept across refreshes by disk
  number) and the app-wide commands; the selected disk with a large `DiskMapControl` and one card
  per partition or free region; and a details panel for `SelectedRow` with its properties
  (`SegmentRow.Details`) and its actions as buttons. Clicking a map segment selects its card
  (`SegmentClicked`), and the map outlines the selected one (`SelectedSegment`).
- **`MainWindow`** (Classic): a toolbar that acts on the selected row and one card per disk with a
  map and a fixed-column partition table. It opens with `SizeToContent="Width"`, so it is exactly
  as wide as the toolbar and the table need; after the first read that width (plus room for a
  scroll bar) becomes its width and minimum. `DiskMapControl` therefore asks for only a modest
  width when measured; asking for all available width would make a content-sized window as wide
  as the screen.

Shared by both:

- Every row's actions are `RowAction`s (name, command, glyph, shortcut, destructive flag) listing
  only what is possible for that row; the disk's own menu lists `DiskAction`s (health, online and
  offline, read-only, convert, detach). Both menus are defined once in `App.xaml`.
- `DiskMapControl` draws the disk with a `DrawingContext`: widths follow sizes but every segment
  is at least as wide as its own label, so small partitions stay readable; used space is drawn in
  a stronger colour, planned partitions with a dashed outline, and a tooltip describes the hovered
  segment. `PartitionColors` is the one colour scheme shared by the map, the swatches and the
  usage bars.
- `PlanPanel` shows the queue; `DeviceChangeRefresher` re-reads the disks 1.5 s after
  `WM_DEVICECHANGE` settles (a drive plugged in or removed), deferred while a dialog is open.
- Key bindings: F5 refresh, F2 drive letter and label, Del delete, each through the same command
  and confirmation as the menus.
- While the queue holds steps, the rows show the projected layout and actions that act on the
  disk immediately (check, optimize, letter and label, initialize, disk state, convert, detach)
  are disabled.

**Dialogs**, one view model each:

| Window | Purpose |
|---|---|
| `NewPartitionWindow` | Size, file system, label, drive letter, quick format; live validation; confirmation. |
| `ResizePartitionWindow` | New size with "Smallest" and "Largest" shortcuts; Windows' limits loaded in the background. An optional *create a partition in the freed space* section turns a shrink into the one-step "shrink and create" flow. |
| `DestructiveActionWindow` | Delete and format; the button only enables after the partition name is typed. |
| `ShrinkAnalysisWindow` | Runs the analysis with progress and cancellation; opens the unblock wizard. |
| `UnblockWindow` | Lists the unblock steps with their consequences; applies and restores. |
| `VolumePropertiesWindow` | Drive letter (or none) and volume label. |
| `CheckVolumeWindow` | Scan without confirmation, fixes with one; preselects the next sensible step; explains chkdsk at restart for the Windows volume. |
| `OptimizeVolumeWindow` | Reads the media type first and preselects, and marks, the recommended mode. |
| `SpaceAnalysisWindow` | Not modal: folders and files largest first with share bars, drill-down (double-click or Enter, Backspace to go up), Open in Explorer, the largest files of the drive. |
| `DiskHealthWindow` | The verdict, the counters the drive reports, and what they mean. |
| `CreateVirtualDiskWindow` | File, size and partition table of a new VHDX. |
| `HistoryWindow` | The operation log as a table, newest first, failures in red. |
| `SettingsWindow` | Theme and layout, UI font, UI language, operation log location and history, pending restore, version. |

The new-partition, resize and delete/format forms also offer **Add to queue** (`Queued` event)
next to applying immediately.

After every write attempt, successful or not, the dialog raises `AttemptFinished` and the main
window refreshes from Windows.

**Theming.** Sixteen brushes — `WindowBackground`, `CardBackground`, `CardBorder`,
`TextPrimary`, `TextSecondary`, `Accent`, `ButtonHoverBackground`, `Danger`, `Warning`,
`HeaderBackground`, `RowHoverBackground`, `RowSelectedBackground`, `ErrorBackground`,
`ErrorBorder`, `ScrollThumb` and `ScrollThumbHover` — plus the `UiFontFamily` font are referenced with `DynamicResource`, so they update
without a restart. Three `ResourceDictionary` files under `Themes/` define the same brush keys for
**White**, **Dark** and **Night**; `ThemeManager` swaps the dictionary in
`Application.Current.Resources.MergedDictionaries`, while the font is a single `UiFontFamily`
resource. The language choice is read from the same settings file by `LanguagePreference` in the
platform layer (shared with the CLI), which maps English to `en` and Turkish to `tr`, sets the
ambient and current-thread culture, and formats dates — English keeps the ISO `yyyy-MM-dd HH:mm`
form, other cultures use their short date/time. The user-facing strings live in `Strings.resx`
(neutral English) with a `Strings.tr.resx` Turkish satellite; `Localization` rebuilds a
`ResourceDictionary` of every key for the chosen culture and swaps it into the application's
merged dictionaries (the same pattern as the theme), and the view models read strings through
`Localization.Get` / `Localization.Format`. The XAML reads them with `DynamicResource` (column
headers are wrapped in `TextBlock`s because `GridViewColumn` is not a `FrameworkElement`).
Messages that originate below the UI (planner and `SafetyChecks` issues, `LayoutGuard`
refusals, unblock steps and progress, the shrink analysis and its report, partition type names,
Storage Management API return codes and the operations layer's own messages) come from
`CoreText` in Core, backed by `Localization/CoreStrings.resx` and `CoreStrings.tr.resx`. Its
culture is a property rather than the thread culture: it stays English unless `Localization`
switches it with the user's choice, so the CLI and the tests keep English output whatever language
Windows runs in. No rule depends on message text. The code, identifiers and comments stay in
English.

Implicit styles in `App.xaml` give every stock control the theme brushes. The ComboBox, GroupBox,
ContextMenu, MenuItem and ScrollBar need full templates: their stock templates hardcode light
colours (the ComboBox's drop-down button, the GroupBox's white inner border, the scroll bar
track), and menus and tooltips open in their own popup windows. The menu template has an icon
column (`MenuItem.Icon` holds a Segoe Fluent Icons character), the header and the shortcut
(`InputGestureText`), and draws a rounded popup with a soft shadow. `UsageBar` is a thin rounded
`ProgressBar` style for used space. At startup
`ThemeManager` also points WPF's default element `Language` at the UI
culture, and changing the language updates the open windows' `Language` immediately. `SettingsStore`
reads and writes `%LOCALAPPDATA%\WindowsPartitionManager\settings.json` (atomically, temp file
then replace), falling back to White + Segoe UI + English + Modern for whatever is missing,
unknown or malformed.
The choices apply immediately and are applied on startup. `PartitionColors` (the disk map / table
swatch palette) and the toolbar action-glyph / status-indicator colours are deliberately untouched
by themes.

**Developer aid.** `--screenshot file.png [--size 1180x720] [--theme Dark] [--language Turkish]
[--layout Classic] [--dialog <name>]` loads the disks, renders the main window to PNG and exits;
it is used to check the layout without a human (1020 px is the minimum width of both layouts).
`--dialog` opens a dialog for the first row that allows it and captures that instead: `settings`,
`history`, `health`, `vhd`, `new`, `resize`, `properties`, `check`, `optimize`, `space`, `format`,
`delete`; `plan` queues a paper-only partition to show the queue, and `select` selects a
partition so the details panel shows. Nothing is saved and no dialog is ever applied.

## Command-line tool (`WindowsPartitionManager.Cli`)

The executable is `wpm`. Every write command prints a plan and the validation issues, refuses on
errors and asks for a typed confirmation unless `--yes` is given. On start-up it applies the same
language preference as the desktop app (`LanguagePreference`), so numbers and dates in its output
follow the chosen culture.

| Command | Purpose |
|---|---|
| `wpm list [--json]` | Disks, partitions and free regions (works unelevated); `--json` emits `DiskJson`. |
| `wpm analyze <letter> [--target <size>]` | Shrink analysis: limits and blocking files. |
| `wpm layout <letter> [--limit N]` | Raw NTFS file layout dump; a debugging aid. |
| `wpm create <disk> (--size <size> \| --max) [--offset] [--fs] [--label] [--letter]` | Create and format a partition in unallocated space. |
| `wpm resize <disk> <partition> (--size \| --shrink-by \| --extend-by) <size>` | Shrink or extend an NTFS/ReFS partition. |
| `wpm format <disk> <partition> [--fs] [--label] [--full]` | Reformat a partition in place. |
| `wpm delete <disk> <partition>` | Delete a partition. |
| `wpm letter <disk> <partition> (<X> \| --remove)`, `wpm label <disk> <partition> <text>` | Drive letter and volume label. |
| `wpm check <disk> <partition> [--spotfix \| --fix]` | Check and repair a file system. |
| `wpm optimize <disk> <partition> [--trim \| --defrag]` | TRIM or defragment; by default what suits the media type. |
| `wpm space <letter> [--top N]` | Largest folders (two levels) and files. |
| `wpm health [<disk>]` | Health verdict and reliability counters. |
| `wpm convert <disk> (gpt \| mbr)` | Partition table of an empty disk. |
| `wpm disk <disk> (online \| offline \| readonly \| writable)` | Disk state. |
| `wpm unblock <letter> [--hibernation] [--pagefile] [--shadows] [--all]`, `wpm unblock --restore` | The unblock wizard. |
| `wpm vhd new <file> --size <size>`, `wpm vhd attach <file>`, `wpm vhd detach <file>` | Virtual disks; `new` attaches with drive letters and initializes GPT, `detach` finds the disk by its file. |

Sizes accept `500GB`, `1.5TB`, `20480MB` or plain bytes, in binary units like Explorer;
ambiguous forms such as `1.500GB` are rejected instead of guessed.

## Anatomy of an operation

Shrinking C: and creating a new drive in the freed space, as it flows through the code:

1. **Read.** `WmiStorageProvider` builds the model; `DiskLayout` produces the rows and map.
2. **Analyse (optional).** `NtfsVolumeInspector` reads the bitmap and file layout;
   `ShrinkAnalyzer` reports the limits and blockers; `UnblockPlanner` proposes steps.
3. **Plan.** The resize form builds a `ResizePartitionRequest` carrying `DiskFingerprint` and
   `PartitionFingerprint`; `ResizePlanner`, `SafetyChecks` and Windows' own limits validate it on
   every keystroke.
4. **Confirm.** The user confirms a summary of the change.
5. **Guard.** Inside `WmiStorageOperations`, wrapped in `Guarded(...)` (sleep inhibited,
   outcome logged):
   1. re-read the disk from Windows;
   2. `LayoutGuard` checks the disk and partition are still the ones the user saw;
   3. volume health and dirty bit are checked;
   4. `ResizePlanner` and the power rules run again on the fresh layout;
   5. any error stops here with "Refused before touching the disk", and nothing has been written.
6. **Write.** `MSFT_Partition.Resize`: Windows moves movable files, shrinks NTFS, then shrinks
   the partition entry.
7. **Read back.** The partition is re-read and returned; the main window refreshes.
8. **Create.** The same path with `CreatePartitionRequest`: `CreatePartition` (no letter yet),
   wait for the volume to appear, `Format`, then `AddAccessPath`. If the volume never appears or
   the format fails, the just-created empty partition is deleted again; if the chosen letter is
   taken, Windows assigns the next free one.

## The pending operations queue

The queue lets the user plan several changes, see the result and apply them with one
confirmation, without weakening the "check against a fresh read" rule.

- **`PlannedOperation`**: `PlannedCreate`, `PlannedDelete`, `PlannedResize` and `PlannedFormat`.
  Partitions are addressed by **offset**, not by number, because numbers are only assigned when a
  partition really exists (and may change).
- **`LayoutSimulator`** applies a step to a `Disk` on paper. A planned partition gets a volume
  whose path starts with `planned:`, which is how the UI recognises and marks it.
- **`OperationPlan`** holds the steps and the projected disks. `TryAdd` validates a new step with
  the normal planners against the *projected* layout (so a create can use space a planned shrink
  frees); `RemoveAt` also removes the steps that depend on the removed one; `Rebase` re-applies
  the plan to a fresh read after a refresh and drops steps that no longer fit.
- **`PlanRunner`** applies the plan step by step. Before each step it reads the disk again and
  compares it with what the plan projected for that moment (same partition starts, sizes within
  1 MiB); it resolves the partition by offset, builds the real request with the actual partition
  number and fresh fingerprints, and sends it through the normal operations layer, so every step
  gets the full guard of [Anatomy of an operation](#anatomy-of-an-operation). The first
  difference or failure stops the run; the remaining steps stay queued (`PlanRunResult`).

## Safety model

Protection is layered so that no single mistake reaches the disk:

| Layer | Where | Protects against |
|---|---|---|
| Storage Management API only | `WmiStorageOperations` | Torn writes; Windows journals and validates. |
| Planners | `PartitionPlanner`, `ResizePlanner`, `FormatPlanner` and the others | Impossible or destructive requests: overlap, shrinking below data, deleting boot partitions, converting a disk with data, taking Windows' disk offline. |
| Pre-flight checks | `SafetyChecks` | Damaged or dirty volumes, BitLocker-locked or converting volumes, low battery, resizing Windows on battery, unplugging removable disks. |
| Step-by-step queue | `PlanRunner` | A plan applied to a disk that no longer looks the way the plan expects. |
| Confirmation | UI dialogs, CLI prompts | Accidental clicks; destructive actions need the partition name typed. |
| Re-validation and fingerprints | `WmiStorageOperations` + `LayoutGuard` | Stale screens, renumbered disks after a USB replug, callers that skipped validation. |
| Cleanup | create path | Half-created partitions after a failed format. |
| Keep awake | `KeepAwake` | Sleep in the middle of a resize. |
| Crash-proof log | `OperationLog` | Not knowing what happened before a crash or power loss. |
| Restore record | `UnblockRunner` | Settings changed by the unblock wizard being forgotten, even after a partial failure. |

**Power loss.** Creating or deleting a partition touches only the partition table, which GPT
keeps in two CRC-protected copies; formatting writes only inside the new partition; resizing is
NTFS-journaled and changes the file system before the partition table when shrinking (after it
when extending), so an interruption leaves a partition merely larger than its file system. An
integration test pulled a virtual disk out mid-shrink: Windows abandoned the resize, the
partition kept its old size, all 978 files were intact and `chkdsk` found nothing.

## Files the program writes

All under `%LOCALAPPDATA%\WindowsPartitionManager\`:

| File | Content |
|---|---|
| `operations.log` | One line per write attempt: time, operation, parameters, outcome. Written through to disk. |
| `unblock-state.json` | What the unblock wizard changed, until it is restored. |
| `settings.json` | The selected theme, UI font, language and layout. |
| `test-trace.log` | Integration tests only: a write-through breadcrumb per step, for diagnosing crashes. |

## Testing

### Unit tests

`WindowsPartitionManager.Core.Tests` (about 200 tests) covers the pure logic and runs anywhere
without privileges: free-space math for GPT and MBR, alignment and reserved areas; every planner
rule, including letters, labels, disk state, conversion, check and optimize modes and the
BitLocker rules; the shrink analyzer against an in-memory fake volume; the space analyzer's tree;
the health verdicts; the queue (simulation, validation against the projection, dependent steps,
rebasing, and `PlanRunner` stopping at the first surprise); `ByteSize`, `DiskJson`, the operation
history parser, `LayoutGuard` and the unblock runner, including a failure half-way through.

`WindowsPartitionManager.App.Tests` (about 50 tests) covers the app's own logic with fake
platform services: settings round trips and fallbacks (theme, font, language, layout), the
language preference and date formats, the resource dictionaries behind theme and language
switches, the BitLocker wording, and the dialog view models (check, optimize, disk health,
history, space analysis, drive letter and label).

### Integration tests (`WindowsPartitionManager.Integration.Tests`)

End-to-end tests against throwaway VHDX disks created in `%TEMP%`. They need an elevated test
host and skip themselves otherwise (`[AdminFact]`).

| Test | Proves |
|---|---|
| `VhdxPartitionTests` | Initialize, create, format, write a file, second partition with a chosen letter, delete; free-space math equals Windows' largest free extent. |
| `VhdxResizeTests` | Shrink with data on the volume, create in the freed space, extending refused while blocked, extend back; data intact throughout. |
| `VhdxFormatTests` | The flash-drive path on MBR: exFAT stick, format to NTFS in place keeping the letter, shrink, split. |
| `VhdxSafetyTests` | A dirty volume is refused by the planner and by the operations layer on its own; every attempt is logged. |
| `VhdxUnblockTests` | A real VSS snapshot is counted and deleted; page file and hibernation settings are readable. |
| `RehearsalTests` | A copy of the developer's real disk layout (system NTFS, Recovery, data NTFS, EFI, free tail) filled with hundreds of hashed files placed at the end of the volume, random byte markers in every area that must not change, and `chkdsk` after each of shrink, create, delete and extend. |
| `GuardTests` | Ten bad or stale requests sent straight to the operations layer are refused, and the primary and backup GPT stay byte-identical. |
| `VhdxVolumePropertiesTests` | Change a drive letter, rename, remove the letter and give it back; the files stay. |
| `VhdxDiskStateTests` | Read-only blocks writes, offline hides the drive; both are reversible and the data survives. |
| `VhdxConvertTests` | GPT to MBR and back while the disk is empty; a disk with a partition is never touched. |
| `VhdxCheckTests` | Scan, spot fix and a full fix that clears the dirty bit on NTFS; the full check on exFAT. |
| `VhdxOptimizeTests` | TRIM and defragmentation on NTFS, defragmentation on FAT32; the files are unchanged. |
| `VhdxSpaceTests` | The space analysis finds deep and small folders and its sizes add up. |
| `VhdxManagementTests` | The virtual disk flow: create, use, detach, attach again; the disk knows its file. |
| `VhdxPlanTests` | A five-step plan (create, format, shrink, create, delete) applied by `PlanRunner` ends exactly as projected, with the data intact. |
| `ShrinkInterruptedBySurpriseRemoval` | Opt-in (`[SurpriseRemovalFact]`, `WPM_RUN_SURPRISE_REMOVAL=1`): pulls the disk mid-shrink and checks consistency. |

**Tests must never reach a real disk.** `TestVhd` and every test helper refuse to act on any
disk that is not a virtual disk, or that is a system or boot disk.

## Packaging

`build/package.ps1` produces everything in `artifacts/` (ignored by git):

1. Runs the unit tests.
2. Publishes the app and the CLI **self-contained** for `win-x64`, so the target computer needs
   no .NET installation:
   - `artifacts/install/`: both programs in one folder sharing one private runtime; this is what
     the MSI installs. About 240 of its 246 binaries are Microsoft-signed runtime files.
   - `artifacts/portable/`: both programs as single-file executables, plus the five native WPF
     DLLs next to them.
3. Signs our own binaries with `signtool` when `WPM_SIGN_THUMBPRINT` names a code-signing
   certificate.
4. Builds the MSI from `installer/Package.wxs` with WiX Toolset 5 and its UI and Util
   extensions, then signs it. The package installs per machine into Program Files, adds
   desktop (public desktop) and Start menu shortcuts and `wpm` on the system PATH, registers an
   Apps entry with icon and install location, shows the license page (`installer/license.rtf`:
   the disclaimer of warranty and limitation of liability from `DISCLAIMER.txt`, then the MIT
   license; Next stays disabled until the user accepts), offers "Launch Windows
   Partition Manager" on the last page (`WixShellExec`, so the app's own UAC prompt appears), and
   upgrades earlier installs, including the same version, by a fixed `UpgradeCode`. A copy is
   written to the repository root as `WindowsPartitionManager-Setup.msi`.
   `LICENSE.txt` and `DISCLAIMER.txt` are copied into both the install folder and the portable
   build, so every copy carries them.
5. Zips the portable build, writes `SHA256SUMS.txt` and scans the artifacts with Microsoft
   Defender, failing the build if anything is detected.

**Avoiding antivirus false positives.** Heuristic scanners react to behaviour typical of malware
droppers, so the packaging deliberately avoids it:

- no packers, obfuscators or compressed single-file bundles (`EnableCompressionInSingleFile`
  is off);
- the single-file executables never extract native DLLs into `%TEMP%` at start-up
  (`IncludeNativeLibrariesForSelfExtract` is off), the DLLs ship next to the exe instead;
- every executable carries product, company, description, version and copyright information and
  an icon;
- the installer is a plain Windows Installer package rather than a custom setup executable;
- the app's manifest declares honestly that it needs administrator rights.

What remains is SmartScreen's reputation check for unsigned downloads ("Windows protected your
PC"), which only a code-signing certificate, or reputation built up over time, removes.

## Known limitations

- Basic GPT and MBR disks only; dynamic disks and Storage Spaces are shown but not modified.
- Only NTFS and ReFS can be shrunk or extended (a Windows limitation); FAT32 and exFAT can only
  be formatted or deleted.
- A volume cannot be shrunk below its immovable NTFS metadata while mounted; offline metadata
  relocation is a roadmap item and would be the only exception to "no raw writes".
- Extending works only into unallocated space directly after the partition.
- Locked BitLocker volumes are shown but never resized, checked, optimized or renamed;
  BitLocker state is only read when the program runs elevated.
- The shrink and space analyses read NTFS only.
- Many USB enclosures and virtual disks report no reliability counters; their health verdict is
  then *unknown*.
- The queue covers create, delete, resize and format; the other actions apply immediately.
- Windows-only by nature; Core is portable, but nothing else is.

## Windows behaviours worth knowing

Lessons learned while building and testing, each handled in the code:

- **Largest free extent.** `CreatePartition` fails with "not enough free space" (40000) above
  `MSFT_Disk.LargestFreeExtent`. Windows aligns the usable end down to 1 MiB, so the free-space
  calculator does too, and the planner checks against Windows' number.
- **MBR tail reserve.** Windows keeps the last 1 MiB of an MBR disk free (room for an LDM
  database) and refuses partitions that reach into it.
- **`AddAccessPath`.** `AccessPath` and `AssignDriveLetter` are mutually exclusive; passing both,
  even with `AssignDriveLetter = false`, is "Invalid parameter" (5).
- **Permanent VHD attach** needs `SeManageVolumePrivilege` *enabled*; administrators hold it
  disabled until a program asks.
- **Dirty bit.** `MSFT_Volume.DirtyBitSet` is cached and lagged by more than 30 seconds in
  testing; `FSCTL_IS_VOLUME_DIRTY` on a handle opened with `GENERIC_READ` answers immediately.
- **`STREAM_LAYOUT_ENTRY`.** `StreamInformationOffset` is 4 bytes, so `AttributeTypeCode` sits at
  offset 36 and the stream identifier at 48, not where a quick reading of the documentation
  suggests.
- **Detaching a VHDX.** Pulling a virtual disk out from under mounted, freshly written volumes
  loses cached writes ("error during a paging operation", disk event 51). Combined with an
  immediate re-attach, it preceded two host bugchecks on the development machine. Detaching
  therefore takes the disk offline first, waits until it is gone and lets notifications settle;
  after that change the full test suite ran with zero paging errors and no crash.
- **Partition numbers.** On GPT, deleting a partition did not renumber the others in testing,
  but nothing relies on that: fingerprints compare offset and size as well.
- **Changing a drive letter.** `AddAccessPath` with a new letter while the old one is still
  assigned fails with 42012; the old letter is removed first, the new one added, and the old one
  put back if adding fails.
- **Reliability counters.** `MSFT_StorageReliabilityCounter` cannot be enumerated directly; it
  is reached from its `MSFT_PhysicalDisk` with `GetRelated`.
- **BitLocker unelevated.** Querying `Win32_EncryptableVolume` without administrator rights does
  not fail fast: it took about 5 seconds per refresh, so it is skipped unless elevated.
- **`FSCTL_QUERY_FILE_LAYOUT` and small folders.** Without
  `QUERY_FILE_LAYOUT_INCLUDE_STREAMS_WITH_NO_CLUSTERS_ALLOCATED` (0x20) the listing leaves out
  records whose data is resident, so folders whose index fits in their MFT record vanish and
  their contents look orphaned.
- **chkdsk output** is written in the OEM code page (437 on English systems), not UTF-8.
- **Integration tests run sequentially.** Several virtual disks changing at once interfere with
  each other (volume arrival, drive letters, VSS, post-format work).
