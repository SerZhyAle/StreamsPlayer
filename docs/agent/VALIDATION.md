# StreamsPlayer Validation Ladder

Pick the lowest level that proves the changed behaviour, then record `expected` and `actual`.

1. Static inspection: file, symbol, reference, or project-boundary check.
2. Formatting diagnostic: `dotnet format StreamsPlayer.sln --verify-no-changes`. It currently reports pre-existing line-ending/encoding issues, so do not treat it as a passing gate until a dedicated baseline-normalization ticket resolves them.
3. Build: `dotnet build StreamsPlayer.sln`.
4. Focused tests: the relevant `StreamsPlayer.Core.Tests` tests.
5. Full tests: `dotnet test StreamsPlayer.sln`.
6. Harness: `dotnet run --project tools/StreamsPlayer.CatalogHarness -- artifacts/favicon-sample.png` for catalog-delivery behaviour.
7. GUI observation: run the WPF app and exercise the changed visible path.
8. Playback smoke: `pwsh -NoProfile -File ./scripts/smoke-playback.ps1`. Publishes the tree and plays a live radio station through WPF `MediaElement` and a live video stream through LibVLC, from the shipping binary. **Mandatory before any release** (`release.ps1` step 2b) and after any change to media, packaging, the runtime, or a dependency that ships native DLLs.

Rung 8 is not a heavier rung 5, and adding tests will never replace it. Rungs 3-5 prove *this repository's code* behaves; SP-0093 was a defect in the WPF runtime beneath it, which refused every Internet-zone media URI. All 858 tests stayed green while the product played nothing, and a release shipped that no user could hear. **A green build says nothing about whether the product works** when the thing that broke is something the build merely consumes. The only evidence that it plays is that it played.

A passing build is not proof that a changed user action works. Store large logs, screenshots, and temporary evidence under `temp/<ticket>/` or ignored `artifacts/`; keep ticket journals short and point to evidence paths.
