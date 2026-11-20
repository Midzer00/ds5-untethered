# Contributing to DS5 Untethered

Thank you for helping. A few rules keep the project open, legally clean and stable for its Windows users.

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

## Linux work

- Linux support is experimental and community-maintained. Pull requests for Linux target the **`linux`** branch.
- Linux code goes in `src/Kopru.Platform.Linux` behind `IPlatform` (see [docs/linux/README.md](docs/linux/README.md)).
  Shared code in `Kopru.Core` must not name an OS-specific class; add a member to `IPlatform` instead.
- Keep anything that needs root out of the app: put it in a tiny, separate helper that does one thing, and document why.
- Keep parsing pure (strings and bytes in, values out) and cover it with unit tests that run on every OS.

## Windows behaviour must not change

The Windows app is what people use. A change for Linux (or anything else) must leave Windows behaviour exactly as it
is, unless the pull request is about changing it on purpose.

- `dotnet build Kopru.sln` passes with no warnings.
- The tests pass: `powershell -ExecutionPolicy Bypass -File tools\test.ps1` on Windows runs all of them.
- A test that only works on Windows (Windows APIs, the registry, Windows paths) gets `[Trait("Category", "Windows")]`,
  so the Linux CI job skips it. Making such a test portable instead is welcome.

What CI (`.github/workflows/ci.yml`) runs on every push and pull request to `main` and `linux`:

- Windows: builds `Kopru.sln` and runs the Core tests except the timer pacing ones (`Category=RealTime`); these must
  pass. Then the timer pacing tests alone and the App tests run, but are allowed to fail (shared runners are too noisy
  for the timing checks, and the App tests are slow there); look at their results anyway, and run `tools\test.ps1`
  locally before asking for a merge.
- Linux: builds Kopru.Core, Kopru.Platform.Linux, the Linux spike and the Core tests, and runs the Core tests except
  `Category=Windows` and `Category=RealTime`; these must pass. The App tests do not run on Linux.

## Style

- C# with tabs, nullable enabled, no new warnings. Follow the code around your change.
- Comments and commit messages in English: say why, not what.
- Never put a real Bluetooth address, an email address, a user name or a local absolute path in code, tests, logs or
  documentation. Tests use made-up values (for addresses, locally administered ones such as `02:00:00:00:00:01`).
- One logical change per pull request; small pull requests get reviewed faster.
