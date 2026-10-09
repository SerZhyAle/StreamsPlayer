namespace StreamsPlayer.Core;

/// <summary>
/// SP-0191: makes a freshly read <see cref="SettingsUiState"/> safe to index.
/// </summary>
/// <remarks>
/// The record declares its dictionaries non-null, but the file is a user-writable document and
/// <c>System.Text.Json</c> assigns a literal <c>null</c> (<c>"groups": null</c>, or an entry whose value is
/// <c>null</c>) without complaint. The window only ever treats this document as a convenience - a damaged
/// part costs that part's memory, never the window - so what cannot be used is dropped here, once, and
/// every reader downstream can index the dictionaries without a guard.
/// </remarks>
public static class SettingsUiStateSanitizer
{
    public static SettingsUiState Sanitize(SettingsUiState state) =>
        state with
        {
            // Compared against null although the declared type forbids it: that is the whole point.
            Groups = state.Groups is null ? [] : new Dictionary<string, bool>(state.Groups),
            Viewports = state.Viewports is null
                ? []
                : state.Viewports
                    .Where(entry => entry.Value is { GroupId.Length: > 0 })
                    .ToDictionary(entry => entry.Key, entry => entry.Value)
        };
}
