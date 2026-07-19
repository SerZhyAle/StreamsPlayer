# Research — SP-0023

## Evidence

- The previous product identity appeared in solution/project names, C# namespaces, assembly/executable names, build scripts, local-data paths, User-Agent, WPF XAML class names, MSIX/winget templates, GitHub Actions, README/site pages, and product links.
- The active GitHub remote is `https://github.com/SerZhyAle/StreamsPlayer.git`; the previous singular public repository path is absent.
- The prior application used a different local-data root for catalog state, preview assets, and `Current.log`; implicitly moving that data would make migration an unreviewed behaviour change.
- The public README and WPF labels required coordinated technical and localized updates.

## Settled decisions

- Use `StreamsPlayer` for all technical identities.
- Use `STREAMS Player` for English-facing brand text and `Трансляции` for Russian-facing brand text.
- Use a new `%LOCALAPPDATA%\StreamsPlayer` root without automatic migration.
- Preserve `PLAN/DONE` as historical evidence rather than mechanically rewriting old observed commands, paths, and screenshots.

## Risks

- Physical project renames must update the solution and every project reference together.
- Namespace/XAML renames can fail at build time if even one generated XAML type or `using` remains stale.
- Package and URL identities are publication-facing contracts; the templates may be changed, but no package/release is published in this ticket.
