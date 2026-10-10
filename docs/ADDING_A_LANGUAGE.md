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

The setting alone does not translate strings. To actually translate the UI you need:

1. A resource model (`.resx` / `ResourceManager`) for the user-facing strings.
2. A satellite assembly for the new culture (e.g. `tr`, `de`, `fr`, `es`).
3. Replace the hardcoded XAML / view-model strings with resource lookups.
4. Include the culture in `SatelliteResourceLanguages` in
   `src/WindowsPartitionManager.App/WindowsPartitionManager.App.csproj` so its satellite
   assembly is produced.

The per-language rollout is tracked in `future/README.txt` (day1–day4).
