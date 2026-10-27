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
