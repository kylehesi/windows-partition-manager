# Adding a language

How to add a new language setting to Windows Partition Manager. The setting changes the
culture (date/number formatting) used by the desktop app and `wpm`; it does not by itself
translate the UI strings — see [Translating the UI](#translating-the-ui).

## Where it lives

- `src/WindowsPartitionManager.Platform.Windows/LanguagePreference.cs` — the single source of
  truth: `AvailableLanguages`, `ResolveCulture`, `Apply`, `FormatTimestamp`, `LoadLanguage`,
  `SettingsPath`.
- Everything else reads from there:
  - `src/WindowsPartitionManager.App/Theming/AppSettings.cs` (`SettingsStore`)
  - `src/WindowsPartitionManager.App/Theming/ThemeManager.cs`
  - `src/WindowsPartitionManager.App/ViewModels/SettingsViewModel.cs`
  - `src/WindowsPartitionManager.App/SettingsWindow.xaml`
  - `src/WindowsPartitionManager.App/App.xaml.cs`
  - `src/WindowsPartitionManager.Cli/Program.cs`

## Adding a language (minimal)

To add, for example, German:

1. In `LanguagePreference.AvailableLanguages`, add `"German"`.
2. In `LanguagePreference.ResolveCulture`, add `"German" => "de"`.

That is all. The Settings dropdown, persistence, culture application, date formatting and the
CLI all pick it up automatically because they read from `LanguagePreference`.

`FormatTimestamp` already formats every non-English culture with `"g"` (the culture's short
date/time); only English keeps the ISO `yyyy-MM-dd HH:mm` form. A new non-English language
needs no change there.

## Tests

Add to `tests/WindowsPartitionManager.App.Tests/LanguagePreferenceTests.cs`:

- a `ResolveCulture_MapsLanguageToCode` case (language name -> culture code), and
- a `FormatTimestamp_*` assertion for the new culture's short date/time output.

## Translating the UI

The user-facing strings are localizable through `Strings.resx` (neutral English) and per-language
satellites (`Strings.tr.resx` for Turkish). XAML reads them with `{DynamicResource Key}` and the
view models with `Localization.Get` / `Localization.Format`. To translate the UI into a new
language:

1. Add `Strings.<code>.resx` (e.g. `Strings.de.resx`) next to `Strings.resx`, with the same keys
   and the translated values. Missing keys fall back to the English neutral resource.
2. Include the culture in `SatelliteResourceLanguages` in
   `src/WindowsPartitionManager.App/WindowsPartitionManager.App.csproj` (e.g. `en;tr;de`) so its
   satellite assembly is produced.

The per-language rollout is tracked in `future/README.txt` (day1–day4).
