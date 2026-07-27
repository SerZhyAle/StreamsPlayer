# SP-0037: Russian surface audit

**Status:** Draft

## Problem

The Ukrainian proofreading pass (2026-07-27, release `26.0727.0253`) audited all five Ukrainian
surfaces against their English sources. The same audit read the Russian siblings as a reference and
found the identical class of defects still present in Russian. They were left alone deliberately -
the task was scoped to Ukrainian - but leaving them recorded only in a commit message would lose
them.

Three concrete findings:

1. **The glossary contradicts the shipped Russian dictionary on `stream`.**
   `docs/localization/glossary.md` prescribes `трансляция`; `Localization.ru.xaml` uses `поток` 26
   times and `трансляц*` only 12, and those twelve are the product name plus the SP-0030
   delete-downloaded block. The same reasoning that settled Ukrainian applies: `Трансляции` is the
   localized product name, so "Удалить загруженные трансляции" reads as deleting the application.
   Either the glossary row or the SP-0030 block is wrong, and it is the same call that was made for
   Ukrainian. Nothing gates this - `LocalizationParityTests` does not compare dictionaries against
   the glossary.

2. **`README.ru.md` is factually stale in the same way `README.uk.md` was.** It is missing the
   listening-history, M3U-import and M3U-export bullets that `README.md` carries, and it omits the
   listening history from its local-data enumeration. `ProductInfo` opens this file as the in-app
   **"Инструкция"** for the Russian locale, so a stale claim here is wrong information inside a UX
   flow, not a documentation nicety. This was the highest-severity finding on the Ukrainian side.

3. **`tools/site/copy/ru.txt` carries the drift the Ukrainian deck was translated from.** The
   Ukrainian copy was a near-literal rendering of the Russian, so every wording defect fixed in
   Ukrainian still stands in Russian: `Исходники MIT` for "MIT source", `Радио начинается в главном
   окне` for "Radio starts", `«Скопировать команду»` where the button reads `Копировать команду`,
   and `выбор сохраняется` where the English says the choice is *restored*.

## Approach

Run the same three-reader audit used for Ukrainian - app dictionary, long-form docs, distribution
metadata - against the Russian surfaces, comparing each to its English source of truth. Resolve the
glossary conflict first, because it decides the terminology every other fix has to follow. Then
resync `README.ru.md` against `README.md` before touching its language, since content correctness
outranks wording.

Do not translate the Ukrainian result into Russian: the two languages made different, defensible
choices in places (`добірка` vs `подборка`, `прев’ю` vs `превью`), and the audit's value is comparing
each language to English, not to its neighbour.

## Out of scope

The other ten machine-translated languages. They carry the `MachineTranslationNotice` and no native
reader has been assigned; Russian and Ukrainian are the two the owner can verify.
