# Phase 03: Validation

**Produces:** build/test and launched-process evidence.

**Consumes:** phases 01–02.

1. Run `dotnet build StreamsPlayer.sln -c Release` and `dotnet test StreamsPlayer.sln -c Release --no-build`. Check: expected no build errors and all Core tests pass.
2. Launch the WPF app without altering existing user-owned processes, close it, and inspect `%LOCALAPPDATA%\StreamsPlayer\Current.log`. Check: expected startup and shutdown entries; actual result is recorded in the strategic ticket audit.
