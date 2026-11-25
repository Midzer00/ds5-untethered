# Translating DS5 Untethered

Thank you for helping translate DS5 Untethered! All user-facing UI strings live in flat JSON files under
`src/Kopru.Core/Localization/strings/` so translators never need to edit C# or XAML code.

## Translating on the website

Open the translation page at **https://ds5untethered.com/translate/**. It shows every English string next to your
translation, with screenshots of where each text appears, and checks placeholders, plurals and length while you type.
Your draft is kept in your browser, so you can stop and come back later.

1. Choose your language, or type its BCP-47 code if it is not listed yet (for example `de`, `pt-BR` or `zh-Hans`).
   First target languages include Simplified Chinese (`zh-Hans`), Russian (`ru`), Spanish (`es`),
   Brazilian Portuguese (`pt-BR`), German (`de`), Japanese (`ja`), French (`fr`), Polish (`pl`), and Korean (`ko`),
   but every language is welcome. English (`en`) is the source and Turkish (`tr`) is maintained by the project,
   so neither is accepted as a volunteer translation.
2. Translate from the English source strings.
   - Set the special `@name` entry to your language's native name (for example, `简体中文`, `Русский`,
     `Español`, `Português (Brasil)`, `Deutsch`, `日本語`, `Français`, `Polski`, `한국어`). This name is shown
     directly in **Settings → General → Language**.
   - Any missing or empty key automatically falls back to English, so partial translations are safe.
3. Send it in one of three ways (all need a free GitHub account):
   - **Send with GitHub** (easiest): sign in to GitHub once in the pop-up and the page opens a translation issue in
     your name. A check runs automatically; if the file passes, a pull request is opened with you as co-author and
     the issue is closed. If something needs fixing, the problems are listed on the issue.
   - **Upload form:** download your `<code>.json` file, then use the page's link to open the
     [Translation issue form](https://github.com/Midzer00/ds5-untethered/issues/new?template=translation.yml), drop the
     file into its upload field and submit. The same automatic check and pull request follow.
   - **Paste:** copy the file and use the page's link to open the file on GitHub's web editor; GitHub creates a fork and
     you open the pull request yourself. The same rules are checked on the pull request.

A later send for the same language from the same account updates your existing pull request instead of opening a new
one. Nothing is merged automatically: the maintainer reviews every translation first.

### What the automatic check looks for

The page shows the same checks while you translate, so a file the page accepts normally passes:

- the language code is a valid BCP-47 code (not `en`, `tr`, `qps` or `qtg`) and matches the file name;
- the file is valid JSON (UTF-8), one flat object of `"key": "text"` pairs, at most 1 MB, without duplicate keys;
- every key exists in `en.json` (plus `@name`);
- every text keeps the English placeholders, has balanced braces, and keeps `plural` blocks with an `other` branch
  where your language has plural forms;
- no HTML tags, no links unless the English text has one, and at most 4× the English length plus 20 characters.

## Translating with a pull request (developers)

Developers can also add or edit `src/Kopru.Core/Localization/strings/<code>.json` directly, using BCP-47 language
codes such as `pt-BR` or `zh-Hans` as the file name, and open a pull request. Before sending, check the file with the
same rules the pull request check uses (Node.js 20 or newer, no install needed):

```powershell
node tools\i18n\translation-check\cli.js src\Kopru.Core\Localization\strings\de.json
```

Pull requests that change `en.json` or `tr.json` are accepted only from the maintainer. Running
`powershell -File tools\test.ps1` also checks every shipped language file in the app's own tests (it needs a
non-empty `@name` and no empty values, so leave untranslated keys out instead of setting them to `""`).

## Glossary

Keep these core concepts distinct and consistent across every screen:

| English term | Meaning & translation guidance |
| --- | --- |
| **Haptics** | DualSense voice-coil haptic feedback — fine-grained, audio-driven vibrations sent by native DualSense games (or audio-to-haptics). Keep this distinct from classic rumble. |
| **Classic rumble** | Traditional two-motor vibration (used by Xbox / DualShock 4 games, or as a fallback when a game does not send DualSense haptics). |
| **Adaptive triggers** | Motorized L2/R2 trigger resistance and recoil effects unique to DualSense controllers. |
| **Experimental** | Short label/tag shown beside features that are still being tested (for example, Xbox Series trigger rumble). Keep it short and neutral. |
| **`ø` button** | The center home button on the controller. **Always use the `ø` symbol** (e.g., `ø button`, `ø tuşu`). Never write `PS` or `PlayStation button`. |

### Names that stay as-is

Do **not** translate these product, hardware, or protocol names:

- `DualSense`, `DualSense Edge`
- `DualShock 4`, `Xbox 360`, `Xbox Series`
- `DS5 Untethered`
- `Bluetooth`, `USB`
- `Steam`, `DSX`, `HidHide`, `ViGEm`
- Button codes when used as symbols: `L1`, `L2`, `L3`, `R1`, `R2`, `R3`

## Rules

### 1. Keep placeholders and ICU plurals intact

- Named placeholders such as `{count}`, `{name}`, `{rate}`, or `{percent}` are replaced at runtime.
  **Never translate, rename, remove, or add placeholder names.** You may reorder placeholders to fit your
  language's grammar.
- Pluralized strings use ICU `MessageFormat` syntax, for example:
  ```text
  {count, plural, one {# game} other {# games}}
  ```
  - `#` is replaced by the formatted number.
  - Keep the placeholder name (`count`) and the keyword `plural` in English.
  - Use the CLDR cardinal plural categories required by your language:
    - `other` only: Chinese (`zh-Hans`), Japanese (`ja`), Korean (`ko`), Turkish (`tr`)
    - `one`, `other`: English (`en`), German (`de`), Spanish (`es`), Brazilian Portuguese (`pt-BR`), French (`fr`) *(French also supports `many` for exact millions)*
    - `one`, `few`, `many`, `other`: Russian (`ru`), Polish (`pl`)
  - Always include an `other {…}` branch.
  - If a message contains a literal apostrophe next to `{` or `}`, escape it by doubling it (`''`). A normal
    apostrophe between letters (such as `don't` or `l'audio`) does not need escaping.

### 2. Keep it short

- Most pages are designed to fit in one window (`1100×700`, `1152×700`, and `960×640`) without vertical scrolling.
- Navigation items, tab strips, button labels, and picker choices have limited width. Aim for roughly the same
  length as English; if a translation is much longer than English (30%+), prefer a shorter natural phrasing over
  a literal word-for-word translation.

### 3. No game names

- Do **not** include real or copyrighted game titles in translations or examples.

### 4. Use your language's natural casing

- English UI headings and buttons use Title Case, **but other languages do not have to**. The app renders your
  exact casing verbatim without rewriting it to Title Case, and formats numbers and dates using your language's
  locale (for example, Turkish `İ`/`ı` casing and locale-appropriate decimal separators).

### 5. What stays in English

- Diagnostic logs, technical error reports, and release update notes are intentionally kept out of community
  translation (update notes are maintained in English and Turkish).

## Previewing context screenshots

Maintainers and contributors with a Windows build environment can generate headless screenshots of every page
and dialog for any language (without starting the app or connecting a controller):

```powershell
powershell -ExecutionPolicy Bypass -File tools\i18n\screenshots.ps1 -Languages en
```

Screenshots are saved to `TestResults\i18n-screenshots\<lang>\` (git-ignored).

## Credits

Once your translation is merged, your GitHub name is listed under your language on ds5untethered.com: on the home
page's language list and in the translation page ("Translated by @you"). The list is built from the repository
history (commit authors and co-authors) and refreshed automatically, so it shows only your public GitHub login,
linked to your profile.

If you would rather not be listed:

- **Send with GitHub** or the **upload form:** tick "Do not list my name among the translators" before you send.
  The choice is kept for all your translations.
- **Your own pull request:** write in the pull request that you do not want to be listed; the maintainer adds your
  login to `tools/i18n/translators-hidden.txt`.

You can also ask for this later in an issue at any time.
