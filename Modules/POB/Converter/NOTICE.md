The bounded PoB parser, item conversion, passive validation and native table readers were adapted from
LocalExile feature/pob-import commit 5394de0a5208bb2407491cf986fab5455714bdc8:
https://github.com/evrydayjunglist/evryPath/tree/5394de0a5208bb2407491cf986fab5455714bdc8
The tattoo catalogue, target-set and limit rules (PathOfBuildingTattoos.cs) were adapted from LocalExile main
commit ec522f1876690c0b203636bf78b508f308365163:
https://github.com/LocalExile/LocalExile/tree/ec522f1876690c0b203636bf78b508f308365163

This converter is integrated into the LocalExile POB module. It prepares builds using the server's extracted
tables and read-only catalogue; the Gateway owns the final save transaction. No Path of Building executable,
game tables, client files or .NET runtime are distributed in this source folder.

Microsoft.Data.Sqlite and its SQLite native dependencies retain their upstream license notices in packaged output.
.NET and Microsoft.Data.Sqlite: Copyright Microsoft Corporation and contributors, MIT.
SQLitePCLRaw: Copyright SourceGear, LLC, Apache License 2.0.
Native SQLite is in the public domain: https://www.sqlite.org/copyright.html
