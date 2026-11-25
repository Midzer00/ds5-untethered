# Translating DS5 Untethered

Thank you for helping translate DS5 Untethered! All user-facing UI strings live in flat JSON files under
`src/Kopru.Core/Localization/strings/` so translators never need to edit C# or XAML code.

## Translating with GitHub

1. Open the [language files](../src/Kopru.Core/Localization/strings/). If your language is already listed, edit its file. Otherwise, copy `en.json` into a new file named with your language code, such as `es.json`, `pt-BR.json`, or `zh-Hans.json`.
2. Translate the values and leave the keys unchanged. Set the `@name` value to your language's name in that language. This is what appears in the app under **Settings → General → Language**.
3. Use the context screenshots below to check whether short labels, tabs, and buttons fit. Partial translations are welcome. Missing or empty values fall back to English.
4. Open a pull request with your changes. If you have the repository checked out locally, you can first run `powershell -File tools\test.ps1` to check your changes.

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

Volunteer translators are credited by name (or handle, if you prefer) in the project credits and release notes
**if you would like to be listed**. Let the maintainer know how you want your name to appear, or let us know if
you prefer to remain anonymous.
