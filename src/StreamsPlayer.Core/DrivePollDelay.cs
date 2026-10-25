namespace StreamsPlayer.Core;

public sealed class DrivePollDelay
{
    private int _failures;
    public TimeSpan After(bool succeeded, double jitter)
    {
        if (succeeded) { _failures = 0; return TimeSpan.FromSeconds(10); }
        var cap = Math.Min(300, Math.Pow(2, Math.Min(_failures++, 9)));
        return TimeSpan.FromSeconds(cap * (0.5 + Math.Clamp(jitter, 0, 1) * 0.5));
    }
}
