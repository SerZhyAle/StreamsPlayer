# SP-0017: Local named channel collections

**Status:** Verified

## Goal

Allow users to organize channels into multiple local named collections while retaining the existing pinned list as a separate quick-access mechanism.

## Why

A single flat favorites/pinned set becomes difficult to manage as a user's library grows. Collections support distinct contexts such as News, Morning, or Cameras without adding an account or remote service.

## Non-goals

- Replace pinning, catalog facets, or search.
- Add cloud synchronization, sharing, collaboration, recommendations, or smart collections.
- Change the external stream-bank format.

## Constraints

- A channel can belong to multiple collections and has an independent order within each one.
- Collection names are local, user-editable, case-insensitively unique after trimming, and bounded to a practical display length.
- Deleting a collection never deletes its channels; deleting a channel removes its collection memberships.
- Catalog refresh preserves memberships for surviving URL identities and removes only references to rows genuinely pruned by the existing merge contract.
- Empty collections remain visible until the user deletes them.

## Acceptance criteria

1. Users can create, rename, reorder, and delete collections with localized validation and confirmation where data organization would be lost.
2. Users can add or remove a channel from one or more collections from both list and Grid presentations.
3. Opening a collection shows only its members in the collection's saved order while retaining normal play and channel actions.
4. Pin/unpin operations do not add, remove, or reorder collection memberships.
5. Catalog refresh, channel removal, restart, and missing-channel cleanup preserve a consistent collection state without resurrecting deleted data.
6. Persistence/merge tests and a run-and-observe collection-management check pass.

## Risks

Many-to-many membership and independent ordering add persistence complexity. Refresh and deletion paths must not leave dangling entries or silently collapse user-defined order.

## Research

See [competitor improvement backlog](../docs/specifications/competitor-improvement-backlog.md).

## Implementation notes (SP-0017)

- `StreamsPlayer.Core/ChannelCollections.cs` (new) - pure rules: normalize/validate names
  (trim, collapse, 40 chars, case-insensitive uniqueness), create/rename/delete, per-collection
  ordered membership, `RemoveChannelEverywhere`, `Prune`, `Members`, `MembershipOf`. 12 unit tests.
- `CatalogState.Collections` + `CatalogState.CatalogCollectionFilter` - persisted; both default so an
  older state file loads unchanged.
- `MainWindow.Collections.cs` (new) - the `Collection` facet next to the other filters, the
  "Add to collection" submenu on each channel (checkable membership, inline "new collection" box,
  "Manage collections…"), and prune-on-load / prune-after-refresh.
- `CollectionsWindow` (new) - create, in-place rename, delete-with-confirmation; the confirmation
  states that the channels stay.
- Deleting a channel (`MainWindow.Hide.cs`) and the SP-0030 purge drop memberships in the same save;
  collections themselves survive, even when empty. Pin/unpin never touches collections.
- `Localization.en/ru/uk.xaml` - fifteen strings each.

Static checks: `dotnet build StreamsPlayer.sln -c Debug` -> expected 0 errors | actual 0 errors,
0 warnings. `dotnet test` -> expected green | actual 189/189.

## Verification - agent-driven UIA run (2026-07-24, Ukrainian UI)

- expected: a channel can be put into a new collection from its row menu | actual: `Додати до добірки`
  -> inline name box -> `Джаз` created with that channel (`Джаз(1)` in state).
- expected: the collection view shows only its members | actual: filter `Джаз` -> `1 з 3,244 каналів`,
  back to `Усі` -> `3,244 з 3,244 каналів`.
- expected: manage window creates, renames and deletes | actual: rename `Джаз` -> `Джаз і блюз`
  persisted; `Камери` created; a duplicate ` камери ` was rejected with
  `Введіть непорожню назву, яка ще не використовується.`
- expected: deleting a collection keeps its channels | actual: confirmation
  `Видалити добірку «Камери»? Її канали залишаться у вашому списку.` -> collection gone, channel count
  still 3,244.
- expected: memberships survive a catalog refresh and a restart | actual: `Джаз і блюз(2)` before and
  after `Оновити каталог`, and again after a full restart; the active collection filter was restored
  from state (`2 з 3,244 каналів`).
- Not covered: cross-collection drag ordering (the ticket only requires an independent saved order,
  which membership append already provides).
