# Windows Partition Manager

**Windows Partition Manager** is a free, open-source partitioning tool for Windows 10 and 11.
It exists because the built-in Disk Management tool often refuses to shrink a volume or to turn
the freed space into a new drive without saying why, and because the third-party alternatives
are either abandoned or paid.

> **Status: early development.** The program reads and displays your disks, explains why a
> volume will not shrink, shrinks and extends NTFS/ReFS partitions, creates, formats and deletes
> partitions, and works on internal disks, external SSDs and USB flash drives. Every write path
> is exercised on virtual disks by the integration tests, including a rehearsal on a copy of a
> real disk layout. See the roadmap below.

## Installation

Windows Partition Manager runs on 64-bit Windows 10 and 11. The setup and the portable build
include the .NET runtime, so nothing else needs to be installed. The program asks for
administrator approval when it starts, because every change to a partition needs it.

Before you start: the program is careful, but changing partitions always carries some risk, so
back up your data first. Installing or using the program means you accept the
[disclaimer](#disclaimer) and the [MIT license](LICENSE); the setup asks you to accept them.

There are three ways to run it.

### Option 1: Setup (recommended)

The installer is **`WindowsPartitionManager-Setup.msi`** in the root folder of this repository.

1. Double-click `WindowsPartitionManager-Setup.msi`.
2. Click **Next**, read and accept the license agreement (the disclaimer below and the MIT
   license), keep or change the install folder, click **Install** and approve the Windows
   administrator prompt.
3. On the last page leave **Launch Windows Partition Manager** ticked and click **Finish**.

The setup installs the program into `C:\Program Files\Windows Partition Manager`, puts a
**Windows Partition Manager** shortcut on the desktop and in the Start menu, and adds the `wpm`
command-line tool to the system PATH. Installing a newer setup over an older one upgrades it in
place. To remove the program, open **Settings > Apps > Installed apps**, find **Windows
Partition Manager** and choose **Uninstall**; the shortcuts and the PATH entry are removed too.

The setup can also run without its wizard, from a PowerShell or Command Prompt window opened
with **Run as administrator**:

```powershell
msiexec /i WindowsPartitionManager-Setup.msi /qn    # install silently
msiexec /x WindowsPartitionManager-Setup.msi /qn    # uninstall silently
```

### Option 2: Portable, started from PowerShell or Command Prompt

The portable build, `WindowsPartitionManager-<version>-portable-x64.zip`, is produced by the
packaging script (see [Building the packages](#building-the-packages)) and attached to releases.
It needs no installation and leaves nothing behind except its log in
`%LOCALAPPDATA%\WindowsPartitionManager`. It contains `DISCLAIMER.txt` and `LICENSE.txt`; using
it means you accept them.

PowerShell:

```powershell
Expand-Archive .\WindowsPartitionManager-0.1.0-portable-x64.zip -DestinationPath C:\Tools\WindowsPartitionManager
cd C:\Tools\WindowsPartitionManager
.\WindowsPartitionManager.exe     # the desktop app
.\wpm.exe list                    # the command-line tool
```

Command Prompt:

```bat
cd /d C:\Tools\WindowsPartitionManager
start WindowsPartitionManager.exe
wpm list
```

### Option 3: From the source code

With the [.NET 10 SDK](https://dotnet.microsoft.com/download) installed, from the repository
folder:

```powershell
dotnet run --project src\WindowsPartitionManager.App -c Release              # the desktop app
dotnet run --project src\WindowsPartitionManager.Cli -c Release -- list      # the command-line tool
```

### Using the command line

After the setup, `wpm` works in any **new** PowerShell or Command Prompt window. Listing disks
works in a normal window; anything that analyses or changes a disk needs a window opened with
**Run as administrator**. Write commands print a plan, refuse on errors and ask you to type a
confirmation unless `--yes` is given.

```powershell
wpm list                                    # disks, partitions and free space
wpm analyze C: --target 300GB               # why C: will not shrink to 300 GB
wpm resize 0 1 --shrink-by 500GB            # shrink partition 1 of disk 0 by 500 GB
wpm create 0 --max --fs NTFS --label Data   # new drive in the largest free space of disk 0
wpm unblock C: --all                        # remove what blocks the shrink; "wpm unblock --restore" undoes it
```

Other commands: `format`, `delete`, `layout`, and `vhd new` / `vhd detach` for practising on a
virtual disk. `wpm help` lists everything.

### Practise on a virtual disk first

```powershell
wpm vhd new C:\Temp\practice.vhdx --size 8GB
```

creates a virtual disk that appears in the app like a real one; `wpm vhd detach
C:\Temp\practice.vhdx` removes it again.

### Verifying a download

`artifacts\SHA256SUMS.txt` lists the SHA-256 checksum of each package:

```powershell
certutil -hashfile WindowsPartitionManager-Setup.msi SHA256
```

### "Windows protected your PC"

The release is not code-signed yet, so Microsoft Defender SmartScreen may show this screen for a
downloaded copy. It is a reputation warning about an unknown publisher, not a virus detection:
choose **More info**, then **Run anyway**. Each release is built so as not to look like malware:
no packers or obfuscation, no executables that unpack themselves into a temporary folder, full
publisher and version information, a standard Windows Installer package, and a Microsoft
Defender scan of every package before publishing. If an antivirus product ever flags a release,
please open an issue; false positives can be reported to Microsoft at
<https://www.microsoft.com/wdsi/filesubmission>.

## Goals

- **Fast.** Native .NET desktop app, no web view, no background services.
- **Safe.** Every change is planned, validated, shown to you, checked again against the live
  disk just before writing, and logged. Destructive actions require typing the partition name.
- **Honest.** When a volume cannot be shrunk, the program tells you *which files* are in the
  way and what to do about them, instead of a vague "insufficient space" message.
- **Simple.** One window: a disk map, a partition table and a toolbar.

## What it does

| Task | How |
|---|---|
| See your disks | Disk map and partition table for every disk, with used and free space. |
| Find out why a volume will not shrink | **Analyze**: lists the unmovable files past your target (page file, hibernation file, restore points, NTFS metadata) and how far the volume can really shrink. |
| Remove what is in the way | **Unblock…** in the analysis window: turns hibernation off, moves the page file to another drive, deletes restore points, and puts the settings back afterwards. |
| Shrink or extend a partition | **Resize** (NTFS and ReFS). The freed space appears as unallocated right after the partition. |
| Turn free space into a new drive | **New partition**: size, file system (NTFS, exFAT, FAT32, ReFS), label and drive letter. |
| Reformat or remove a partition | **Format** and **Delete**, with typed confirmation. Windows' own partitions are protected. |
| Prepare a new disk | **Initialize disk** (GPT) for a disk without a partition table. |

Select a row and use the toolbar, or use the row's **Actions** menu (also on right-click).

### Appearance

The **Settings** window lets you pick one of three colour themes — **White** (light), **Dark**
and **Night** (blue) — a UI font (Segoe UI, Arial, Calibri, Consolas or Times New Roman), and a
UI language (English or Turkish). Changing any of them takes effect immediately and is remembered
on the next launch. The language sets the culture used for dates and numbers — in the app and in
the `wpm` command-line tool — but the interface strings are not yet translated. Log and analysis
output stay in a monospace font; the partition colours in the disk map and table are not changed
by the theme.

### Typical use: a new drive from the free space of C:

1. Select C: and press **Analyze**. If Windows' limit is far above your data, the report names
   the files responsible; **Unblock…** can remove them (some steps need a reboot).
2. Press **Resize**, choose the new size, confirm.
3. Select the new **Unallocated** row, press **New partition**, choose a letter, confirm.
4. If you used Unblock, open it again (or **Settings**) and press **Restore previous settings**.

### USB flash drives and external SSDs

Removable drives appear like any other disk and support the same operations. Most flash drives
come as a single exFAT or FAT32 partition, which Windows cannot shrink; **Format** it to NTFS in
place (copy your files elsewhere first), then shrink and split it like an internal disk. Every
operation on a removable disk warns you not to unplug it until it has finished.

## Why Windows cannot shrink your disk

NTFS keeps some files that Windows will not move while the volume is mounted: parts of the
Master File Table, the NTFS log, the page file, the hibernation file and System Restore
storage. Disk Management only shrinks a volume down to the last such file, so a single one near
the end of a 2 TB disk can block the whole operation. The analysis finds them by reading the
volume's file layout directly (read-only, about three seconds for 190,000 files); the page file,
hibernation file and restore points can be removed temporarily, NTFS metadata cannot.

## Safety

The program never writes raw sectors. Every change goes through the Windows Storage Management
API, the same calls as PowerShell's `New-Partition` and `Resize-Partition`, so a power loss
lands in mechanisms Windows already handles:

- **Create and delete** touch only the partition table, which GPT keeps in two CRC-protected
  copies. Existing partitions and boot files are not written.
- **Format** writes only inside the target partition.
- **Shrink and extend** are journaled by NTFS. An interruption leaves a partition that is merely
  larger than its file system; in testing, pulling a disk out mid-shrink left every file intact.

On top of that the program:

- refuses volumes that are dirty or need repair;
- refuses to resize Windows on battery, and refuses anything below 30 % charge;
- keeps the computer awake during an operation;
- re-reads the disk right before writing and refuses if what you saw is out of date (another
  tool changed it, or a USB drive was swapped);
- removes a half-created partition again if formatting it fails;
- logs every attempt to `%LOCALAPPDATA%\WindowsPartitionManager\operations.log`.

Even so, back up important data before resizing the disk Windows runs from.

## Tests

```powershell
dotnet test tests\WindowsPartitionManager.Core.Tests
dotnet test tests\WindowsPartitionManager.Integration.Tests
```

The unit tests cover the layout math, every validation rule and the shrink analysis. The
integration tests run real partition operations on throwaway virtual disks and need an
elevated prompt; they never touch a real disk. They include a rehearsal on a copy of a real
disk layout that verifies every file by hash and every untouched area byte by byte after each
step. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md#testing) for the full list.

## Building the packages

Requirements: the [.NET 10 SDK](https://dotnet.microsoft.com/download) and
[WiX Toolset 5](https://wixtoolset.org) with two extensions, installed once:

```powershell
dotnet tool install --global wix --version 5.0.2
wix extension add -g WixToolset.UI.wixext/5.0.2
wix extension add -g WixToolset.Util.wixext/5.0.2
```

Then:

```powershell
powershell -ExecutionPolicy Bypass -File build\package.ps1
```

The script runs the unit tests, builds the self-contained app and CLI, writes
`WindowsPartitionManager-Setup.msi` to the repository root and the versioned MSI, the portable
zip and `SHA256SUMS.txt` to `artifacts\`, and scans everything with Microsoft Defender. Set
`WPM_SIGN_THUMBPRINT` to the thumbprint of a code-signing certificate (and install the Windows
SDK for `signtool`) to sign the executables and the installer; open-source projects can get free
signing through the SignPath Foundation.

## Documentation

[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) describes the internals: projects and layers, how
an operation travels from a click to the disk, the safety model, the testing strategy, known
limitations and the Windows behaviours the design works around.

## Roadmap

- [x] Read disks, partitions, volumes and free regions
- [x] Disk map and partition table UI with toolbar and per-row actions
- [x] NTFS shrink analysis: unmovable files and the real minimum size
- [x] Create, format, delete partitions; initialize disks
- [x] Shrink and extend NTFS and ReFS partitions
- [x] Unblock wizard for the page file, hibernation and restore points, with restore
- [x] USB flash drives and external SSDs
- [x] Independent safety audit; stale-layout protection and re-validation before every write
- [x] Virtual-disk integration tests, including a rehearsal on a real layout
- [x] MSI installer with desktop and Start menu shortcuts, and a portable build, scanned with Microsoft Defender
- [ ] Shrink and create in one step
- [ ] Queue of pending operations with preview
- [ ] Code signing (removes the SmartScreen warning)
- [ ] Continuous integration on GitHub Actions (workflow written, not yet run)
- [ ] Offline NTFS metadata relocation for non-system volumes

## Disclaimer

In short: the program is careful, but changing partitions always carries some risk. Back up
your data first. The authors cannot take responsibility for your data.

1. Windows Partition Manager is free, open-source software, provided "as is" and "as
   available", without warranty of any kind, express or implied, including but not limited to
   the warranties of merchantability, fitness for a particular purpose and non-infringement.
2. Changing partitions is never completely free of risk. Hardware faults, power failures, other
   software, or choosing the wrong partition can lead to loss of data. The program checks every
   change before it is made and uses only the disk functions built into Windows, but it cannot
   rule these risks out.
3. You use the program at your own risk. You are responsible for backing up your data before
   creating, resizing, formatting or deleting partitions, and for checking that each change is
   the one you intend.
4. To the maximum extent permitted by applicable law, the authors and contributors of Windows
   Partition Manager shall not be liable for any loss or corruption of data, damage to hardware
   or software, loss of time, loss of profit, or any other direct, indirect, incidental, special
   or consequential damage arising from the use of, or the inability to use, the program, even
   if advised of the possibility of such damage. No claim of any kind is accepted against the
   authors or contributors on these grounds.
5. Nothing in this notice limits any liability that cannot be limited or excluded under the law
   that applies to you.

By installing, copying or using Windows Partition Manager you confirm that you have read,
understood and accepted these terms and the MIT license. If you do not accept them, do not
install or use the program. The same text ships as [DISCLAIMER.txt](DISCLAIMER.txt) and is part
of the license agreement the setup asks you to accept.

## License

MIT. See [LICENSE](LICENSE). The [disclaimer](#disclaimer) above applies in addition.
