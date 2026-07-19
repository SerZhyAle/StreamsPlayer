# StreamPlayer Agent Memory

`memory/MEMORY.md` is the short, always-read index. Each durable entry is a separate Markdown file linked from that index.

Use four entry types:

- `user`: durable information that calibrates collaboration.
- `feedback`: non-obvious corrections and confirmed working preferences.
- `project`: active constraints or decisions not derivable from code or history.
- `reference`: a pointer to an external system and its purpose.

Each entry has frontmatter with `name`, `description`, and `type`, followed by the fact or rule and—when useful—`Why` and `How to apply`. Do not record paths, symbols, architecture, Git history, temporary task state, or anything already present in `AGENTS.md`; those are derivable or belong in a ticket. Verify remembered repository claims before acting on them.

