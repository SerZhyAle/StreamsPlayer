namespace StreamsPlayer.App;

public static class StreamTitleFormatter
{
    public static string Display(string title)
    {
        var trimmed = title.Trim();
        if (!trimmed.EndsWith(')'))
        {
            return trimmed;
        }

        var open = trimmed.LastIndexOf(" (", StringComparison.Ordinal);
        if (open <= 0)
        {
            return trimmed;
        }

        var baseTitle = trimmed[..open].Trim();
        var parenthetical = trimmed[(open + 2)..^1].Trim();
        return baseTitle.Equals(parenthetical, StringComparison.OrdinalIgnoreCase) ? baseTitle : trimmed;
    }
}
