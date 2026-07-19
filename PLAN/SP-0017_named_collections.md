# SP-0017: Local named channel collections

**Status:** Approved

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
