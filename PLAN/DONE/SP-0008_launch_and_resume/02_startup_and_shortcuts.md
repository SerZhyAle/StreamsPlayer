# Phase 02 — Startup and shortcuts

1. [x] Replace declarative startup with argument-aware application startup and pass
   the parsed request to the main window.
   - Static check: the main window receives a launch request and no longer
     relies on `StartupUri`. PASS.
2. [x] Route explicit URL/ID requests after local state is ready; otherwise resume
   the saved local selection. Preserve offline and audio/video playback rules.
   - Static check: an explicit target takes precedence; a missing saved target
     does not throw or refresh. PASS.
3. [x] Add selected-channel command copy and desktop-shortcut creation, using the
   local record GUID and a clear localized result.
   - Static check: shortcut command construction quotes arguments and does not
     use a display-order value. PASS.
