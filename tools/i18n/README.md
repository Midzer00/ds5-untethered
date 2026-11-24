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
