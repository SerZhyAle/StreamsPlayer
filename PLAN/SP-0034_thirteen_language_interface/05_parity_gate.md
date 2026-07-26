# Phase 05 - Parity gate

**Status:** Approved

Criterion 2, and the Constraints requirement that the gate run in continuous integration rather than
by hand. CI already runs `dotnet test` on `windows-latest`, so a test is the cheapest host that is
actually enforced.

CyrFlip cannot be copied here. Its key parity is guaranteed by the compiler - all thirteen
translations of a string are arguments of one `Add(...)` call, so the sets cannot diverge. With
thirteen separate dictionaries that guarantee is gone, and parity, placeholder integrity and
untranslated-leftover detection are new code. What transfers is the *gate discipline*: several
complementary facts, and a self-test proving the gate is capable of failing.

The gate must not create a project reference. `StreamsPlayer.Core.Tests` may not reference
`StreamsPlayer.App`, so the dictionaries are consumed as **files** and parsed as XML.

1. Add to `tests/StreamsPlayer.Core.Tests/StreamsPlayer.Core.Tests.csproj` a `Content` item linking
   `..\..\src\StreamsPlayer.App\Localization.*.xaml` into `localization\` with
   `CopyToOutputDirectory="PreserveNewest"`. This is a test fixture, not a dependency: no App type is
   referenced and the dependency direction is unchanged.
   Static check: the test output directory holds one `Localization.*.xaml` per shipped language and
   `StreamsPlayer.Core.Tests.csproj` contains no `ProjectReference` to the App.

2. Add `tests/StreamsPlayer.Core.Tests/LocalizationParityTests.cs` asserting, with English as the
   reference:
   - every `InterfaceLanguages.All` entry has a dictionary file, and every dictionary file on disk
     belongs to a registry entry (no orphan file, no missing language);
   - each dictionary's `x:Key` set equals English's, reporting the symmetric difference;
   - no duplicate `x:Key` within a file;
   - per key, the multiset of format placeholder indices equals English's, so `{0}`/`{1}` cannot be
     dropped, duplicated or renumbered;
   - the file has no byte-order mark and parses as XML;
   - `UiFlowDirection` (phase 08) equals `RightToLeft` for exactly the registry's right-to-left
     languages and `LeftToRight` otherwise, which keeps the registry authoritative over the
     dictionaries;
   - no value is byte-identical to the English value outside an explicit allow-list - `KindRtsp`, the
     thirteen `Language*` endonyms, and any key whose English value is a proper noun or acronym. The
     allow-list is a named constant with a comment per entry, so a leftover cannot be hidden by
     quietly widening it.
   Static check: `dotnet test --filter FullyQualifiedName~LocalizationParityTests` passes.

3. Add the self-test in the same file, exercising the comparison helpers on synthetic dictionaries: a
   missing key, an extra key, a changed placeholder set, a duplicated key, a BOM-prefixed file and a
   value left in English each produce a failure. A gate that silently inspected nothing would pass
   exactly as quietly as a clean one.
   Static check: each synthetic case is asserted to be rejected.

4. Assert criterion 15 mechanically in the same test class: no dictionary declares a `Source`
   attribute or any `http` URI, so no translation asset can be fetched at runtime.
   Static check: the assertion exists and passes.

5. Do not touch `.github/workflows/ci.yml` - the gate is a test and the existing `dotnet test` step
   already runs it.
   Static check: `ci.yml` is unchanged and the gate appears in the test run output.
