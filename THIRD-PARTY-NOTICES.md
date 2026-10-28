# Third-party notices

Windows Partition Manager is released under the [MIT License](LICENSE). It is built on the
components below, each under its own license. Only the components marked *shipped* are part of
the installer and the portable build; the others are used to build or test it.

| Component | Use | License |
|---|---|---|
| [.NET runtime and Windows Desktop runtime (WPF)](https://github.com/dotnet/runtime) | shipped: the app and `wpm` are self-contained builds that include the runtime | [MIT](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT) |
| [System.Management](https://www.nuget.org/packages/System.Management) | shipped: WMI access to the Windows Storage Management API | [MIT](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT) |
| [WiX Toolset 5](https://github.com/wixtoolset/wix) with WixToolset.UI and WixToolset.Util | builds the MSI installer; its standard setup dialogs are part of the MSI | [MS-RL](https://github.com/wixtoolset/wix/blob/main/LICENSE.TXT) |
| [xUnit.net](https://github.com/xunit/xunit) | tests only | [Apache-2.0](https://github.com/xunit/xunit/blob/main/LICENSE) |
| [Microsoft.NET.Test.Sdk](https://github.com/microsoft/vstest) | tests only | [MIT](https://github.com/microsoft/vstest/blob/main/LICENSE) |
| [coverlet](https://github.com/coverlet-coverage/coverlet) | tests only | [MIT](https://github.com/coverlet-coverage/coverlet/blob/master/LICENSE) |

The icons in the app come from the Segoe Fluent Icons and Segoe MDL2 Assets fonts, which are part
of Windows; they are used through the system font and are not redistributed. The app icon and the
banner are original artwork, drawn by [`assets/make-icon.ps1`](assets/make-icon.ps1) and
[`assets/make-banner.ps1`](assets/make-banner.ps1), and fall under this project's MIT license.

Windows, NTFS, BitLocker and the other Microsoft product names are trademarks of Microsoft
Corporation. This project is not affiliated with or endorsed by Microsoft.
