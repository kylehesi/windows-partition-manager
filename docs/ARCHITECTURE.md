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
8. [Safety model](#safety-model)
9. [Files the program writes](#files-the-program-writes)
10. [Testing](#testing)
11. [Known limitations](#known-limitations)
12. [Windows behaviours worth knowing](#windows-behaviours-worth-knowing)

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
│  └─ WindowsPartitionManager.Integration.Tests/ elevated end-to-end tests on throwaway VHDX disks
├─ installer/                                    WiX source of the MSI and its license page
├─ build/                                        package.ps1: release build, MSI, zip, checksums, scan
├─ assets/                                       application icon (a disk snapped in two) and the script that draws it
├─ WindowsPartitionManager-Setup.msi             the installer, rebuilt by build/package.ps1
└─ docs/                                         this document
```

Dependencies only point inward:

```
   App ─────┐
            ├──► Platform.Windows ──► Core
   Cli ─────┘                          ▲
   Core.Tests ─────────────────────────┘
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
| `Disk` | A physical or virtual disk: number, model, serial, bus type, size, sector sizes, partition style (GPT, MBR, RAW), system/boot/read-only/offline flags, health, Windows' own largest free extent, and its partitions ordered by offset. |
| `Partition` | A partition table entry: number, offset, size, kind (`Basic`, `EfiSystem`, `MicrosoftReserved`, `Recovery`, `Extended`, `Ldm`, ...), drive letter, flags and the mounted `Volume`, if any. |
| `Volume` | A mounted file system: GUID path, letter, label, file system, size, free space, health and the NTFS dirty bit. |
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

### Operations (`Operations/`)

| Type | Role |
|---|---|
| `CreatePartitionRequest`, `ResizePartitionRequest`, `FormatPartitionRequest`, `DeletePartitionRequest` | Immutable descriptions of a write, including the fingerprints of the disk and partition the user saw. |
| `PartitionPlanner` | Rules for creating (inside free space, aligned, at least 8 MiB, within Windows' largest free extent, FAT32 ≤ 32 GB, label length, letter free, MBR primary limit) and for deleting (never Windows, boot, EFI, MSR or Recovery partitions; on the system disk only basic data partitions). |
| `ResizePlanner` | Rules for shrinking (not below used data, not below Windows' own minimum, warn below 5 % free) and extending (only into unallocated space directly after the partition); NTFS/ReFS only. |
| `FormatPlanner` | Rules for formatting in place (basic data only, never the Windows volume, file-system limits, a warning that exFAT/FAT32 cannot be resized later). |
| `SafetyChecks` | Pre-flight rules shared by all writes: power source and battery level, volume health and dirty bit, removable-disk warning. |
| `LayoutGuard`, `DiskFingerprint`, `PartitionFingerprint` | The last check before a write: the disk (number, size, model, serial) and partition (number, offset, size) must still be exactly what the user saw. |
| `UnblockPlanner`, `UnblockRunner` | Turn a shrink report into concrete steps (hibernation, page file, shadow copies), apply them, record what changed after every step, and restore it later. |

Planners return a list of `ValidationIssue`s (errors block, warnings inform); the UI shows them
live while the user types, the CLI prints them, and the platform layer runs them again.

### Abstractions (`Abstractions/`)

`IStorageProvider` (read disks), `IStorageOperations` (writes), `IVolumeInspector` and
`IVolumeInspection` (NTFS analysis), `ISupportedSizeProvider` (Windows' shrink/extend limits),
`IUnblockOperations` (hibernation, page file, shadow copies) and `IPowerStatusProvider`.
Tests implement these with fakes; the platform layer implements them for real.

## Platform layer (`WindowsPartitionManager.Platform.Windows`)

| Component | What it does |
|---|---|
| `WmiStorageProvider` | Reads `MSFT_Disk`, `MSFT_Partition` and `MSFT_Volume` from the `root\Microsoft\Windows\Storage` WMI namespace and builds the model. The NTFS dirty bit is read live with `FSCTL_IS_VOLUME_DIRTY` because the WMI value is cached. Also calls `MSFT_Partition.GetSupportedSize` (Windows' own shrink/extend limits; slow, so the UI loads it in the background). |
| `WmiStorageOperations` | All writes: `MSFT_Disk.Initialize` and `CreatePartition`, `MSFT_Volume.Format`, `MSFT_Partition.AddAccessPath`, `Resize` and `DeleteObject`. See [Anatomy of an operation](#anatomy-of-an-operation). |
| `Wmi` | Query and invoke helpers; turns Storage Management API return codes and `ExtendedStatus` into readable messages. |
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

**Main window.** A toolbar (Refresh, Analyze, Resize, New partition, Delete, Format, Settings)
acts on the selected row; every row also has an **Actions** menu (button and right-click) listing
only what is possible for it. Each disk is a card with a title, a disk map and a partition table.

- `DiskMapControl` draws the disk with a `DrawingContext`: widths follow sizes but every segment
  is at least as wide as its own label, so small partitions (EFI, MSR, Recovery) stay readable;
  used space is drawn in a stronger colour; a tooltip describes the hovered segment.
- `PartitionColors` is the single colour scheme shared by the map and the table's row swatches.
- `MainViewModel` keeps one selection across all disks, rebuilds rows on refresh, and clears them
  when a refresh fails so stale rows cannot be acted on.

**Window width.** The main window opens with `SizeToContent="Width"`, so it is exactly as wide as
the toolbar and the partition table need; after the first disk read that width (plus room for a
vertical scroll bar) becomes the window's width and minimum width, and sizing is handed back to
the user. `DiskMapControl` therefore asks for only a modest width when measured; asking for all
available width would make a content-sized window as wide as the screen.

**Dialogs**, one view model each:

| Window | Purpose |
|---|---|
| `NewPartitionWindow` | Size, file system, label, drive letter, quick format; live validation; confirmation. |
| `ResizePartitionWindow` | New size with "Smallest" and "Largest" shortcuts; Windows' limits loaded in the background. |
| `DestructiveActionWindow` | Delete and format; the button only enables after the partition name is typed. |
| `ShrinkAnalysisWindow` | Runs the analysis with progress and cancellation; opens the unblock wizard. |
| `UnblockWindow` | Lists the unblock steps with their consequences; applies and restores. |
| `SettingsWindow` | Theme (White / Dark / Night), UI font, UI language, operation log location, pending restore, version. |

After every write attempt, successful or not, the dialog raises `AttemptFinished` and the main
window refreshes from Windows.

**Theming.** Fourteen brushes — `WindowBackground`, `CardBackground`, `CardBorder`,
`TextPrimary`, `TextSecondary`, `Accent`, `ButtonHoverBackground`, `Danger`, `Warning`,
`HeaderBackground`, `RowHoverBackground`, `RowSelectedBackground`, `ErrorBackground` and
`ErrorBorder` — plus the `UiFontFamily` font are referenced with `DynamicResource`, so they update
without a restart. Three `ResourceDictionary` files under `Themes/` define the same brush keys for
**White**, **Dark** and **Night**; `ThemeManager` swaps the dictionary in
`Application.Current.Resources.MergedDictionaries`, while the font is a single `UiFontFamily`
resource. The language choice is read from the same settings file by `LanguagePreference` in the
platform layer (shared with the CLI), which maps English to `en` and Turkish to `tr`, sets the
ambient and current-thread culture, and formats dates — English keeps the ISO `yyyy-MM-dd HH:mm`
form, other cultures use their short date/time. There are no resource files yet, so the strings
stay in English. At startup `ThemeManager` also points WPF's default element `Language` at the UI
culture, and changing the language updates the open windows' `Language` immediately. `SettingsStore`
reads and writes `%LOCALAPPDATA%\WindowsPartitionManager\settings.json` (atomically, temp file
then replace), falling back to White + Segoe UI + English when the file is missing or malformed.
The choices apply immediately and are applied on startup. `PartitionColors` (the disk map / table
swatch palette) and the toolbar action-glyph / status-indicator colours are deliberately untouched
by themes.

**Developer aid.** `--screenshot file.png [--size 1020x700]` loads the disks, renders the main
window to PNG and exits; it is used to check the layout without a human (1020 px is the minimum
window width and the table must fit there).

## Command-line tool (`WindowsPartitionManager.Cli`)

The executable is `wpm`. Every write command prints a plan and the validation issues, refuses on
errors and asks for a typed confirmation unless `--yes` is given. On start-up it applies the same
language preference as the desktop app (`LanguagePreference`), so numbers and dates in its output
follow the chosen culture.

| Command | Purpose |
|---|---|
| `wpm list` | Disks, partitions and free regions (works unelevated). |
| `wpm analyze <letter> [--target <size>]` | Shrink analysis: limits and blocking files. |
| `wpm layout <letter> [--limit N]` | Raw NTFS file layout dump; a debugging aid. |
| `wpm create <disk> (--size <size> \| --max) [--offset] [--fs] [--label] [--letter]` | Create and format a partition in unallocated space. |
| `wpm resize <disk> <partition> (--size \| --shrink-by \| --extend-by) <size>` | Shrink or extend an NTFS/ReFS partition. |
| `wpm format <disk> <partition> [--fs] [--label] [--full]` | Reformat a partition in place. |
| `wpm delete <disk> <partition>` | Delete a partition. |
| `wpm unblock <letter> [--hibernation] [--pagefile] [--shadows] [--all]`, `wpm unblock --restore` | The unblock wizard. |
| `wpm vhd new <file> --size <size>`, `wpm vhd detach <file>` | Create, attach and initialize a VHDX for experiments; detach it. |

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

## Safety model

Protection is layered so that no single mistake reaches the disk:

| Layer | Where | Protects against |
|---|---|---|
| Storage Management API only | `WmiStorageOperations` | Torn writes; Windows journals and validates. |
| Planners | `PartitionPlanner`, `ResizePlanner`, `FormatPlanner` | Impossible or destructive requests: overlap, shrinking below data, deleting boot partitions. |
| Pre-flight checks | `SafetyChecks` | Damaged or dirty volumes, low battery, resizing Windows on battery, unplugging removable disks. |
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
| `settings.json` | The selected theme, UI font and language. |
| `test-trace.log` | Integration tests only: a write-through breadcrumb per step, for diagnosing crashes. |

## Testing

### Unit tests (`WindowsPartitionManager.Core.Tests`)

About 100 xUnit tests over the pure logic, run anywhere without privileges: free-space math for
GPT and MBR, alignment and reserved areas; every planner rule; the shrink analyzer against an
in-memory fake volume (page file, shadow copies, immovable flags, unattributed clusters);
`ByteSize` parsing; `LayoutGuard`; the unblock runner, including a failure half-way through.

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
- Locked BitLocker volumes cannot be analysed; unlocked ones behave normally.
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
- **Integration tests run sequentially.** Several virtual disks changing at once interfere with
  each other (volume arrival, drive letters, VSS, post-format work).
