# Phase 07 - Localization: nine keys x thirteen languages

**Status:** Planned

## Goal

The new About-tab strings, the mail subject and the mail body exist in every shipped interface
language and the parity gate is green again (spec criterion 11).

## Changes

- `src/StreamsPlayer.App/Localization.<code>.xaml` for all thirteen codes: the nine `SendLogs*`
  keys from phase 06, inserted next to the other About-tab keys so the file stays navigable.
- Terminology follows `docs/localization/glossary.md`; the product name is not re-invented.
- The mail body is prose the author will read in the user's language - it must state that the
  archive holds diagnostic logs, that the user attached it deliberately, and nothing else.

## Constraints

- UTF-8 without BOM, `\r\n`, no other line in these files touched - dictionary encoding has been
  corrupted before by the wrong editing tool (SP-0034).
- Identical placeholder sets per key across all thirteen; no key added to the
  `SameAsEnglishAllowed` allow-list in the parity test - these strings are prose and must be
  translated.

## Verification

- `dotnet test tests/StreamsPlayer.Core.Tests -c Release --filter "FullyQualifiedName~LocalizationParityTests"`
  passes - key parity, placeholder parity and the no-untranslated-leftover fact all cover the nine
  new keys.
- `grep -c "SendLogs" src/StreamsPlayer.App/Localization.*.xaml` - expected 9 in each of the
  thirteen files.

## Checks

- Status: Implemented.
- expected: 9 `SendLogs` keys in each of the thirteen dictionaries | actual: 9 in all thirteen.
- expected: parity gate green | actual: `--filter "FullyQualifiedName~Localization"` - Passed 25, Failed 0.
- Owner-level decision taken after the translation pass: `SendLogsSubject` keeps the Latin `STREAMS Player` in Russian and Ukrainian too, against the glossary rule that localizes the brand in those two. The subject is what the author sorts his inbox by, and a Cyrillic-only subject would sit outside that filter; the in-app strings stay fully localized.
- Observed automation names: Italian `Invia i registri all'autore`, Arabic `إرسال السجلات إلى المؤلف`.
