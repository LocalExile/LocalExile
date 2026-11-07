LocalExile: a local server for the Path of Exile 3.29.3b client. You play on your own PC against servers on 127.0.0.1.
Not affiliated with or endorsed by Grinding Gear Games. This package holds no game files.

You need Windows 10/11 x64 and your own copy of the standalone Path of Exile 3.29.3b client. Nothing else to install.

TO PLAY
Double-click LocalExile.exe, or run it in a terminal, and answer its questions. The first start copies the client if you
choose that, extracts the game data (several minutes; each step is reported) and has you create a local account.
Later starts take about a minute. In game, set Options > Game > Network Mode to Predictive once.

ALWAYS START THE GAME WITH LocalExile.exe
Started any other way (the game's own executable, Steam, the official launcher), the client contacts the official patch
servers and can update itself. A newer client does not work with LocalExile, and you cannot get 3.29.3b back that way.

TO UPDATE LOCALEXILE
Close the game, then extract the new zip to the same place, so its LocalExile folder goes over this one. Your characters, keys and extracted data in data\ and your
client in Client\ are kept. Only LocalExile is updated, never the game. LocalExile.exe tells you when a newer release
exists; add --no-update-check to turn that off. It never downloads anything by itself.

IF SOMETHING GOES WRONG
Logs are in logs\. An antivirus may flag Client\Game.patched.exe, a modified copy of the client: allow it if you trust
this package. LocalExile.exe --help lists every option.

LICENSE
AGPL-3.0. See licenses\: the source of this exact build is named in licenses\SOURCE.txt.
