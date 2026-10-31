# Third-party notices

LocalExile is licensed under AGPL-3.0 ([LICENSE](LICENSE)). A release also holds the programs and libraries below, each
under its own license. Their license texts are in the release's `licenses/` folder or in the package named.

| Component | Used for | License |
|---|---|---|
| .NET runtime and ASP.NET Core | runs the servers and tools | MIT |
| Microsoft.CodeAnalysis (Roslyn) | compiles leagues and mod scripts | MIT |
| Microsoft.Data.Sqlite, SQLitePCLRaw | the save database | MIT, Apache-2.0 |
| SQLite | the save database | public domain |
| LibGGPK3 | reads `Content.ggpk` | AGPL-3.0-or-later |
| OozSharp | decompresses game bundles | MIT |
| Python (embeddable package) | runs the data generators | PSF-2.0, `licenses/python-LICENSE.txt` |
| Node.js | runs ooz-wasm | MIT and others, `licenses/node-LICENSE.txt` |
| ooz-wasm | decompresses game bundles | GPL-3.0-or-later, `licenses/ooz-wasm-LICENSE.txt` |
| dat-schema | the game tables' column layouts | MIT, `licenses/dat-schema-LICENSE.txt` |

LocalExile is not affiliated with or endorsed by Grinding Gear Games. A release holds no game files: everything the
servers read from the game is extracted on the player's PC from the player's own client.
