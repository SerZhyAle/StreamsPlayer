namespace StreamsPlayer.Core.Tests;

public sealed class PreviewCaptureCooldownTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 6, 20, 48, 0, TimeSpan.Zero);
    private const string DeadUrl = "https://stmv1.transmissaodigital.com/blitstv/blitstv/playlist.m3u8";

    [Fact]
    public void IsSuppressed_UnknownUrl_IsFalse() =>
        Assert.False(new PreviewCaptureCooldown().IsSuppressed(DeadUrl, Start));

    [Fact]
    public void IsSuppressed_AfterAFailure_HoldsForTheCooldownAndThenReleases()
    {
        var cooldown = new PreviewCaptureCooldown();
        cooldown.RecordFailure(DeadUrl, Start);

        // The archive's worst case: the same dead source asked again one second later, and again while
        // the user scrolled back over it minutes afterwards.
        Assert.True(cooldown.IsSuppressed(DeadUrl, Start.AddSeconds(1)));
        Assert.True(cooldown.IsSuppressed(DeadUrl, Start + PreviewCaptureCooldown.Cooldown - TimeSpan.FromSeconds(1)));
        Assert.False(cooldown.IsSuppressed(DeadUrl, Start + PreviewCaptureCooldown.Cooldown));
    }

    [Fact]
    public void IsSuppressed_OnlyTheUrlThatFailed()
    {
        var cooldown = new PreviewCaptureCooldown();
        cooldown.RecordFailure(DeadUrl, Start);

        Assert.False(cooldown.IsSuppressed("https://example.test/live/index.m3u8", Start.AddSeconds(1)));
    }

    [Fact]
    public void Forget_ReleasesImmediately()
    {
        var cooldown = new PreviewCaptureCooldown();
        cooldown.RecordFailure(DeadUrl, Start);

        cooldown.Forget(DeadUrl);

        Assert.False(cooldown.IsSuppressed(DeadUrl, Start.AddSeconds(1)));
    }

    [Fact]
    public void RecordFailure_SweepsEntriesThatCanNoLongerSuppressAnything()
    {
        var cooldown = new PreviewCaptureCooldown();
        for (var i = 0; i < 50; i++)
        {
            cooldown.RecordFailure($"https://example.test/{i}/index.m3u8", Start);
        }

        Assert.Equal(50, cooldown.Count);

        cooldown.RecordFailure(DeadUrl, Start + PreviewCaptureCooldown.Cooldown);

        Assert.Equal(1, cooldown.Count);
        Assert.True(cooldown.IsSuppressed(DeadUrl, Start + PreviewCaptureCooldown.Cooldown));
    }

    [Fact]
    public void Clear_DropsEverything()
    {
        var cooldown = new PreviewCaptureCooldown();
        cooldown.RecordFailure(DeadUrl, Start);

        cooldown.Clear();

        Assert.Equal(0, cooldown.Count);
        Assert.False(cooldown.IsSuppressed(DeadUrl, Start));
    }
}
