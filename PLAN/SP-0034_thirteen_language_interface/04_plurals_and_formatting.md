# Phase 04 - Pseudo-plurals and formatting

**Status:** Implemented

Decision 8. Runs before phase 06 so no string is translated thirteen times and then rewritten. The
target form already exists in the repository: the Russian and Ukrainian dictionaries solved these
same keys with a "label: count" phrasing, which needs no grammatical agreement.

1. Rephrase the six parenthesised-suffix keys in `src/StreamsPlayer.App/Localization.en.xaml` -
   `ImportPreviewSummary`, `ImportResult`, `ExportResult`, `DeleteDownloadedConfirm`,
   `DeleteDownloadedResult`, `CollectionCount` - into forms correct without agreement, matching the
   Russian phrasing already in `Localization.ru.xaml`. Also rephrase `ChannelCount`
   (`{0:N0} of {1:N0} channels`), which has the same agreement problem in Slavic and Arabic even
   though it carries no `(s)`. Adjust the ru and uk values only where the new English shape changes
   the argument order.
   Static check: `rg '\(s\)' src/StreamsPlayer.App/Localization.*.xaml` returns nothing.

2. Replace the two culture-less format calls with `LocalizationService.Format`:
   `src/StreamsPlayer.App/MainWindow.Localization.cs:128` and
   `src/StreamsPlayer.App/ChannelRow.cs:169`, both formatting `BitrateValue`. Left as they are, they
   format numbers under the OS culture rather than the selected language, which becomes visible with
   Hindi, Bengali and Arabic digit shaping.
   Static check: `rg 'string\.Format\(LocalizationService' src/StreamsPlayer.App` returns nothing.

3. Make the two non-modal snapshot sites follow a language change. `PlayerWindow` is shown with
   `Show()` (`MainWindow.xaml.cs:959`), so the picker can be used while it is open, yet
   `PlayerWindow.xaml.cs:122` composes `Title` by interpolation and `:236`/`:376` assign `WaitText`
   once. Give `PlayerWindow` a `RefreshLocalization()` that recomposes them from keys, and call it
   from `MainWindow.Localization.cs:RefreshLocalizedInterface`.
   Static check: `RefreshLocalizedInterface` reaches every open `PlayerWindow`.

4. Replace the hardcoded `" - "` title separators (`PlayerWindow.xaml.cs:122`,
   `MainWindow.Localization.cs:178`) with a keyed format string so the order can differ per language.
   Static check: `rg '" - "' src/StreamsPlayer.App` returns no title composition.

## Checks

- `rg -c '\(s\)' src/StreamsPlayer.App/Localization.*.xaml` - expected: 0 per file | actual: 0, 0, 0.
- `rg 'string\.Format\(LocalizationService' src/StreamsPlayer.App` - expected: no hit | actual: none.
  Both `BitrateValue` sites now go through `LocalizationService.Format`, which formats under
  `CurrentUICulture` and therefore follows the selected language's digit shaping.
- `rg '\$"\{.*\} - \{' src/StreamsPlayer.App/*.cs` - expected: no title interpolation | actual: none.
- `dotnet build StreamsPlayer.sln -c Release` - expected: succeeds | actual: succeeded, 0 warnings.
- `dotnet test StreamsPlayer.sln -c Release --no-build` - expected: no regression | actual: 274 passed.

### Rephrasings applied

All seven moved to the agreement-free "label: count" shape the Russian and Ukrainian values already
used, so the target form was precedent rather than invention:

| Key | Was | Now |
|---|---|---|
| `ImportPreviewSummary` | `{0} new channel(s) will be added as imported.` | `New channels to be added as imported: {0}.` |
| `ImportResult` | `Imported {0} channel(s).` | `Imported channels: {0}.` |
| `ExportResult` | `Exported {0} channel(s).` | `Exported channels: {0}.` |
| `DeleteDownloadedConfirm` | `Delete all {0:N0} downloaded catalog stream(s)? ..` | `Delete all downloaded catalog streams ({0:N0})? ..` |
| `DeleteDownloadedResult` | `Deleted {0:N0} downloaded stream(s).` | `Deleted downloaded streams: {0:N0}.` |
| `CollectionCount` | `{0:N0} channel(s)` | `channels: {0:N0}` |
| `ChannelCount` | `{0:N0} of {1:N0} channels` | `Channels: {0:N0} / {1:N0}` |

`ChannelCount` is the one whose Russian and Ukrainian values also changed: the old shape put the count
before a noun that has to agree with it, which survives Slavic genitive but not Arabic's five number
categories. Argument order is unchanged, so no call site moved.

### Scope note on step 3

Fixing `PlayerWindow` needed slightly more than the plan implied. Two of its four `WaitText`
assignments are `SetResourceReference` calls that already follow a swap, and two are formatted strings
that cannot. Overwriting `.Text` breaks the resource reference, so the two mechanisms have to be told
apart: `SetWaitText` records the key and arguments for replay, `SetWaitTextResource` clears that record
and re-establishes the binding, and `RefreshLocalization` replays only when a recorded key exists.
Reached from `MainWindow.RefreshLocalizedInterface` over `Application.Current.Windows`, since
`_openPlayerWindows` is only a count and holds no references.

New key `WindowTitleWithSubject` (`{0} - {1}`) replaces both hardcoded `" - "` compositions, so a
right-to-left language can reorder the title rather than inheriting Latin order.
