# Translating DS5 Untethered

Thank you for helping translate DS5 Untethered! All user-facing UI strings live in flat JSON files under
`src/Kopru.Core/Localization/strings/` so translators never need to edit C# or XAML code.

## Translating on Weblate

1. Open the [Windows app interface on Weblate](https://hosted.weblate.org/projects/ds5-untethered/windows-ui/)
   and sign in (or create a free account).
2. Choose your language from the list, or start a new translation if yours is not listed yet.
   First target languages include Simplified Chinese (`zh-Hans`), Russian (`ru`), Spanish (`es`),
   Brazilian Portuguese (`pt-BR`), German (`de`), Japanese (`ja`), French (`fr`), Polish (`pl`), and Korean (`ko`),
   but every language is welcome.
3. Translate from the English source strings (`en.json`).
   - Set the special `@name` metadata entry to your language's native name (for example, `简体中文`, `Русский`,
     `Español`, `Português (Brasil)`, `Deutsch`, `日本語`, `Français`, `Polski`, `한국어`). This name is shown
     directly in **Settings → General → Language**.
   - Any missing or empty key automatically falls back to English, so partial translations are safe.
4. Check the context screenshots (if uploaded to Weblate or generated with `tools/i18n/screenshots.ps1`) to make
   sure short labels, tabs, and buttons fit their space.
5. Save your translations (or submit suggestions for review). Approved Weblate changes are synced back to the
   repository by the maintainer.

*(Developers who prefer GitHub pull requests can also add or edit `src/Kopru.Core/Localization/strings/<code>.json`
directly using BCP-47 language codes such as `pt-BR` or `zh-Hans`, then run `powershell -File tools\test.ps1`.)*

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
