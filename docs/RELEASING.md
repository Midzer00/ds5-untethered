# Releasing

## Build

1. Set `<Version>` in `Directory.Build.props`.
2. Run the tests: `powershell -ExecutionPolicy Bypass -File tools\test.ps1` (builds once, runs Core and the App tests split
   over parallel `dotnet test` processes, then the real-time Core tests alone; about a minute and a half). Every line
   must say `Başarılı!`, the totals `Core toplam: Başarısız 0, ...` and `App toplam: Başarısız 0, ...`, exit code 0.
   Plain `dotnet test --blame-hang --blame-hang-timeout 180s` runs the same tests in about seven minutes.
3. Optional: write the release notes the app shows (tooltip of the Updates row in Settings > General) to
   `tools/release/notes.json`, a few short plain lines per language:
   `{ "tr": "Kumanda hoparlörü daha temiz.", "en": "Cleaner controller speaker." }`. Without the file the notes are empty.
4. Run `powershell -ExecutionPolicy Bypass -File tools\publish.ps1` (Inno Setup 6 installed). It writes to `artifacts\`:
   `DS5-Untethered-<version>-Setup.exe`, `DS5-Untethered-<version>-win-x64-Portable.zip`, `DS5-Untethered-<version>-Update.exe`
   (a byte-identical copy of the Setup.exe, see "Download counts" below) and `update.json` (the update manifest for this
   release: version, the `v<version>` download URLs of the Update.exe and the zip, their sizes and SHA-256, the release
   page, the notes). `tools\release\make-update-json.ps1` makes the manifest and can be run on its own.

## Steam PlayStation support list

`src/Kopru.Core/Games/playstation-support.json` (embedded) tells the controller type "Automatic" which Steam games list
"DualSense Controller Support" (Steam categories 57/58: DualSense) or only "DualShock Controller Support" (55/56:
DualShock 4); any other known game gets Xbox 360. The app never looks this up online. Refresh it now and then (a few
times a year is enough), from the repository root:

```
dotnet run --project tools/Kopru.DetectStudy -- <out-dir outside the repo> playstation --out=src/Kopru.Core/Games/playstation-support.json
```

It pages through the public Steam store search (games only, one category at a time, 100 per page, 1.2 s apart, about
20 minutes) and writes the file only when every category came back nearly whole; otherwise it stops with a message and
leaves the file alone. Pages are cached per day under `<out-dir>\cache\playstation\<date>\`, so an interrupted run
resumes. Commit the new file with its `generated` date. Installed Steam games the list does not know are also looked up
in Steam's own cache on the PC (`appcache\appinfo.vdf`, read only).

## Xbox trigger rumble game list

`src/Kopru.Core/Games/xbox-trigger-games.json` (CC BY-NC-SA 3.0, see THIRD-PARTY-NOTICES.md) lists the games Automatic gives the Xbox Series pad (while the experimental Xbox Trigger Rumble setting is on). To refresh it, take the "Native support" entries of PCGamingWiki's "List of games that support Xbox One impulse triggers" page (name, Steam app id or null, exe names when known, the game's page as `source`), keep the schema and set `generated`; keep it a separate file under that licence.

## Publish (in this order)

1. Create the GitHub release with the tag `v<version>` and upload all three files (Setup.exe, portable zip, Update.exe), under
   exactly these names. In the release notes link only the Setup.exe and the portable zip, and add a line such as:
   "`DS5-Untethered-<version>-Update.exe` is used by in-app updates; download the Setup.exe or the portable zip instead."
2. Check that all three download links work.
3. Only then copy `artifacts\update.json` over `update.json` at the repository root, commit and push it to `main`.

The app reads `https://raw.githubusercontent.com/Midzer00/ds5-untethered/main/update.json` about 20 s after it starts
and then once a week (or at once with "Check Now"), so the manifest must never point at files that are not there yet.
If it does anyway, the app says the new version is not ready yet and the user can try again later; nothing is
installed. The app accepts installers only from `https://github.com/Midzer00/ds5-untethered/releases/download/`, only
with the exact size and SHA-256 from the manifest. Do not change an uploaded file after the manifest is live: its hash
would no longer match and every download would be rejected.

`update.json` is not part of the app build. Optional field: `"minimumVersion": "0.1.6"` makes older copies open the
release page instead of updating in place (for a release that needs a manual install).

## Download counts

GitHub counts downloads per release asset. The README and the release notes point people at the Setup.exe (and the
zip), while `update.json` points the in-app updater at the Update.exe, the same installer under another name. So per
release: Setup.exe + zip = new downloads (people installing or reinstalling by hand), Update.exe = installed copies
that updated in place. The app sends nothing anywhere; the counts are only GitHub's own. Read them with:

```
gh api repos/Midzer00/ds5-untethered/releases --paginate --jq '.[] | .tag_name as $t | .assets[] | "\($t)\t\(.name)\t\(.download_count)"'
```

Releases up to 0.1.8 have no Update.exe (their manifest points at the Setup.exe), so their Setup.exe counts include
in-app updates. Copies from 0.1.6 on accept the Update.exe URL as they are: the updater only requires the URL to be
under `.../releases/download/` and the size and SHA-256 to match; it saves the file under its own name
(`DS5-Untethered-<version>-Setup.exe`) whatever the asset is called. A counted download is any request for the file,
including a browser click on the release page, so the split is close, not exact.

`UpdateAssetTests` (Core tests) runs `make-update-json.ps1` on a stand-in Setup.exe and its Update.exe copy, then
rehearses an update from that manifest against a loopback server with a `/releases/download/` path: check, download,
size and SHA-256, without starting any installer.

## How an installed copy updates

On "Update" the app downloads the installer the manifest names (the Update.exe; up to 0.1.8 the Setup.exe) to
`%LOCALAPPDATA%\Untethered\updates\DS5-Untethered-<version>-Setup.exe`, checks size and SHA-256 again, stops the virtual
controller the way Exit does, swaps its single-instance mutex `DS5Untethered.Running` for `DS5Untethered.Updating`
(held until the process has ended), starts
`Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /NOCANCEL /UPDATE=1 /LANG=<tr|en> /LOG=<updates\install.log>`
and exits. With `/UPDATE=1` the installer waits for `DS5Untethered.Updating` to disappear before it replaces files and
starts the new version when it is done (`tools/installer/DS5Untethered.iss`). Zip copies (no uninstall entry for their
folder) open the release page instead. The first start of a new version copies `settings.json` to
`settings.json.<previous version>.bak` (the newest three are kept).
