# DIAGNOSTIC-REPORT - pointer

| | |
| --- | --- |
| **Id** | `DIAGNOSTIC-REPORT` |
| **Version** | 0.9 (draft) |
| **Home** | `Contracts/diagnostic-report/README.md` |
| **Owner** | **this product**. StreamsPlayer defines the diagnostic bundle format, environment summary, and redaction invariants |
| **This product's role** | **producer and consumer** |

## What this repository must do to stay conformant

1. **Diagnostic Bundle Structure:** Pack session logs and `environment.txt` into a bounded `.zip` archive (`StreamsPlayer-logs-yyyyMMdd-HHmmss.zip`).
2. **Environment Summary Invariant:** The environment summary is count-only and structural metadata only; it must **never** contain user stream URLs, channel titles, collection names, or history entries.
3. **Mandatory Redaction:** Mask all passwords, tokens, PINs, and auth headers with `[REDACTED]`; sanitize absolute user profile paths with `<USER>` or `<APP_DATA>`.
4. **Bounded Size & Compaction:** Cap live session log at 16 MB with middle compaction (keeping 1 MB head + 7 MB tail with `LOG COMPACTED` marker). Truncate oversize logs in archive at 16 MB with `LOG TRUNCATED` marker. Rotate and retain up to 10 previous session logs.
5. **User Consent Only:** Generate diagnostic archives and copy diagnostic info only on explicit user request; zero background telemetry.

Evidence: `src/StreamsPlayer.Core/DiagnosticArchiveBuilder.cs`, `DiagnosticEnvironmentSummary.cs`, `DiagnosticLogFiles.cs`, `src/StreamsPlayer.App/CurrentLog.cs`, `MainWindow.Diagnostics.cs`. Tests: `DiagnosticArchiveBuilderTests.cs`, `DiagnosticEnvironmentSummaryTests.cs`, `DiagnosticLogFilesTests.cs`.
