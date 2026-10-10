# Windows Partition Manager — notes for Claude Code

Open-source Windows disk partition manager. C# / .NET 10 / WPF. README.md is the user-facing
documentation, docs/ARCHITECTURE.md the internals. When behaviour changes, update both.

## Toolchain on this machine

The .NET SDK is a user-local install and is **not on PATH** by default. Prefix commands with:

```powershell
$env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;C:\Program Files\Git\cmd;$env:PATH"
```

Then `dotnet build`, `dotnet test`, `dotnet run --project src/WindowsPartitionManager.Cli`.

The WPF app's manifest demands administrator rights (UAC prompt). To run it unelevated for
read-only checks, build it with the dev manifest:

```powershell
dotnet build src/WindowsPartitionManager.App -p:DevManifest=true
```

That build lands in `src/WindowsPartitionManager.App/bin/Debug/dev/WindowsPartitionManager.exe`. Never launch the regular
`bin/Debug/net10.0-windows/WindowsPartitionManager.exe` from an automated shell: it shows a UAC prompt that blocks
until a human answers it. The same applies to `wpm analyze`, which needs an elevated prompt.

To check the layout without asking the user for a screenshot, run the dev build with
`--screenshot <file.png> [--size 1020x700]`: it loads the disks, renders the main window to PNG
and exits. 1020 px is the window's minimum width; the table must fit there.

Integration tests (`tests/WindowsPartitionManager.Integration.Tests`) need an elevated test host; they skip
otherwise. Run them through `Start-Process cmd -Verb RunAs` with output redirected to a file in
the scratchpad, and tell the user a UAC prompt is coming. They create and delete a temporary
VHDX in %TEMP%; they never touch real disks.

**VHDX lesson (2026-10-07):** the development machine bugchecked twice (0x1E, then 0xA in
win32kbase/KiInsertTimerTable) seconds after a test detached a VHDX with mounted, freshly
written volumes and immediately attached the next one. `VirtualDisk.Detach()` therefore takes
the disk offline first (flush + dismount), waits until it is gone and lets device-removal
notifications settle. `DetachWithoutDismount()` (surprise removal) is only for the opt-in
`[SurpriseRemovalFact]` test (`WPM_RUN_SURPRISE_REMOVAL=1`). Every test step is traced
write-through to `%LOCALAPPDATA%\WindowsPartitionManager\test-trace.log`; read it first after a crash.

## Rules

- **Windows Partition Manager never writes raw sectors.** Every change to a disk goes through the Windows Storage
  Management API (`WmiStorageOperations`), so NTFS journaling and GPT redundancy keep a power
  loss recoverable. The only planned exception, offline NTFS metadata relocation, must bring
  its own journal/rollback design and is not to be started casually.
- Every write operation runs through `Guarded(...)`: sleep inhibited, outcome appended to
  `%LOCALAPPDATA%\WindowsPartitionManager\operations.log`. Keep it that way for new operations.
- Pre-flight checks live in `SafetyChecks` (power source, volume health/dirty bit). Planners
  and the operations layer both call them; the UI and CLI surface them as issues.

- `WindowsPartitionManager.Core` must stay free of Windows-specific dependencies; all OS access goes through
  `WindowsPartitionManager.Platform.Windows` behind interfaces in `WindowsPartitionManager.Core.Abstractions`.
- Anything that writes to a disk needs: a planner step in Core, validation, a confirmation in
  the UI, and tests against a VHDX, never against the developer's real disks.
- Sizes are `ulong` bytes in the model; format only at the UI edge with `ByteSize.Format`.
- Keep the UI dependency-free until there is a concrete need for an MVVM or theming library.
- The UI and all code stay in English (user requirement); no Turkish strings or identifiers.
- How to add a language setting: see `docs/ADDING_A_LANGUAGE.md`.
- Partition colours live in `PartitionColors`; the disk map and the table swatches must share them.

## Packaging

`powershell -ExecutionPolicy Bypass -File build\package.ps1` builds `artifacts\` (MSI, portable
zip, SHA256SUMS, Defender scan) and copies the installer to the repository root as
`WindowsPartitionManager-Setup.msi` (the user wants it there, tracked in git). WiX 5.0.2 is a
global dotnet tool (`%USERPROFILE%\.dotnet\tools`), with `WixToolset.UI.wixext/5.0.2` and
`WixToolset.Util.wixext/5.0.2`. The icon is drawn by `assets/make-icon.ps1` (a disk snapped in
two like a cookie, with crumbs); regenerate it there rather than editing `app.ico`. Keep the anti-false-positive choices documented in
docs/ARCHITECTURE.md (no compression, no self-extracting native DLLs, full version info). The MSI
`UpgradeCode` in installer/Package.wxs must never change; bump `<Version>` in
Directory.Build.props for each release.
