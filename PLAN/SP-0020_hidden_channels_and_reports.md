# SP-0020: Hidden catalog channels and copyable failure reports

**Status:** Approved

## Goal

Let users locally hide unwanted or persistently broken catalog channels and copy a concise diagnostic report for deliberate sharing.

## Why

Catalog refresh can restore rows the user does not want to see, while a generic failure message gives maintainers too little evidence. Local hiding and explicit report copying solve both without automatic telemetry.

## Non-goals

- Automatically send reports, logs, URLs, or analytics anywhere.
- Delete or mutate the published catalog.
- Run background channel monitoring.
- Replace removal of user-owned channels.

## Constraints

- Hide applies to catalog rows and is persisted by normalized URL identity across catalog refreshes.
- Hidden rows are excluded from ordinary views, search results, counts, preview capture, and playback navigation but are available in a dedicated manage-hidden view.
- Unhide restores the current catalog row without changing pin, order, collection, or play-mark semantics.
- A copied failure report contains app version, UTC time, channel title, catalog URL, media kind, and a stable error category; it excludes local paths, credentials, unrelated logs, and catalog contents.
- Copying is a visible user action and never transmits the report.

## Acceptance criteria

1. Hiding a catalog channel removes it from normal list/Grid views and keeps it hidden after restart and explicit catalog refresh.
2. Users can view and unhide hidden rows; a channel absent from the latest catalog is cleaned up without an error.
3. Hiding does not delete the catalog row, change the external bank, or affect a user-owned row with a colliding URL.
4. After a real playback failure, the user can copy a localized, bounded report containing the defined diagnostic fields and no credentials or local paths.
5. No report is sent or saved remotely, and cancelling or ignoring the action changes nothing.
6. Merge/persistence/redaction tests and localized hide/unhide/report run-and-observe checks pass.

## Risks

URLs can contain credentials or private query values. Redaction must take precedence over diagnostic completeness, and hidden URL identities must remain compatible with the existing user-row-wins merge contract.

## Research

See [competitor improvement backlog](../docs/specifications/competitor-improvement-backlog.md).
