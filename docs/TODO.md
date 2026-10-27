# Documentation to update

Features are landing faster than the docs; README.md and docs/ARCHITECTURE.md are brought up to
date in one pass before the release. Everything below still needs to be written up.

- Drive letter: change or remove a partition's letter (`DriveLetterPlanner`, `ChangeDriveLetterRequest`).
- Volume label: rename a volume (`LabelPlanner`, `SetLabelRequest`).
- CLI: `wpm letter <disk> <partition> (<X> | --remove)` and `wpm label <disk> <partition> <text>`.
- UI: "Drive letter and label…" row action and dialog (`VolumePropertiesWindow`); the Windows drive keeps its letter.
- Screenshot aid: `--dialog properties`.
