using System.Text.Json;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class ExchangeCastTests
{
    [Fact]
    public void Offer_WithSupportedHttpAudioDescriptor_ParsesSuccessfully()
    {
        var json = """
        {
          "schemaVersion": 2,
          "type": "cast-offer",
          "castId": "c123",
          "broadcastId": "b456",
          "fromDeviceId": "d789",
          "fromDeviceName": "Pixel Phone",
          "descriptor": {
            "schemaVersion": 1,
            "url": "http://192.168.1.50:8080/audio.mp3",
            "mode": "AUDIO_ONLY",
            "title": "Living Room Radio",
            "sourceId": "s1",
            "isLive": true,
            "endpoints": [
              {
                "url": "http://192.168.1.50:8080/audio.mp3",
                "transport": "HTTP",
                "mode": "AUDIO_ONLY"
              }
            ]
          }
        }
        """;

        using var doc = JsonDocument.Parse(json);
        Assert.True(ExchangeCastOffer.TryParse(doc.RootElement, out var offer));
        Assert.NotNull(offer);
        Assert.Equal("c123", offer.CastId);
        Assert.Equal("b456", offer.BroadcastId);
        Assert.Equal("d789", offer.DeviceId);
        Assert.Equal("Pixel Phone", offer.DeviceName);
        Assert.Equal("Living Room Radio", offer.Title);
        Assert.NotNull(offer.Descriptor);
        Assert.Equal(ExchangeBroadcastSupport.Supported, offer.Support);
    }

    [Fact]
    public void Offer_WithUnsupportedTransport_ParsesAsUnsupported()
    {
        var json = """
        {
          "schemaVersion": 2,
          "type": "cast-offer",
          "castId": "c123",
          "broadcastId": "b456",
          "fromDeviceId": "d789",
          "fromDeviceName": "Pixel Phone",
          "descriptor": {
            "schemaVersion": 1,
            "url": "p2p://something",
            "mode": "AUDIO_ONLY",
            "title": "Unsupported Stream",
            "isLive": true,
            "endpoints": [
              {
                "url": "p2p://something",
                "transport": "P2P",
                "mode": "AUDIO_ONLY"
              }
            ]
          }
        }
        """;

        using var doc = JsonDocument.Parse(json);
        Assert.True(ExchangeCastOffer.TryParse(doc.RootElement, out var offer));
        Assert.NotNull(offer);
        Assert.Equal(ExchangeBroadcastSupport.Unsupported, offer.Support);
    }

    [Fact]
    public void Offer_WithHigherSchemaVersion_ParsesAsUnsupportedSchema()
    {
        var json = """
        {
          "schemaVersion": 2,
          "type": "cast-offer",
          "offerId": "off123",
          "broadcastId": "b456",
          "descriptor": {
            "schemaVersion": 99,
            "url": "http://192.168.1.50/stream",
            "mode": "audio",
            "title": "Future Stream",
            "isLive": true
          }
        }
        """;

        using var doc = JsonDocument.Parse(json);
        Assert.True(ExchangeCastOffer.TryParse(doc.RootElement, out var offer));
        Assert.NotNull(offer);
        Assert.Equal("off123", offer.CastId);
        Assert.Equal(ExchangeBroadcastSupport.UnsupportedSchema, offer.Support);
    }

    [Fact]
    public void Offer_WithoutCastId_Fails()
    {
        var json = """
        {
          "schemaVersion": 2,
          "type": "cast-offer",
          "broadcastId": "b456"
        }
        """;

        using var doc = JsonDocument.Parse(json);
        Assert.False(ExchangeCastOffer.TryParse(doc.RootElement, out var offer));
        Assert.Null(offer);
    }

    [Fact]
    public void Stop_ParsesSuccessfully()
    {
        var json = """
        {
          "schemaVersion": 2,
          "type": "cast-stop",
          "castId": "c123",
          "broadcastId": "b456"
        }
        """;

        using var doc = JsonDocument.Parse(json);
        Assert.True(ExchangeCastStop.TryParse(doc.RootElement, out var stop));
        Assert.NotNull(stop);
        Assert.Equal("c123", stop.CastId);
        Assert.Equal("b456", stop.BroadcastId);
    }

    [Fact]
    public void Answers_SerializeCorrectProperties()
    {
        var accept = JsonSerializer.Serialize(ExchangeCastAnswer.Accept("c1"));
        Assert.Contains("\"accepted\":true", accept);
        Assert.Contains("\"castId\":\"c1\"", accept);

        var decline = JsonSerializer.Serialize(ExchangeCastAnswer.Decline("c1"));
        Assert.Contains("\"accepted\":false", decline);
        Assert.Contains("\"reason\":\"declined\"", decline);

        var unsupported = JsonSerializer.Serialize(ExchangeCastAnswer.Unsupported("c1"));
        Assert.Contains("\"accepted\":false", unsupported);
        Assert.Contains("\"reason\":\"unsupported\"", unsupported);

        var timeout = JsonSerializer.Serialize(ExchangeCastAnswer.Timeout("c1"));
        Assert.Contains("\"accepted\":false", timeout);
        Assert.Contains("\"reason\":\"timeout\"", timeout);
    }

    [Fact]
    public async Task Coordinator_FoldsRepeatedOffersForSameBroadcast()
    {
        var coordinator = new ExchangeCastCoordinator();
        var promptCalls = 0;
        var promptTcs = new TaskCompletionSource<bool>();

        var offer1 = new ExchangeCastOffer("c1", "b1", "d1", "Phone", "Stream 1", null, ExchangeBroadcastSupport.Supported);
        var offer2 = new ExchangeCastOffer("c2", "b1", "d1", "Phone", "Stream 1", null, ExchangeBroadcastSupport.Supported);

        var task1 = coordinator.RequestDecisionAsync(offer1, async (off, ct) =>
        {
            Interlocked.Increment(ref promptCalls);
            return await promptTcs.Task;
        }, CancellationToken.None);

        // Wait a small moment to ensure task1 entered promptUser
        await Task.Delay(20);

        var task2 = coordinator.RequestDecisionAsync(offer2, (off, ct) =>
        {
            Interlocked.Increment(ref promptCalls);
            return Task.FromResult(true);
        }, CancellationToken.None);

        promptTcs.SetResult(true);

        var res1 = await task1;
        var res2 = await task2;

        Assert.True(res1);
        Assert.True(res2);
        Assert.Equal(1, promptCalls);
    }

    [Fact]
    public async Task Coordinator_QueuesDifferentBroadcastsSequentially()
    {
        var coordinator = new ExchangeCastCoordinator();
        var promptCalls = 0;
        var prompt1Tcs = new TaskCompletionSource<bool>();

        var offer1 = new ExchangeCastOffer("c1", "b1", "d1", "Phone", "Stream 1", null, ExchangeBroadcastSupport.Supported);
        var offer2 = new ExchangeCastOffer("c2", "b2", "d1", "Phone", "Stream 2", null, ExchangeBroadcastSupport.Supported);

        var task1 = coordinator.RequestDecisionAsync(offer1, async (off, ct) =>
        {
            Interlocked.Increment(ref promptCalls);
            return await prompt1Tcs.Task;
        }, CancellationToken.None);

        await Task.Delay(20);

        var task2 = coordinator.RequestDecisionAsync(offer2, (off, ct) =>
        {
            Interlocked.Increment(ref promptCalls);
            return Task.FromResult(false);
        }, CancellationToken.None);

        Assert.Equal(1, promptCalls);

        prompt1Tcs.SetResult(true);

        var res1 = await task1;
        var res2 = await task2;

        Assert.True(res1);
        Assert.False(res2);
        Assert.Equal(2, promptCalls);
    }
}
