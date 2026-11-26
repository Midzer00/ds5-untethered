# Localization tools

## String extraction (`Extract/`)

Re-runnable Roslyn tool that converts any remaining `Pick(l, $"...", $"...")` calls in
`src/Kopru.Core/Localization/Text*.cs` into `T(l, "Key", ...)` calls and updates `en.json` / `tr.json`:

```powershell
dotnet run --project tools\i18n\Extract\Kopru.I18nExtract.csproj -- --repo .
```

## Translator context screenshots (`screenshots.ps1`)

Renders every page and dialog headless into PNGs grouped by language folder (`<OutDir>\<lang>\*.png`) so translators
can inspect UI context without running the app or connecting hardware:

```powershell
powershell -ExecutionPolicy Bypass -File tools\i18n\screenshots.ps1
```

Optional flags: `-OutDir <path>` (defaults to `TestResults\i18n-screenshots`, git-ignored), `-Languages en,tr,qps`
(defaults to all shipped languages), `-Width 1152 -Height 700`, `-NoBuild`.

## Translation file checks (`translation-check/`)

The rules every volunteer translation must pass, shared by the translation page, the `Translation validate` workflow
(pull requests) and the `Translation intake` workflow (issues from the page or the issue form). Zero dependencies,
Node.js 20 or newer:

```powershell
node tools\i18n\translation-check\cli.js src\Kopru.Core\Localization\strings\de.json
node --test tools\i18n\translation-check\test\rules.test.js tools\i18n\translation-check\test\cli.test.js tools\i18n\translation-check\test\pr-check.test.js tools\i18n\translation-check\test\intake.test.js tools\i18n\translation-check\test\actions.test.js tools\i18n\translation-check\test\credits.test.js tools\i18n\translation-check\test\credits-action.test.js
node --test tools\i18n\translate-worker\test\worker.test.mjs
```

`rules.js` ports the page validator (`site/web/assets/translate/core.js`); the two must stay in sync. The parity test
in `rules.test.js` compares them when the site folder sits next to the repository.

### Trust and the validate check (maintainer note)

The `Translation validate` result on a pull request is advisory, not a trust boundary. It runs on `pull_request`, and
GitHub runs the workflow file from the pull request itself, so a fork pull request can change
`.github/workflows/translation-validate.yml` or the checker in `tools/i18n/` in the same pull request and make the
check pass. Read the diff, not only the check mark. Two settings make the check binding:

- **Require review from Code Owners** in the branch ruleset (or branch protection) for `main`, with a pull request
  required before merging. `.github/CODEOWNERS` assigns `.github/` and `tools/i18n/` to the owner, so a pull request
  that touches the workflows or the checks cannot be merged without the owner's approving review.
- **Require approval for first-time contributors** (Settings > Actions > General > "Approval for running fork pull
  request workflows from contributors"; "Require approval for all external contributors" is stricter). A modified
  workflow from a new contributor then does not run until the owner has looked at it and approved the run.

Do not make `Translation validate` a required status check: pull requests opened by the intake workflow use the
workflow token, and GitHub starts no workflows for those, so a required check would never report on them. They need
no validate run: the intake already applied the same rules (from the default branch) before opening them.

The intake workflow puts each volunteer's pull request on `translate/<numeric user id>/<code>`, allows 3 open ones per
account, and comments a refusal at most 3 times per issue.

### Merging translation pull requests (maintainer note)

The intake commit of a translation pull request is **authored by the volunteer** (their
`<id>+<login>@users.noreply.github.com` address) and committed by `github-actions[bot]`. That authorship is what puts
the volunteer in the repository's Contributors list and in the translator credits.

- Merge these pull requests with **Create a merge commit** or **Rebase and merge**. Both keep the volunteer's commit
  as it is, with its author.
- Do **not** use **Squash and merge**: it writes a new commit whose author GitHub picks, and the pull request was
  opened by the bot, so the volunteer can lose the authorship and the credit. (The credits ignore `Co-authored-by`
  lines outside the intake's own commits, so one that GitHub adds to a squash commit does not count.)
- When the volunteer asked not to be listed, the commit is authored by the bot and carries
  `Translator-Listing: hidden`; the pull request title then has no name either, because the default merge commit
  message is the title. Keep the trailer when merging.
- Volunteer-authored commits have no `Signed-off-by` line, so the DCO check shows "Action required" on them. It is
  not a required check; see "The DCO check" in `translate-worker/OWNER-SETUP.md`.

### Send with GitHub (`translate-worker/`)

The Cloudflare Worker behind "Send with GitHub". The volunteer signs in through an OAuth App that asks for no scopes;
the Worker learns their login and id, revokes the token at once, and opens the issue as the GitHub App bot
`ds5-untethered-translate[bot]` with the line `**Sender:** login (id N, verified)` directly under the marker (no `@`,
so nobody is notified). The bot never reposts the volunteer's text: the Worker rebuilds the whole issue from the checked
values. The intake workflow trusts that line only in issues opened by that bot; in any other issue the author is the
volunteer.
Setup, secrets and rotation: `translate-worker/OWNER-SETUP.md`. The repository copy holds no secrets.

`credits.js` builds the translator credits (`translators.json`) from the language files' commit history. The
`Translators data` workflow publishes it on the branch `translation-data`; `translators-hidden.txt` lists logins that
are never shown. Credit goes to commit authors. `Co-authored-by` lines and `Translator-Listing: hidden` are message text
anyone can write, so they count only on the intake's own commits (committer `github-actions[bot]`, exact intake message;
co-authors only on the SHA-allowlisted older intake commits). A normal pull request cannot credit or hide anyone else
that way. Commit authorship itself is not verified by git: check the author of every commit before merging.
