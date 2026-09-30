# DIAGNOSTIC-REPORT - pointer

| | |
| --- | --- |
| **Id** | `DIAGNOSTIC-REPORT` |
| **Version** | 0.10 (draft) |
| **Home** | `Contracts/diagnostic-report/README.md` |
| **Owner** | **this product**. StreamsPlayer defines the diagnostic bundle format, environment summary, and redaction invariants |
| **This product's role** | **producer and consumer** |

## What this repository must do to stay conformant

1. **Diagnostic Bundle Structure:** Pack session logs and `environment.txt` into a bounded `.zip` archive (`StreamsPlayer-logs-yyyyMMdd-HHmmss.zip`).
2. **Environment Summary Invariant:** The environment summary is count-only and structural metadata only; it must **never** contain user stream URLs, channel titles, collection names, or history entries.
3. **Mandatory Redaction:** Mask all passwords, tokens, PINs, and auth headers with `[REDACTED]`; sanitize absolute user profile paths with `<USER>` or `<APP_DATA>`.
4. **Bounded Size & Compaction:** Cap live session log at 16 MB with middle compaction (keeping 1 MB head + 7 MB tail with `LOG COMPACTED` marker). Truncate oversize logs in archive at 16 MB with `LOG TRUNCATED` marker. Rotate and retain the last 10 session logs in total - the current session's plus the 9 previous (amended 0.10, SP-0174).
5. **User Consent Only:** Generate diagnostic archives and copy diagnostic info only on explicit user request; zero background telemetry.

Evidence: `src/StreamsPlayer.Core/DiagnosticArchiveBuilder.cs`, `DiagnosticEnvironmentSummary.cs`, `DiagnosticLogFiles.cs`, `src/StreamsPlayer.App/CurrentLog.cs`, `MainWindow.Diagnostics.cs`. Tests: `DiagnosticArchiveBuilderTests.cs`, `DiagnosticEnvironmentSummaryTests.cs`, `DiagnosticLogFilesTests.cs`.

Rule 3 URL half (SP-0123): `CatalogUrlIdentity.RedactText` runs at the log sink (`CurrentLog`) and again when the archive is packed; `LogSinkRedactionSourceTests.cs` gates the sink against bypass. Rule 3 path half (SP-0137): `DiagnosticPathRedactor` replaces the data directory with `<APP_DATA>` and the profile with `<USER>` at the same two places; the registry exception is closed (2026-09-26). SP-0174 widens rule 3's URL half (secret query names in plain and HTML-escaped form, Xtream-style credential-in-path shapes, passwords containing `/`, `?` or `#`), bounds a path-redaction timeout to the line it strikes, keeps the profile folder out of the mail body, and pins rule 4's retention count with `DiagnosticLogFilesTests.Retention_MatchesTheContractRuleFourCount`.
