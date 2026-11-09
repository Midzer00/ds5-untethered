# Sources for supported-games.json

Entries were verified online on 2026-11-09: haptics/adaptiveTriggers flags against the PCGamingWiki DualSense lists (read via Internet Archive snapshots, as pcgamingwiki.com itself is behind a Cloudflare challenge), and every steamAppId against the Steam store API.

- https://web.archive.org/web/2026/https://www.pcgamingwiki.com/wiki/List_of_games_that_support_Dualsense_haptic_feedback
- https://web.archive.org/web/2026/https://www.pcgamingwiki.com/wiki/List_of_games_that_support_PlayStation_adaptive_triggers
- https://web.archive.org/web/2025/https://www.pcgamingwiki.com/wiki/Controller:DualSense
- https://store.steampowered.com/api/appdetails?appids=<id> (app id / name check for every entry)
- https://www.gamereactor.eu/chatting-with-team-ninja-about-nioh-2-the-complete-edition/ (Nioh 2 developer claim; not on PCGW lists, so excluded)

Not verified: executable names. No exe was confirmed against a source (PCGW game pages were unreachable), so no entry is marked exeConfidence "high"; treat all exe names as best-effort and confirm on real installs.
