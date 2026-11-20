# Releasing the Linux build (experimental)

The `linux` branch has no installer, no update manifest and no in-app updater. Its builds are published by CI. The
Windows app's release process (installer, update manifest, `tools/publish.ps1`) lives on `main` and does not apply here.

## Build and check

1. Set `<Version>` in `Directory.Build.props` when the version changes.
2. `dotnet build Kopru.sln` must pass with no warnings.
3. Run the tests: `dotnet test tests/Kopru.Core.Tests --filter "Category!=RealTime"` and `dotnet test tests/Kopru.App.Tests`
   (headless). Where PowerShell is available, `tools/test.ps1` runs them in parallel shards.
4. `dotnet publish src/Kopru.App -c Release -r linux-x64 --self-contained -o out/ds5-untethered-linux-app` must succeed.
5. Add a line to the Linux note at the top of `CHANGELOG.md`.

## Publish

Every push to `linux` runs `.github/workflows/ci.yml` on Ubuntu. Besides the build and tests it publishes:

- `ds5-untethered-linux-app-x64.tar.gz`: the app folder, `ds5-untethered.desktop`, the icon (`ds5-untethered.png`),
  `install.sh` and the commented-out polkit rule (`50-ds5-untethered.rules`) from `packaging/linux/`;
- `ds5-untethered-linux-spike-x64.tar.gz`: the headless spike.

Both are kept as build artifacts for 30 days and uploaded to the `linux-spike` pre-release. That tag stays fixed (it is
created once, on the `linux` branch); each push only replaces the two files and rewrites the release notes, which name
the commit (short SHA) the files were built from. Being a pre-release, it never becomes the Windows app's latest release.

## Data files shared with main

`src/Kopru.Core/Games/playstation-support.json`, `supported-games.json` and `xbox-trigger-games.json` (CC BY-NC-SA 3.0,
see THIRD-PARTY-NOTICES.md) are refreshed on `main` with the tools there and copied to this branch as they are.
