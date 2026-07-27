# Phase 12 - Stale-claim sweep and winget

**Status:** Implemented

Criteria 13 and 14. The audit found roughly forty places asserting two or three interface languages,
including three READMEs that contradict themselves - SP-0029 added a three-language bullet without
removing the older two-language one.

1. READMEs, keeping the set at three (criterion 13): remove the stale two-language bullet from
   `README.md:47-48`, `README.ru.md:47-48` and `README.uk.md:47-48`, and rewrite the surviving bullet
   (`README.md:69-70`, `README.ru.md:60-61`, `README.uk.md:60-61`) to state thirteen languages without
   listing them - the list lives in the registry. Leave the reciprocal language link block
   (`:15-20` in each) alone.
   Static check: each README states the language count exactly once.

2. Site copy: replace `fact-langs` (`EN / RU interface`) and `cap-yours-4`
   (`Switch the whole interface EN ⇄ RU`) in all thirteen `tools/site/copy/*.txt` files from phase 09.
   Static check: `rg 'EN / RU|EN ⇄ RU' docs/ tools/site/` returns nothing.

3. winget: add `winget/templates/SerZhyAle.StreamsPlayer.locale.uk-UA.yaml` modelled on the ru-RU
   template - `PackageLocale: uk-UA`, `ManifestType: locale`, no `ReleaseDate` (valid only on the
   default locale), and a new `REPLACE_RELEASE_NOTES_UK` token. Use the Ukrainian product name
   `Трансляції`, matching the ru-RU template's use of a localized name. Correct the counts in
   `winget/README.md:21,23` from four files and two release-note tokens to five and three. Remove the
   two-language claims from `locale.en-US.yaml:16` and `locale.ru-RU.yaml:14-15`.
   Static check: `winget/templates/` holds five files and `rg 'English and Russian' winget/` is empty.

4. Store copy already handled by phase 10; remove the remaining two-language claims from
   `msix/store-listing.md:37,56,92,171` or delete that file if phase 10's copy deck supersedes it.
   Static check: `rg 'English and Russian|RU / EN' msix/` returns nothing.

5. `STORE_PUBLISHING.md`: correct the two-language material at `:61,67-69,85-87,98,105-106,153`, and
   fix the instruction at `:110` that says "The file is UTF-8 with BOM .. Keep that encoding." - that
   is exactly what Partner Center refuses. Add the recorded Partner Center rules the repository does
   not yet carry: re-take the export before every import, add languages by hand first or their copy is
   dropped silently, the import is all-or-nothing, a relative image path works only via folder upload,
   a listing needs a description and a screenshot to leave Incomplete, and never copy the Win10
   logo-override flag between languages.
   Static check: `rg 'Keep that encoding|EN \+ RU' STORE_PUBLISHING.md` returns nothing.

6. Do **not** change `msix/AppxManifest.xml:15`. A single `<Resource Language="en-us"/>` is correct:
   the CyrFlip precedent ships thirteen listing languages with the same single-language manifest and
   records that a listing language is independent of the package resource set. The manifest text names
   no language count, so it already satisfies criterion 14.
   Static check: `AppxManifest.xml` is unchanged and states no language count.

7. Amend the open sibling tickets that still assert two languages so the next grep stays clean:
   `PLAN/SP-0026_selectable_media_backend.md` and its phase files, and
   `PLAN/SP-0027_keep_awake_during_playback.md`. Leave everything under `PLAN/DONE/` alone - those are
   historical records.
   Static check: the claim grep from the audit returns hits only under `PLAN/DONE/`.

## Checks

- Full claim grep (`English and Russian|EN / RU|RU / EN|EN ⇄ RU|EN \+ RU|EN/RU|English/Russian|русским
  и английским|англійською та російською`, across `*.md *.ps1 *.yaml *.yml *.xaml *.cs *.txt *.html
  *.js`) - expected: nothing outside history | actual: the only hits are `PLAN/DONE/`, `manifests/`
  (published snapshots of already-submitted manifests), the gitignored legacy `tmp/` tree, and three
  new comments that quote the old claim to explain why the new tooling exists.
- READMEs - expected: the count stated once each | actual: one bullet each in `README.md`,
  `README.ru.md`, `README.uk.md`; the stale two-language bullet removed from all three; the reciprocal
  language link block untouched.
- winget - expected: five templates, three release-note tokens | actual: five files including
  `locale.uk-UA.yaml` with `PackageName: Трансляції` and `REPLACE_RELEASE_NOTES_UK`, no `ReleaseDate`;
  `winget/README.md` says five and names all three tokens.
- `AppxManifest.xml` - expected: unchanged | actual: not in the diff.
- Sibling tickets - expected: amended to point at the registry | actual: SP-0026 (ticket, INDEX,
  PHASE-4, research) and SP-0027 (two places) now require every shipped language and name the parity
  gate as the enforcement.

### Four stale claims the plan did not list

Found by widening the grep past the phrase the audit had used:

1. **`CLAUDE.md:55` - "Localization is English + Russian"**, in the file that instructs every agent
   working in this repository. Left alone it would have kept re-teaching the wrong fact. Rewritten to
   name the single declaration, the nullable `Language` field and the CI parity gate.
2. **`.claude/agents/streamsplayer-doc-writer.md:11` - "StreamsPlayer has no published downloads yet,
   so never imply a shipped release."** Not a language claim at all, but flatly false: the app is live
   on the Microsoft Store and in winget. That instruction was actively pushing the doc agent to write
   incorrect copy. Corrected, along with the same file's three-language assumption and a new rule that
   the ten machine-translated languages must be labelled as such.
3. **`tools/store/make-store-images.ps1:90,94`** baked `EN / RU` and `RU / EN` into the composed
   promotional cards - a stale claim rendered into a shipped PNG, where no grep of the text would ever
   catch it again. Changed to `13 languages` / `13 языков` and all ten Store images regenerated.
4. **`STORE_PUBLISHING.md:69`** pointed at `screenshot-{en,ru}-1366x768.png`; the files are
   `-2732x1536`. A wrong path in a publishing checklist costs the owner a search at the worst moment.

### The winget locale set stays at three, and now says so

Criterion 13 keeps winget at en/ru/uk while the app ships thirteen, which looks like an oversight to
the next reader. `winget/README.md` now states the reason: a winget locale is prose the owner
re-verifies on every release, and ten machine-translated package descriptions would be maintenance with
no reader. Documented so nobody "completes" the set.

### `msix/store-listing.md` reduced rather than deleted

Phase 10's decks supersede its copy, but the file also held the runFullTrust justification,
certification notes, system requirements and the submission profile - none of which belong in a
per-language deck. It is now that material only, with a line saying copy lives in `msix/listing/`. The
certification test path also gained a step that exercises the language picker and one right-to-left
language, since that is now a reviewable feature.
