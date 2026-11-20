## What and why

<!-- One or two sentences. Link the issue, e.g. "Part of #1". -->

## Checklist

- [ ] Every commit is signed off (`git commit -s`, DCO).
- [ ] My own code: nothing copied or translated from other projects (see CONTRIBUTING.md, clean-room rule).
- [ ] Linux pull requests target the `linux` branch.
- [ ] Windows behaviour is unchanged (or the change is the point of this pull request, and says so above).
- [ ] `dotnet build Kopru.sln` has no warnings and the tests pass; Windows-only tests are marked `Category=Windows`.
- [ ] Root-only code (if any) is in a small separate helper, not in the app.
- [ ] No real Bluetooth addresses, emails, user names or local paths in code, tests, logs or docs.

## Tested on

<!-- OS (Windows version, or distribution, kernel and desktop), Bluetooth adapter, controller, and games if relevant. -->
