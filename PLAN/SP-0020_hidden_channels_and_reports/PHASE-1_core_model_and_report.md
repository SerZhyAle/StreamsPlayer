# PHASE-1 — Core: hidden-catalog model, URL identity, failure report + redaction, category

**Consumes:** nothing.
**Produces:** persisted hide state, a shared URL-identity helper, a platform-neutral failure-report formatter with credential redaction, and a stable error-category classifier — all consumed by Phases 2–4.

Keep everything in `StreamsPlayer.Core` (platform-neutral; no WPF/App/media types). `CatalogMerger` is **not** modified in this phase.

## Steps

1. **Hide state on `CatalogState`** — [src/StreamsPlayer.Core/Models.cs](../../src/StreamsPlayer.Core/Models.cs) (`CatalogState`, ~line 122).
   - Add `public List<string> HiddenCatalogUrls { get; init; } = [];`.
   - Leave `SchemaVersion = 1` (defaulted collection is backward/forward compatible under Web-defaults JSON; no migration).
   - Static check: `dotnet build StreamsPlayer.sln` succeeds.

2. **Shared URL identity helper** — new `src/StreamsPlayer.Core/CatalogUrlIdentity.cs`.
   - `public static string Normalize(string url)` — deterministic identity used for hide matching: trim; lower-case scheme + host; preserve path/query/fragment. Must be idempotent and total (never throws on the URLs the catalog already accepts).
   - `public static bool IsHidden(IEnumerable<string> hiddenUrls, string channelUrl)` — membership by normalized identity.
   - Note: matching is by normalized identity applied to **both** sides, so a catalog refresh that re-adds the exact `Url` (Ordinal, per `CatalogMerger`) still matches.

3. **Credential/redaction helper** — in the same `CatalogUrlIdentity.cs` (or a `UrlRedactor`): `public static string Redact(string url)` strips userinfo (`user:pass@`) and known credential-bearing query keys; returns a display-safe URL. Never emits local filesystem paths.

4. **Failure-report formatter** — new `src/StreamsPlayer.Core/FailureReportFormatter.cs`.
   - `public static string Format(FailureReport report)` where `FailureReport` carries: app version (string, injected by caller), UTC timestamp (`DateTimeOffset`, injected), channel title, stream URL, `MediaKind`, and a stable error category. Output is a bounded, human-readable block; URL is passed through `Redact`; excludes local paths, credentials, and any log/catalog dump.

5. **Error-category classifier** — new `src/StreamsPlayer.Core/PlaybackErrorCategory.cs`.
   - `enum PlaybackErrorCategory { Rejected, MediaError, Network, Unsupported, Unknown }` (finalize set during impl).
   - `public static PlaybackErrorCategory Classify(string reason)` mapping the existing failure reasons (`"play_rejected"`, `"encountered_error"`, audio exception type names) to a stable category. Deterministic; unknown → `Unknown`.

6. **Tests** — `tests/StreamsPlayer.Core.Tests` (new files, e.g. `CatalogStateHideTests.cs`, `FailureReportFormatterTests.cs`, `CatalogUrlIdentityTests.cs`):
   - `CatalogState` with `HiddenCatalogUrls` round-trips through `StreamCatalogStore.SaveAsync`/`LoadAsync` (reuse the temp-dir pattern in `StreamCatalogStoreTests`).
   - `CatalogUrlIdentity.Normalize` is idempotent; `IsHidden` matches a re-added exact catalog URL.
   - `FailureReportFormatter.Format` contains app version, UTC time, title, media kind, category, and a redacted URL; asserts a `user:pass@` credential is absent from output.
   - `PlaybackErrorCategory.Classify` returns stable categories for the known reasons and `Unknown` otherwise.
   - Existing `CatalogMergerTests` still pass unchanged (merge protection intact).

## Static verification predicate

`dotnet build StreamsPlayer.sln` succeeds, and focused Core tests pass:
`dotnet test tests/StreamsPlayer.Core.Tests -c Release --filter "FullyQualifiedName~CatalogStateHideTests|FullyQualifiedName~FailureReportFormatterTests|FullyQualifiedName~CatalogUrlIdentityTests|FullyQualifiedName~CatalogMergerTests"`.
Record `expected: build ok + N/N passed | actual: ...`.

## Result — DONE

- Added `CatalogState.HiddenCatalogUrls`, `CatalogUrlIdentity` (Normalize/SameIdentity/IsHidden/Redact), `FailureReport`+`FailureReportFormatter`, `PlaybackErrorCategory`+`PlaybackErrorClassifier`. `CatalogMerger` untouched. `StreamTitleFormatter` is App-only, so the formatter uses the raw trimmed title (Core stays platform-neutral).
- expected: Release solution build clean; focused Core tests pass | actual: build 0 warnings / 0 errors; 21/21 passed (`CatalogStateHideTests`, `CatalogUrlIdentityTests`, `FailureReportFormatterTests`, `CatalogMergerTests`).
