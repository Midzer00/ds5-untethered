# Kopru.DetectStudy

A measurement harness for game detection. It drives the real `GameWatcher.Recognize` / `GameCatalog` /
`SteamLibrary` / `InstalledGameFinder` code with described processes; it never launches a game or the app.

```
dotnet run --project tools/Kopru.DetectStudy -- <out-dir> [fetch|audit|recall|propose|local|fp|all] [--offline] [--cache=<dir>]
```

`--cache=<dir>` reads (and fills) the metadata cache in another folder than `<out-dir>\cache`, so several result
folders can share one download.

Keep `<out-dir>` outside the repository: the `local` and `fp` results name programs installed on the PC that ran them.

| command | writes | what it does |
|---|---|---|
| `fetch` | `cache\` | Downloads public Steam metadata for every catalog game: `api.steamcmd.net/v1/info/<appid>` (install folder, launch exes) and, for entries without an app id, the store search API (name -> app id, exact normalised name only). Sequential, 1.2 s apart; sends only game names and app ids. Cached, so later runs need no network (`--offline` forbids it). |
| `audit` | `audit.md` | Part A: static catalog audit (exe/no-exe counts, names below `MinNameLength`, name containment, generic names, shared exe names, duplicate app ids). |
| `recall` | `recall.md`, `recall.csv` | Part B: each game's Steam launch exe simulated (v1) inside an in-memory Steam library, (v2) at `D:\Games\<installdir>\...` with folder and exe name only, (v2t) v2 plus the Steam name as window title; plus two what-if catalogs. Misses are grouped by cause. |
| `propose` | `propose.md`, `supported-games.proposed.json` | Not part of `all`. Proposes catalog exe names from Steam's launch options (public branch only; no launcher or anti-cheat stubs, installers, extras, internal builds, engine-wide or generic names, names shorter than 3 characters, names launched by more than one Steam app or proposed for two entries, nothing in `GameWatcher.Ignored`, no exe named like the older game a "(year)" entry is told apart from) and Steam install folder names that differ from the game's name (not a plain single word, not a common folder, not a game's name, not a "(year)" entry's year-less name). Writes the embedded catalog with them added, same order and format; copy it over `src/Kopru.Core/Games/supported-games.json` after review. |
| `local` | `local.md` | Part C: games installed on this PC (Steam libraries and `InstalledGameFinder`), their real exe files and version info read from disk, at their real path and as a non-Steam copy. |
| `fp` | `fp.md` | Part D: exe files under Program Files, Program Files (x86) and `%LOCALAPPDATA%\Programs` (game folders left out), uninstall entries and running processes through the same lookup. |

The harness redirects `AppLog` into `<out-dir>\applog` so library code does not write into the app's own log.
