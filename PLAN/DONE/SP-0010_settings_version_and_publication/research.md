# Research: settings, version, and publication contracts

**Date:** 2026-07-19

## Local evidence

- Grid tiles currently use a fixed 320 x 180 design surface and MainWindow owns column calculation, so a persisted size preference can change both dimensions and column density without entering Core UI dependencies.
- `GridPreviewCoordinator` runs a worker plus a 60-second periodic refresh while MainWindow starts and stops it with Grid mode and window activation.
- `CatalogState` already persists view, language, and window preferences; it is the existing settings contract.
- `Directory.Build.props` still uses `0.1.0`, while the MSIX script already derives date-based numeric parts and release workflow still validates semantic tags.
- Existing Store and winget copy predates grid previews, bilingual UI, window controls, settings, and the new version contract.

## External contract evidence

- Microsoft Store MSIX listings require a description and at least one screenshot; descriptions allow 10,000 characters, What’s new allows 1,500, features allow 20 entries of 200 characters, and short descriptions should remain under 270 visible characters: https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/add-and-edit-store-listing-info
- Store metadata must accurately represent implemented behavior; IARC age rating and privacy declarations remain Partner Center responsibilities: https://learn.microsoft.com/en-us/windows/apps/publish/store-policies
- Entertainment is the best primary category for combined audio/video streaming, with Music as a reasonable optional secondary category: https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/categories-and-subcategories
- MSIX Identity requires four-part `Major.Minor.Build.Revision` notation: https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-f-identity
- Current winget authoring documentation uses manifest schema 1.12.0 and permits date-driven package versions: https://learn.microsoft.com/en-us/windows/package-manager/package/manifest
- A winget submission must pass `winget validate` before repository review: https://learn.microsoft.com/en-us/windows/package-manager/winget/validate

## Verified version behavior

- Building .NET with `26.0719.0321` succeeds.
- `AssemblyVersion` normalizes numeric display to `26.719.321.0`, while `FileVersion` and `AssemblyInformationalVersion` retain `26.0719.0321` exactly.
- Therefore Settings reads informational version, release/tag/winget retain the exact three-part form, and MSIX adds `.0`.

## Deferred settings candidates

- Clear thumbnail cache with an explicit size readout and confirmation.
- Preview refresh interval for users on limited networks.
- Reset only presentation settings, separately from destructive catalog deletion.
- Default playback volume after a real volume-control feature exists.
- Update checks only after an explicit network/update policy decision.
