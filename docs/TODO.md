# Documentation to update

Features are landing faster than the docs; README.md and docs/ARCHITECTURE.md are brought up to
date in one pass before the release. Everything below still needs to be written up.

- Drive letter: change or remove a partition's letter (`DriveLetterPlanner`, `ChangeDriveLetterRequest`).
- Volume label: rename a volume (`LabelPlanner`, `SetLabelRequest`).
- CLI: `wpm letter <disk> <partition> (<X> | --remove)` and `wpm label <disk> <partition> <text>`.
- UI: "Drive letter and label…" row action and dialog (`VolumePropertiesWindow`); the Windows drive keeps its letter.
- Screenshot aid: `--dialog properties`.
- Disk state: online/offline and read-only/writable (`DiskStatePlanner`, `SetDiskStateRequest`); Windows' own disk is refused.
- CLI: `wpm disk <disk> (online | offline | readonly | writable)`.
- UI: "Disk ▾" menu in each disk card (online/offline, read-only/writable); hidden on Windows' own disk.
- Auto refresh: the disk list re-reads itself 1.5 s after a drive is plugged in or removed (WM_DEVICECHANGE); deferred while a dialog is open.
- UI: Settings > "Show history" opens the operation history table (`HistoryWindow`, `OperationHistory` parser); screenshot aid `--dialog history`.
- CLI: `wpm list --json` (schemaVersion 1, sizes in bytes, camelCase; `DiskJson`).
- Virtual disks: create/attach/detach (`IVirtualDiskOperations`, `VirtualDiskPlanner`, `Disk.VirtualDiskPath`).
- CLI: `wpm vhd attach <file>`; `vhd new` now attaches with drive letters; `vhd detach` finds the disk by its file.
- UI: toolbar "Virtual disk" menu (create/attach) with `CreateVirtualDiskWindow`; "Detach virtual disk…" in the Disk menu; screenshot aid `--dialog vhd`.
- Disk health: SMART/reliability counters (`DiskHealth`, `DiskHealthAssessment`, `IDiskHealthProvider`); verdict Good/Attention/Bad/Unknown, cautious wording.
- CLI: `wpm health [<disk>]`.
- UI: "Health…" in every disk's Disk menu (Windows disk included) opens `DiskHealthWindow`; screenshot aid `--dialog health`.
- Check/repair file system: Scan, SpotFix, OfflineScanAndFix (`CheckPlanner`, `CheckVolumeRequest`, `CheckResult` mapping Repair-Volume status codes); Windows volume: scan only, fix via chkdsk at restart.
- CLI: `wpm check <disk> <partition> [--spotfix | --fix]`.
- UI: "Check file system…" row action (`CheckVolumeWindow`): scan without confirmation, fixes with one, the next step is preselected; on the Windows drive a hint explains chkdsk at restart. Screenshot aid `--dialog check`.
- Optimize: TRIM and defragment (`OptimizePlanner`, `OptimizeVolumeRequest`); recommended mode by media type; defrag on SSD and TRIM on HDD are explained.
- CLI: `wpm optimize <disk> <partition> [--trim | --defrag]` (default: what suits the media type).
- UI: "Optimize…" row action (`OptimizeVolumeWindow`): the media type is read first so the right mode is preselected and marked recommended; runs without confirmation. Screenshot aid `--dialog optimize`.
- BitLocker: `Volume.BitLocker` (`BitLockerStatus`, `EncryptionState`), `SafetyChecks.Encryption` with `EncryptionUse`; locked volumes refused for resize/check/optimize/rename, warned before format/delete; resize waits while converting; recovery-key reminder before resizing an encrypted volume.
- CLI: `wpm list` shows `[BitLocker: on | encrypted, protection off | locked | encrypting N %]`.
- UI: lock icon next to encrypted partitions with a tooltip (on / protection off / locked / converting); same text in the disk map tooltip. BitLocker is only read when elevated.
- Convert empty disk MBR <-> GPT (`ConvertDiskPlanner`, `ConvertDiskRequest`); only disks without data partitions (the Microsoft Reserved partition does not count); Windows disk refused (MBR2GPT hint).
- CLI: `wpm convert <disk> (gpt | mbr)`.
- UI: "Convert to GPT…/MBR…" in the Disk menu of an empty disk.
- README: link CHANGELOG.md; mention version 0.2.0.
- Space analysis: `SpaceAnalyzer` builds a folder tree from the MFT listing (`SpaceEntry`, `SpaceFolder`, `SpaceReport`); 50 largest files per folder and 100 overall are kept by name.
