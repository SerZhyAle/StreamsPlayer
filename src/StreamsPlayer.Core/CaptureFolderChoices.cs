namespace StreamsPlayer.Core;

/// <summary>
/// SP-0179: the user's folder for each captured kind (CAPTURE-OUTPUT rule 10). Until SP-0179 one choice,
/// <see cref="CatalogState.FrameFolder"/>, covered frames and both kinds of recording; the contract lets one
/// choice cover several kinds only as the user's option, so the choice is split per kind - once, keeping
/// where a user who had chosen a folder was already getting every file.
/// </summary>
public static class CaptureFolderChoices
{
    public const int CurrentSchema = 1;

    /// <summary>
    /// The state with its single folder choice split per kind. A state already split is returned as it is:
    /// a recording folder the user cleared after the split must stay cleared.
    /// </summary>
    public static CatalogState Split(CatalogState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.CaptureFoldersSchema >= CurrentSchema)
        {
            return state;
        }

        return state with
        {
            VideoRecordingFolder = Blank(state.VideoRecordingFolder) ? Chosen(state.FrameFolder) : state.VideoRecordingFolder,
            AudioRecordingFolder = Blank(state.AudioRecordingFolder) ? Chosen(state.FrameFolder) : state.AudioRecordingFolder,
            CaptureFoldersSchema = CurrentSchema
        };
    }

    /// <summary>The user's folder for <paramref name="kind"/>, or null when they chose none.</summary>
    public static string? For(CatalogState state, CaptureKind kind)
    {
        ArgumentNullException.ThrowIfNull(state);
        return Chosen(kind switch
        {
            CaptureKind.VideoFrame => state.FrameFolder,
            CaptureKind.StreamVideo => state.VideoRecordingFolder,
            CaptureKind.StreamAudio => state.AudioRecordingFolder,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        });
    }

    private static bool Blank(string? folder) => string.IsNullOrWhiteSpace(folder);

    private static string? Chosen(string? folder) => Blank(folder) ? null : folder!.Trim();
}
