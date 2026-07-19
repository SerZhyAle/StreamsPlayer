# Phase 03 — Validation

1. [x] Run a source scan for emoji/icon-range characters in app and documentation.
   - Check: the scan returns no matches. PASS.
2. [x] Build and test the solution in Release.
   - Check: `dotnet build StreamsPlayer.sln -c Release` and `dotnet test
     StreamsPlayer.sln -c Release --no-build` exit zero.
3. [x] Launch the app and visually inspect representative ordinary, compact, and
   video-player buttons.
   - Check: glyphs render, labels/tooltip actions remain understandable, and
     the UI stays responsive. PASS; evidence under `tmp/`.
