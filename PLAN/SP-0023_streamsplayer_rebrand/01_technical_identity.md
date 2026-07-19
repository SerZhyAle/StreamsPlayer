# Phase 01 — Technical identity

**Status:** Approved

1. Rename the solution, project folders/files, test/tool identities, and winget template filenames to `StreamsPlayer`. Update every solution and project reference, namespace, XAML class reference, assembly name, executable reference, build command, and test/harness import. Static check: no stale former technical identity remains in tracked project files.
2. Update active script process/executable references, local product paths, diagnostic User-Agent, app metadata, and package technical identifiers to `StreamsPlayer`. Static check: build scripts and application code agree on `StreamsPlayer.exe` and `%LOCALAPPDATA%\StreamsPlayer`.
