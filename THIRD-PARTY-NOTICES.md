# Third-party notices

SPDX-License-Identifier: GPL-3.0-only

Winora is licensed under GPL-3.0-only. The full text is in [LICENSE](LICENSE).

Winora needs the .NET 10 Desktop Runtime, which you install from Microsoft under Microsoft's terms. It is not included.

## Packages used by Winora

"Direct" means the app references the package itself. The others come with those packages.

| Package | Version | Direct | License | License URL |
| --- | --- | --- | --- | --- |
| CommunityToolkit.Mvvm | 8.4.2 | yes | MIT | https://licenses.nuget.org/MIT |
| Microsoft.Data.Sqlite | 10.0.12 | no | MIT | https://licenses.nuget.org/MIT |
| Microsoft.Data.Sqlite.Core | 10.0.12 | no | MIT | https://licenses.nuget.org/MIT |
| Microsoft.Extensions.DependencyInjection | 10.0.12 | yes | MIT | https://licenses.nuget.org/MIT |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.12 | no | MIT | https://licenses.nuget.org/MIT |
| Microsoft.Extensions.Logging | 10.0.12 | yes | MIT | https://licenses.nuget.org/MIT |
| Microsoft.Extensions.Logging.Abstractions | 10.0.12 | no | MIT | https://licenses.nuget.org/MIT |
| Microsoft.Extensions.Options | 10.0.12 | no | MIT | https://licenses.nuget.org/MIT |
| Microsoft.Extensions.Primitives | 10.0.12 | no | MIT | https://licenses.nuget.org/MIT |
| Microsoft.NET.ILLink.Tasks | 10.0.12 | yes | MIT | https://licenses.nuget.org/MIT |
| SQLitePCLRaw.bundle_e_sqlite3 | 2.1.12 | no | Apache-2.0 | https://licenses.nuget.org/Apache-2.0 |
| SQLitePCLRaw.core | 2.1.12 | no | Apache-2.0 | https://licenses.nuget.org/Apache-2.0 |
| SQLitePCLRaw.lib.e_sqlite3 | 2.1.12 | no | Apache-2.0 | https://licenses.nuget.org/Apache-2.0 |
| SQLitePCLRaw.provider.e_sqlite3 | 2.1.12 | no | Apache-2.0 | https://licenses.nuget.org/Apache-2.0 |
| System.Formats.Cbor | 10.0.12 | no | MIT | https://licenses.nuget.org/MIT |
| System.Security.Cryptography.Cose | 10.0.12 | no | MIT | https://licenses.nuget.org/MIT |
| WPF-UI | 4.3.0 | yes | MIT | https://licenses.nuget.org/MIT |
| WPF-UI.Abstractions | 4.3.0 | no | MIT | https://licenses.nuget.org/MIT |

## Other notices

- Each release includes a `third-party-licenses` folder with the license and notice files that these packages ship.
- SQLite, used through SQLitePCLRaw, is in the public domain.
- WPF-UI includes content from other projects; its own notice file lists them and ships with each release.
- The MSI installer is built with the WiX Toolset. No WiX code is part of Winora.
