# Sources for supported-games.json

Entries were verified online on 2026-11-09: haptics/adaptiveTriggers flags against the PCGamingWiki DualSense lists (read via Internet Archive snapshots, as pcgamingwiki.com itself is behind a Cloudflare challenge), and every steamAppId against the Steam store API.

- https://web.archive.org/web/2026/https://www.pcgamingwiki.com/wiki/List_of_games_that_support_Dualsense_haptic_feedback
- https://web.archive.org/web/2026/https://www.pcgamingwiki.com/wiki/List_of_games_that_support_PlayStation_adaptive_triggers
- https://web.archive.org/web/2025/https://www.pcgamingwiki.com/wiki/Controller:DualSense
- https://store.steampowered.com/api/appdetails?appids=<id> (app id / name check for every entry)
- https://www.gamereactor.eu/chatting-with-team-ninja-about-nioh-2-the-complete-edition/ (Nioh 2 developer claim; not on PCGW lists, so excluded)

Not verified: executable names. No exe was confirmed against a source (PCGW game pages were unreachable), so entries are marked exeConfidence "high" only after their exe names were confirmed on a running install (so far: Lego Batman: Legacy of the Dark Knight); treat all other exe names as best-effort. Games without exe names are still recognised by their Steam library folder or by their full name (folder, product name or window title).

<!-- gamelist:start -->
## Last automatic refresh (tools/Kopru.GameList)

2026-11-09: read 223 games via PCGamingWiki MediaWiki API (action=parse of the two list pages); flags follow the PCGamingWiki lists (true, limited and hackable all count as listed).
Added 149, removed 0, flags changed 8. New entries have no exe name yet ("Added from PCGamingWiki; exe name unknown"); fill them in by hand.

- https://www.pcgamingwiki.com/wiki/List_of_games_that_support_Dualsense_haptic_feedback
- https://www.pcgamingwiki.com/wiki/List_of_games_that_support_PlayStation_adaptive_triggers
<!-- gamelist:end -->
