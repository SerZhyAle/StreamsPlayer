# PHASE-1 — Core model and pure history logic

Produces: `ListeningHistoryEntry`, `CatalogState.ListeningHistory`, `ListeningHistory`
(pure). Consumes: nothing. Keeps `StreamsPlayer.Core` platform-neutral.

## Steps

1. **`ListeningHistoryEntry` record** — add to
   [src/StreamsPlayer.Core/Models.cs](../../src/StreamsPlayer.Core/Models.cs):

   ```csharp
   public sealed record ListeningHistoryEntry
   {
       public required Guid ChannelId { get; init; }
       public required string Title { get; init; }
       public required MediaKind MediaKind { get; init; }
       public required DateTimeOffset LastPlayedAt { get; init; }
       public string? LastTrackText { get; init; }
   }
   ```
   No `Url` field — playback resolves by `ChannelId` only (privacy + no stale-URL reopen).

2. **`CatalogState.ListeningHistory`** — add to the `CatalogState` record (Models.cs) a
   `public List<ListeningHistoryEntry> ListeningHistory { get; init; } = [];` with an XML
   doc comment: recency-ordered (most recent first), keyed by channel id, bounded to
   `ListeningHistory.MaxEntries`, local-only, never uploaded; older state files without
   the key deserialize to the empty default. Do **not** bump `SchemaVersion` (matches
   `HiddenCatalogUrls`/`KeepAwakeDuringPlayback` precedent).

3. **`ListeningHistory` pure helper** — new
   [src/StreamsPlayer.Core/ListeningHistory.cs](../../src/StreamsPlayer.Core/ListeningHistory.cs),
   `public static class ListeningHistory`:
   - `public const int MaxEntries = 100;`
   - `RecordPlay(IReadOnlyList<ListeningHistoryEntry> history, Guid channelId, string title, MediaKind kind, DateTimeOffset playedAt)`
     → returns a new list: take the existing entry for `channelId` (if any) to preserve
     its `LastTrackText`, drop it from its old position, insert a refreshed entry
     (`Title`/`MediaKind` from the new play, `LastPlayedAt = playedAt`,
     `LastTrackText` carried over) at the **front**, then truncate the tail to
     `MaxEntries`. Pure — allocates a new `List`, never mutates the input.
   - `UpdateTrackText(IReadOnlyList<ListeningHistoryEntry> history, Guid channelId, string? trackText)`
     → if an entry with `channelId` exists **and** its `LastTrackText` differs, return a
     new list with only that entry's `LastTrackText` replaced (position unchanged, no
     add); otherwise return the input materialized unchanged. Trims/collapses nothing —
     the caller passes already-parsed ICY text.
   - Carry-over rationale (comment): a fresh play should not blank the last-known song
     line before ICY re-populates it; ICY overwrites via `UpdateTrackText`.

4. **Core tests** — new
   [tests/StreamsPlayer.Core.Tests/ListeningHistoryTests.cs](../../tests/StreamsPlayer.Core.Tests/ListeningHistoryTests.cs):
   - New play prepends and returns a new list (input unchanged).
   - Replaying an existing channel moves it to front without adding a row (count stable)
     and carries the prior `LastTrackText`.
   - Inserting a 101st distinct channel evicts exactly the oldest; count == 100; the
     evicted id is gone and the newest is at front.
   - `UpdateTrackText` sets text on the matching entry only, no reorder; unknown id and
     unchanged text are no-ops (reference-stable content).
   - Ordering is strictly most-recent-first after a sequence of plays.

## Static verification predicate

- `dotnet build src/StreamsPlayer.Core/StreamsPlayer.Core.csproj -c Release` → 0 warnings.
- `dotnet test tests/StreamsPlayer.Core.Tests -c Release --filter "FullyQualifiedName~ListeningHistoryTests"` → all green.
- `rg -n "using.*Windows|using.*Wpf" src/StreamsPlayer.Core/ListeningHistory.cs` → no match (Core boundary intact).
- Record `expected: build 0 warn; N history tests pass; no WPF using | actual: ...`.

## Result — DONE

- Added `ListeningHistoryEntry` and `CatalogState.ListeningHistory` (Models.cs) plus the
  pure `ListeningHistory` helper (`MaxEntries = 100`, `RecordPlay`, `UpdateTrackText`
  returning `null` on no-op). 10 tests in `ListeningHistoryTests.cs` cover prepend,
  promote-without-dup + track carry-over, eviction/cap, ordering, track-update no-ops,
  round-trip, and legacy-default.
- expected: Core build 0 warn; history tests pass; no WPF using | actual: build 0 warn;
  10/10 pass; `rg` finds no WPF/Windows using in ListeningHistory.cs.
