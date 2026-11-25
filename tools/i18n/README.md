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
```

`rules.js` ports the page validator (`site/web/assets/translate/core.js`); the two must stay in sync. The parity test
in `rules.test.js` compares them when the site folder sits next to the repository.

`credits.js` builds the translator credits (`translators.json`) from the language files' commit history. The
`Translators data` workflow publishes it on the branch `translation-data`; `translators-hidden.txt` lists logins that
are never shown.
