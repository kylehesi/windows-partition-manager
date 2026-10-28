# Changelog

All notable changes to Windows Partition Manager. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- **What uses the space**: a row action on NTFS volumes reads the master file table and shows
  folders and files largest first, with a share bar, drill-down, Open in Explorer and the
  100 largest files of the drive. About 3 seconds for 700,000 files. CLI: `wpm space`.
- **Pending operations queue**: plan several creates, deletes, resizes and formats with
  "Add to queue", see the resulting layout before anything is written, then apply them with one
  confirmation. Before every step the disk is read again and compared with the plan; the first
  difference or failure stops the run and leaves the rest queued.

### Fixed

- The shrink analysis lost small folders (those whose index fits in their MFT record), so some
  reported paths showed `<record number>` instead of their folders.
- The format progress line and a few read-back errors were still in English in Turkish mode.

## [0.2.0] - 2026-10-27

Every change below still goes through the Windows Storage Management API, is checked against a
fresh read of the disk before anything is written, and is recorded in the operation log.

### Added

- **Drive letter and label**: change or remove a partition's drive letter and rename its volume
  from the row's Actions menu. The Windows drive keeps its letter. CLI: `wpm letter`, `wpm label`.
- **Disk menu** in every disk card:
  - take a disk offline or bring it back, and switch its read-only attribute (`wpm disk`);
  - convert an empty disk between MBR and GPT; only disks without data partitions, never the
    Windows disk (`wpm convert`);
  - detach an attached virtual disk.
- **Disk health**: what the drive reports about itself (SMART): media type, Windows' health
  status, temperature, SSD wear, power-on hours and error counts, with a plain verdict:
  looks healthy, needs attention, back up now. CLI: `wpm health`.
- **Check file system**: online scan, quick fix and the full offline check and repair
  (chkdsk). The Windows drive can be scanned; for a repair the window explains chkdsk at the
  next restart. CLI: `wpm check`.
- **Optimize**: TRIM for SSDs and defragmentation for hard disks, with the right one
  recommended for the drive. CLI: `wpm optimize`.
- **BitLocker**: a lock next to encrypted partitions shows whether protection is on or off,
  the volume is locked, or BitLocker is still converting it.
- **Virtual disks**: create a VHDX (file, size, partition table) or attach an existing VHD or
  VHDX from the new Virtual disk toolbar menu; attached disks get drive letters.
  CLI: `wpm vhd attach`.
- **Operation history**: Settings > Show history lists every change from the operation log,
  newest first, failures in red.
- **Automatic refresh** when a drive is plugged in or removed.
- **`wpm list --json`** for scripts: disks, partitions, volumes, BitLocker and free regions,
  sizes in bytes, with a `schemaVersion`.
- **Shrink and create in one step** in the Resize dialog.
- **Themes and fonts**: White, Dark and Night colour schemes and a choice of UI font in
  Settings.
- **Turkish**: an English/Turkish language setting for the app and the CLI. Turkish covers the
  whole desktop app, including validation and safety messages, progress, Windows' error
  descriptions and the shrink report; the CLI output stays English. English remains the
  default.

### Changed

- Settings and every dialog follow the chosen theme, including drop-down lists, group boxes,
  menus and tooltips.
- The toolbar keeps every button visible with longer translations; the selection hint wraps
  instead.

### Safety

- A volume locked by BitLocker is never resized, checked, optimized or renamed; formatting or
  deleting it warns that its contents are lost for good. A volume BitLocker is still encrypting
  is not resized, and resizing an encrypted volume reminds you to keep the recovery key at hand.
- Renaming is offered only for data partitions, never for Recovery or EFI partitions.

### Fixed

- Merge conflict markers left in 31 files on the development branch.
- The drive letter in the new-partition summary was printed with a doubled colon ("E::").

## [0.1.0] - 2026-10-07

First release: read disks, partitions, volumes and free space; disk map and partition table;
NTFS shrink analysis with the unblock wizard (hibernation, page file, restore points); create,
format, delete, shrink and extend partitions; initialize disks; USB flash drives and external
SSDs; layout fingerprints and re-validation before every write; MSI installer and portable
build.

[Unreleased]: https://github.com/kylehesi/windows-partition-manager/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/kylehesi/windows-partition-manager/compare/0029b27...v0.2.0
[0.1.0]: https://github.com/kylehesi/windows-partition-manager/commit/0029b27
