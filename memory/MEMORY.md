# StreamsPlayer Agent Memory

Short index of durable, non-obvious context for future sessions. Add one link per entry; keep entry bodies in separate files and verify repo claims before relying on them.

## Index

- [User Feedback & Collaboration Rules](feedback.md) - PR template disciplines, clarification preferences, verification habits
- [Project Architecture](project_architecture.md) - Codebase structure, module boundaries, cross-cutting concerns
- [Project General Learnings](project_general.md) - Cross-cutting project corrections and non-obvious patterns
- [Project Localization](project_localization.md) - Translation workflows, language-specific behaviors, CLDR collation
- [Project Playback](project_playback.md) - Media handling, backend selection, playback-specific patterns  
- [Project Theme](project_theme.md) - DynamicResource theming, palette system, WPF airspace considerations
- [References](references.md) - External references, standards, and cross-repository dependencies

## Adding New Entries

1. Choose an existing topic file or create a new one under `memory/`
2. Add a link to the file in the index above
3. Keep entries concise but actionable
4. Reference specific SP tickets, dates, and commit hashes where applicable
5. Prefer bullet lists with clear action items over long prose

## Verification Rule

Before relying on any entry, verify its claim against the current codebase. Out-of-date entries are worse than missing entries.
