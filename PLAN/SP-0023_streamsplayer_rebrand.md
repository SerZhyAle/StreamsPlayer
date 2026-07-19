# SP-0023: StreamsPlayer product rebrand

**Status:** BlockNeedUserTest — English UI was observed; Russian runtime switching still needs direct observation.

## Goal

Align every project surface with the StreamsPlayer identity, presenting it as **STREAMS Player** in English and **Трансляции** in Russian.

## Why

The published repository and desired product identity use StreamsPlayer. A consistent name prevents broken links, confusing executable/package identities, and mixed branding in the Windows UI.

## Non-goals

- Publish a release, tag, MSIX package, winget manifest, or GitHub Pages deployment.
- Change stream-bank compatibility, playback behaviour, or catalog data.
- Automatically migrate existing local catalog, pins, shortcuts, previews, or logs from a prior application-data directory.

## Constraints

- Technical identifiers use `StreamsPlayer`: solution, projects, assemblies, namespaces, executable, package identifiers, local-data directory, user-agent, and repository URLs.
- English user-facing product identity is exactly `STREAMS Player`; Russian user-facing product identity is exactly `Трансляции`.
- Existing user data under a prior application-data root remains untouched; the app uses `%LOCALAPPDATA%\StreamsPlayer` as its local-data root.
- The active repository URL is `https://github.com/SerZhyAle/StreamsPlayer`; planned GitHub Pages URLs use the matching `StreamsPlayer` path.
- Historical tickets, research, and validation records use the same StreamsPlayer identity as the active project.

## Acceptance criteria

1. The solution, project references, namespaces, assembly names, executable name, build/run scripts, tests, and catalog harness consistently use `StreamsPlayer`.
2. The English UI, English/Ukrainian public documentation, package display metadata, and website show `STREAMS Player`; Russian UI and Russian public documentation show `Трансляции`.
3. Active source, build, workflow, repository, MSIX, winget, and site links refer to the StreamsPlayer repository and corresponding public paths.
4. Active local state, logs, shortcuts, and diagnostic user-agent identify StreamsPlayer, and no automatic read/write touches the prior local-data root.
5. Release build and tests pass; a launched application visibly presents the correct English and Russian names and writes only to the new local-data root.
6. A source scan finds no stale former identity in tracked project surfaces.

## Risks

Renaming package identities and local storage creates a clean separation from the old product but means existing local data is not automatically available. A future migration must be an explicit, privacy-reviewed feature rather than an implicit side effect of rebranding.

## Research

See `PLAN/SP-0023_streamsplayer_rebrand/research.md`.

## Last Audit

- PASS — solution, projects, namespaces, assemblies, executable, scripts, test harness, package templates, URLs, and local-data root use `StreamsPlayer`.
- PASS — English user-facing resources and observed window title use `STREAMS Player`; Russian resources, package metadata, and documentation use `Трансляции`.
- PASS — expected: `./scripts/check.ps1` succeeds | actual: Release build completed with 0 warnings/errors and 38/38 tests passed.
- PASS — expected: a launched English application shows the new brand and creates the new local root | actual: `StreamsPlayer.exe` reported `MainWindowTitle=STREAMS Player`; `%LOCALAPPDATA%\StreamsPlayer` exists.
- MANUAL — expected: switching the running UI to Russian shows `Трансляции` in main, settings, player, and dialog titles | actual: localized resources and dynamic resource bindings were checked statically; the switching interaction was not observed in this run.
