# Phase 06 - App: the About-tab action, the mail and the reveal

**Status:** Planned

## Goal

One button in the About tab that produces the archive, opens the default mail program prepared
for the author, and reveals the archive in a file manager (spec criteria 1, 4, 5, 6, 10).

## Changes

1. `src/StreamsPlayer.App/ProductInfo.cs` - `public const string AuthorEmail = "serzhyale@gmail.com";`
   (the address exists today only in the README and the site copy; the app gets one home for it).
2. New `src/StreamsPlayer.App/LogReportMailer.cs` - the shell side, kept out of the windows:
   - `Compose(string archivePath, string subject, string body)` builds the `mailto:` URI with
     `Uri.EscapeDataString` on subject and body (a raw `&`, `#` or line break in a translated
     body would silently truncate the message) and starts it with `UseShellExecute = true`.
   - `Reveal(string archivePath)` runs `explorer.exe /select,"<path>"`.
   - Both return a bool rather than throwing; `Win32Exception` (no handler registered) and
     `InvalidOperationException` are the expected failures and map to criterion 10's message.
   - Body length is capped at 1500 characters before escaping, because shell command length and
     client behaviour both degrade past that (spec Risks: default-mail behaviour varies).
3. `src/StreamsPlayer.App/MainWindow.ImportExport.cs` - rename `StreamListAction` to
   `SettingsAction` and `RunStreamListActionAsync` to `RunSettingsActionAsync`, add the
   `SendLogsToAuthor` member routed to the new handler (see INDEX "Decisions").
4. `src/StreamsPlayer.App/MainWindow.Diagnostics.cs` - `SendLogsToAuthorAsync(Window owner)`:
   builds the environment record from `_state` + `ProductInfo.Version` +
   `RuntimeInformation.OSDescription`/`OSArchitecture`, renders it, builds the archive off the UI
   thread (`Task.Run` - it copies and compresses files), then composes the mail and reveals the
   file; records `LOG REPORT` with the outcome and the archive size; on failure shows the localized
   explanation and, when an archive exists, its path.
5. `src/StreamsPlayer.App/SettingsWindow.xaml` - in the About tab, below the links row: a button
   `SendLogsButton` with `Content="{DynamicResource SendLogs}"`,
   `ToolTip="{DynamicResource SendLogsTip}"`, `AutomationProperties.Name="{DynamicResource SendLogs}"`,
   and one wrapped `TextBlock` `{DynamicResource SendLogsHint}` stating what the archive contains
   (spec Decision 4). Left-aligned in a `StackPanel` so the mirrored layouts follow the tab's
   existing flow direction.
6. `src/StreamsPlayer.App/SettingsWindow.xaml.cs` - the click handler calls
   `_runSettingsAction(SettingsAction.SendLogsToAuthor, this)`; the field and constructor parameter
   are renamed with the type. No new constructor parameter.
7. `src/StreamsPlayer.App/MainWindow.Settings.cs` - the call site follows the rename.

## New localization keys (English added here, twelve translations in phase 07)

`SendLogs`, `SendLogsTip`, `SendLogsHint`, `SendLogsSubject`, `SendLogsBody`, `SendLogsReady`,
`SendLogsNoMailClient`, `SendLogsFailed`, `SendLogsNoLogs`.

`SendLogsSubject` takes the version; `SendLogsBody` takes the archive file name and its folder;
`SendLogsNoMailClient` takes the archive path. Placeholders are positional `{0}`-style, matching
the existing `LocalizationService.Format` usage, so the parity gate's placeholder fact covers them.

## Verification

- `dotnet build StreamsPlayer.sln -c Release` succeeds with no warnings.
- `grep -rn "StreamListAction\|RunStreamListActionAsync" src/` - expected: no hits (rename complete).
- `grep -c "SendLogs" src/StreamsPlayer.App/Localization.en.xaml` - expected 9 keys.
- `dotnet test StreamsPlayer.sln -c Release --no-build` - the localization parity gate is expected
  to **fail** here for the twelve untranslated dictionaries; phase 07 turns it green. Record the
  failing count as this phase's evidence rather than pretending it passed.

## Checks

- Status: Implemented.
- expected: Release build clean | actual: 0 warnings, 0 errors.
- expected: no `StreamListAction` hits left | actual: `grep -rn "StreamListAction\|RunStreamListActionAsync" src/` - no matches.
- expected: 9 English keys | actual: `grep -c "SendLogs" src/StreamsPlayer.App/Localization.en.xaml` - 9.
- expected: parity gate fails for twelve dictionaries at this point | actual: `LocalizationParityTests.EveryDictionaryHoldsTheSameKeySetAsEnglish` failed with 12 x 9 missing keys, as planned; phase 07 turned it green.
- Deviation from the plan: the `mailto:` construction moved to Core (`DiagnosticMailLink`) with its own tests. Criterion 4 cannot be observed on this machine (see phase 09), and escaping is precisely the part that fails silently, so it needed a mechanical proof rather than a screenshot.
- `SendLogsBody` uses literal `&#x0D;&#x0A;` entities: XAML collapses real newlines in element content, which would have flattened the mail body into one paragraph.
