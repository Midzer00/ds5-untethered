# Contributing to DS5 Untethered

Thank you for helping. A few rules keep the project open, legally clean and stable for its users.

## License

DS5 Untethered is licensed under **GPL-3.0-or-later** (see [LICENSE](LICENSE)). By sending a contribution you agree that
it is licensed under the same terms. New source files start with:

```csharp
// SPDX-License-Identifier: GPL-3.0-or-later
```

## Sign your commits (DCO)

Every commit needs a `Signed-off-by` line, which certifies the
[Developer Certificate of Origin 1.1](https://developercertificate.org/): you wrote the change or otherwise have the
right to submit it under the project's license. Git adds the line for you:

```sh
git commit -s -m "Describe the change"
```

Use your real name or the name you are known by on GitHub, with an email you can be reached at. Pull requests with
unsigned commits cannot be merged (`git rebase --signoff main` fixes older commits).

## Clean-room rule

Write your own code. **Do not copy code from other projects**, and do not translate it from another language either.
That includes, among others, OpenDS5, the Linux kernel drivers (hid-playstation, usbip/vhci), SDL, Wine and Proton,
Steam, inputplumber, DSX, ViGEm and xpad, even where their licenses would allow it.

Allowed: public documentation and specifications (the USB and HID specifications, the USB/IP protocol document, kernel
`Documentation/` pages, man pages, public header constants such as ioctl numbers), your own measurements and captures of
your own hardware, and this repository's code. When you rely on a document, name it in a comment.

## This branch: the Linux app

- The `linux` branch is the Linux app (experimental, community-maintained). It is built and released separately from
  the Windows app, which lives on `main`, and the two are never merged. Pull requests for Linux target `linux`.
- The Windows-only code (the Windows platform, HidHide, usbip-win2, WASAPI, the registry, the updater and installer) is
  not on this branch. The shared parts keep the same files and names as on `main` (the bridge, the virtual pads, the
  USB/IP server, protocol, haptics, lighting, profiles, localization, the games catalog and the view models), so fixes
  can be carried over between the branches by hand. Do not rename or move them.
- Linux code goes in `src/Kopru.Platform.Linux` behind `IPlatform` (see [docs/linux/README.md](docs/linux/README.md)).
  Shared code in `Kopru.Core` must not name an OS-specific class; add a member to `IPlatform` instead.
- Keep anything that needs root out of the app. Today that is only `usbip attach` and `usbip detach`, which go through
  `pkexec`; anything more belongs in a tiny, separate helper that does one thing, and document why.
- Keep parsing pure (strings and bytes in, values out) and cover it with unit tests.

## Checks before a pull request

- `dotnet build Kopru.sln` passes with no warnings.
- The tests pass: `dotnet test tests/Kopru.Core.Tests --filter "Category!=RealTime"` and `dotnet test
  tests/Kopru.App.Tests` (the App tests run headless). `tools/test.ps1` runs both in parallel shards where PowerShell is
  available.
- `dotnet publish src/Kopru.App -c Release -r linux-x64 --self-contained` succeeds.

What CI (`.github/workflows/ci.yml`) runs on every push and pull request to `linux`, on Ubuntu: builds `Kopru.sln`,
runs the Core tests except the timer pacing ones (`Category=RealTime`) and the App tests (still allowed to fail while the
Linux app settles), and publishes the app and the headless spike. On pushes it refreshes the `linux-spike` pre-release.

## Style

- C# with tabs, nullable enabled, no new warnings. Follow the code around your change.
- Comments and commit messages in English: say why, not what.
- Never put a real Bluetooth address, an email address, a user name or a local absolute path in code, tests, logs or
  documentation. Tests use made-up values (for addresses, locally administered ones such as `02:00:00:00:00:01`).
- One logical change per pull request; small pull requests get reviewed faster.
