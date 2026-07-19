# StreamPlayer Validation Ladder

Pick the lowest level that proves the changed behaviour, then record `expected` and `actual`.

1. Static inspection: file, symbol, reference, or project-boundary check.
2. Formatting diagnostic: `dotnet format StreamPlayer.sln --verify-no-changes`. It currently reports pre-existing line-ending/encoding issues, so do not treat it as a passing gate until a dedicated baseline-normalization ticket resolves them.
3. Build: `dotnet build StreamPlayer.sln`.
4. Focused tests: the relevant `StreamPlayer.Core.Tests` tests.
5. Full tests: `dotnet test StreamPlayer.sln`.
6. Harness: `dotnet run --project tools/StreamPlayer.CatalogHarness -- artifacts/favicon-sample.png` for catalog-delivery behaviour.
7. GUI observation: run the WPF app and exercise the changed visible path.

A passing build is not proof that a changed user action works. Store large logs, screenshots, and temporary evidence under `tmp/` or ignored `artifacts/`; keep ticket journals short and point to evidence paths.
