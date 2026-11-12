# Releasing

## Build

1. Set `<Version>` in `Directory.Build.props`.
2. Optional: write the release notes the app shows (tooltip of the Updates row in Settings > General) to
   `tools/release/notes.json`, a few short plain lines per language:
   `{ "tr": "Kumanda hoparlörü daha temiz.", "en": "Cleaner controller speaker." }`. Without the file the notes are empty.
3. Run `powershell -ExecutionPolicy Bypass -File tools\publish.ps1` (Inno Setup 6 installed). It writes to `artifacts\`:
   `DS5-Untethered-<version>-Setup.exe`, `DS5-Untethered-<version>-win-x64.zip` and `update.json` (the update manifest
   for this release: version, the `v<version>` download URLs, sizes and SHA-256 of both files, the release page, the
   notes). `tools\release\make-update-json.ps1` makes the manifest and can be run on its own.

## Publish (in this order)

1. Create the GitHub release with the tag `v<version>` and upload both files, under exactly these names.
2. Check that both download links work.
3. Only then copy `artifacts\update.json` over `update.json` at the repository root, commit and push it to `main`.

The app reads `https://raw.githubusercontent.com/Midzer00/ds5-untethered/main/update.json` about 20 s after it starts
and then once a week (or at once with "Check Now"), so the manifest must never point at files that are not there yet.
If it does anyway, the app says the new version is not ready yet and the user can try again later; nothing is
installed. The app accepts installers only from `https://github.com/Midzer00/ds5-untethered/releases/download/`, only
with the exact size and SHA-256 from the manifest. Do not change an uploaded file after the manifest is live: its hash
would no longer match and every download would be rejected.

`update.json` is not part of the app build. Optional field: `"minimumVersion": "0.1.6"` makes older copies open the
release page instead of updating in place (for a release that needs a manual install).

## How an installed copy updates

On "Update" the app downloads the Setup.exe to `%LOCALAPPDATA%\Untethered\updates\`, checks size and SHA-256 again,
stops the virtual controller the way Exit does, swaps its single-instance mutex `DS5Untethered.Running` for
`DS5Untethered.Updating` (held until the process has ended), starts
`Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /NOCANCEL /UPDATE=1 /LANG=<tr|en> /LOG=<updates\install.log>`
and exits. With `/UPDATE=1` the installer waits for `DS5Untethered.Updating` to disappear before it replaces files and
starts the new version when it is done (`tools/installer/DS5Untethered.iss`). Zip copies (no uninstall entry for their
folder) open the release page instead. The first start of a new version copies `settings.json` to
`settings.json.<previous version>.bak` (the newest three are kept).
