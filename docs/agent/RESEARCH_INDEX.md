# StreamPlayer Research Index

Start research with `README.md`, then the relevant `PLAN/` ticket, then locate symbols with `rg` before reading implementation files. The stable code map is:

- `src/StreamPlayer.Core`: catalog contracts, CSV/playlist parsing, bank reading, merge/prune, service, and local store.
- `src/StreamPlayer.App`: WPF windows, UI coordination, favicon loading, and presentation helpers.
- `tests/StreamPlayer.Core.Tests`: Core behavioural and contract tests.
- `tools/StreamPlayer.CatalogHarness`: live catalog-bank smoke/contract harness.
- `docs/specifications/streams.txt`: standalone product specification.

For a ticket-bound investigation, save a concise cited dossier in its ticket directory or `tmp/`, then link only durable findings from the ticket. Do not repeatedly grep a question already answered by a dossier.

