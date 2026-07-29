# SP-0040 tactical plan - Send diagnostic logs to the author

Strategic spec: [SP-0040_send_logs_to_author.md](../SP-0040_send_logs_to_author.md)

## Shape

Three seams, in dependency order:

1. **Core (platform-neutral, unit-testable)** - log file naming and rotation, the environment
   summary text, the archive assembly. All pure `System.IO` + `System.IO.Compression`, no WPF,
   no shell, no mail. This is where the tests live, because `StreamsPlayer.Core.Tests` is the
   only test project.
2. **App (WPF + shell)** - retention wired into the log facade, the About-tab action, the
   `mailto:` compose, the file-manager reveal, the failure dialogs, and the playback-quality
   records (Decision 6).
3. **Surfaces** - thirteen interface dictionaries, three READMEs, thirteen site locales.

`MediaBackend`, `AppLanguage` and `CatalogState` already live in Core, so the summary builder
can take them directly without the App inventing a transport type.

## Phases

| # | File | What | Depends on |
|---|---|---|---|
| 01 | [01_core_log_files.md](01_core_log_files.md) | `DiagnosticLogFiles`: names + previous-session rotation | - |
| 02 | [02_core_summary.md](02_core_summary.md) | `DiagnosticEnvironmentSummary`: the environment text | - |
| 03 | [03_core_archive.md](03_core_archive.md) | `DiagnosticArchiveBuilder`: the ZIP, bounded and self-cleaning | 01, 02 |
| 04 | [04_app_log_rotation.md](04_app_log_rotation.md) | `CurrentLog` rotates instead of replacing | 01 |
| 05 | [05_app_quality_trace.md](05_app_quality_trace.md) | Session summaries for video and audio; Flyleaf stall/error records | - |
| 06 | [06_app_send_action.md](06_app_send_action.md) | About-tab button, action seam, mail + reveal, failure paths | 03, 04 |
| 07 | [07_localization.md](07_localization.md) | New keys in all thirteen dictionaries | 06 |
| 08 | [08_docs_and_site.md](08_docs_and_site.md) | READMEs, site copy, privacy correction, generated pages | 06 |
| 09 | [09_validation.md](09_validation.md) | Release gate + run-and-observe evidence | all |

## Decisions this plan fixes (the spec deliberately left them open)

- **The archive is named per attempt and cleaned up**: `StreamsPlayer-logs-<UTC yyyyMMdd-HHmmss>.zip`
  in `%LOCALAPPDATA%\StreamsPlayer\reports\`, and the builder deletes older archives in that folder
  before writing, so criterion 9 holds without a background job.
- **The action seam is the existing settings-action dispatcher**, renamed from `StreamListAction` to
  `SettingsAction` (three call sites, mechanical): a log report is not a stream-list operation, and
  filing it under that name would be the third unrelated member in an enum that already stretched
  once (SP-0030). No new callback parameter on `SettingsWindow`.
- **Counts come from `MainWindow`**, which owns `_state`; `SettingsWindow` never learns them.
- **The mail body is localized, the address and the file names are not.**
- **Nothing in the archive is redacted.** The log's content stays exactly as SP-0013 writes it,
  including stream URLs (spec Risks); the disclosure text says so instead of pretending otherwise.
