# Phase 06 - Glossary and the ten dictionaries

**Status:** Approved

Decision 4. Ten new dictionaries of 300 keys each. The glossary is not a transfer from CyrFlip -
CyrFlip has none, because its storage form puts all thirteen variants of a string on one screen where
a translator sees its neighbours. Thirteen separate files lose that, so the glossary compensates for
the storage form this ticket keeps.

Encoding safety is procedural and must be mechanical, per the Constraints. The existing dictionaries
are UTF-8 **without** BOM with CRLF endings and no XML declaration, and `memory/MEMORY.md:94-101`
records a confirmed incident where `powershell.exe` 5.1 double-encoded these exact files. Write them
only with the Write/Edit tools; never read-modify-write them from Windows PowerShell 5.1.

1. Add `docs/localization/glossary.md`: the recurring terms named in Decision 4 - stream, catalog,
   channel, refresh, pinned, collection, preview - with the chosen rendering in each of the thirteen
   languages, one row per term. Maintainer material, English headings, not localized (non-goal 2).
   Static check: the table has 13 language columns and 7 term rows with no empty cell.

2. Add the thirteen `Language*` endonym keys to `Localization.en.xaml`, `.ru.xaml` and `.uk.xaml`
   (ten are new), each holding the language's own name in its own script, plus the `UiFlowDirection`
   key set to `LeftToRight`. Endonyms stay identical across all dictionaries and are on the parity
   gate's allow-list.
   Static check: the three existing dictionaries gain the same keys and the gate still passes.

3. Create `src/StreamsPlayer.App/Localization.{de,it,es,fr,pt,zh,hi,bn,ar,ur}.xaml`, each a full
   translation of all keys, following the glossary, keeping every format placeholder exactly as
   English has it, and preserving the rephrased plural-free forms from phase 04. `ar` and `ur` set
   `UiFlowDirection` to `RightToLeft`. Same shape as the existing files: `<ResourceDictionary>` root
   with the `sys` namespace, `<sys:String x:Key="...">`, no XML declaration, no BOM.
   Static check: `dotnet test --filter FullyQualifiedName~LocalizationParityTests` passes for all
   thirteen files.

4. Add the honest-labelling text Decision 4 requires - a short statement, visible to the user in the
   Settings window, that translations are machine-produced and not proofread by native speakers. One
   new key, translated into all thirteen like any other string. Do not claim proofread quality
   anywhere (non-goal 8).
   Static check: the key exists in all thirteen dictionaries and is bound in `SettingsWindow.xaml`.

5. Confirm the ten new dictionaries are picked up without csproj changes: WPF globs `*.xaml` and the
   existing three are not listed in `StreamsPlayer.App.csproj` either.
   Static check: `dotnet build` compiles thirteen `Localization.*.baml` resources into the assembly.
