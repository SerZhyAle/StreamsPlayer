using System.Text.Json;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class ExchangeCastTests
{
    private const string HttpAudioDescriptor = """
        {
          "schemaVersion": 1,
          "url": "http://192.168.1.50:8080/audio.mp3",
          "mode": "AUDIO_ONLY",
          "title": "Living Room Radio",
          "sourceId": "s1",
          "isLive": true,
          "endpoints": [
            { "url": "http://192.168.1.50:8080/audio.mp3", "transport": "HTTP", "mode": "AUDIO_ONLY" }
          ]
        }
        """;

    private static JsonDocument Offer(string members) => JsonDocument.Parse(
        "{\"schemaVersion\":2,\"type\":\"cast-offer\"," + members + "}");

    [Fact]
    public void Offer_InTheContractShape_ParsesByTheBroadcastRecord()
    {
        // DEVICE-EXCHANGE 7.8: cast-offer carries `broadcast` (the 6.3 record) and `fromDeviceId`, nothing else.
        using var doc = Offer("""
            "fromDeviceId": "d789",
            "broadcast": {
              "broadcastId": "b456",
              "deviceId": "d789",
              "title": "Kitchen",
              "mode": "AUDIO_ONLY",
              "descriptor":
            """ + HttpAudioDescriptor + "}");

        Assert.True(ExchangeCastOffer.TryParse(doc.RootElement, out var offer));
        Assert.NotNull(offer);
        Assert.Equal("b456", offer.BroadcastId);
        Assert.Equal("d789", offer.DeviceId);
        Assert.Null(offer.CastId);
        Assert.Equal("Kitchen", offer.Title);
        Assert.NotNull(offer.Descriptor);
        Assert.Equal(ExchangeBroadcastSupport.Supported, offer.Support);
    }

    [Fact]
    public void Offer_WithoutTheRecordsTitle_TakesTheDescriptorsTitle()
    {
        using var doc = Offer("""
            "fromDeviceId": "d789",
            "broadcast": { "broadcastId": "b456", "deviceId": "d789", "mode": "AUDIO_ONLY", "descriptor":
            """ + HttpAudioDescriptor + "}");

        Assert.True(ExchangeCastOffer.TryParse(doc.RootElement, out var offer));
        Assert.Equal("Living Room Radio", offer!.Title);
    }

    [Theory]
    [InlineData("castId")]
    [InlineData("offerId")]
    public void Offer_WithALegacyCastIdExtra_KeepsItOnlyAsAnOptionalMember(string member)
    {
        using var doc = Offer($$"""
            "{{member}}": "c123",
            "fromDeviceId": "d789",
            "broadcast": { "broadcastId": "b456", "deviceId": "d789", "mode": "AUDIO_ONLY", "descriptor":
            """ + HttpAudioDescriptor + "}");

        Assert.True(ExchangeCastOffer.TryParse(doc.RootElement, out var offer));
        Assert.Equal("c123", offer!.CastId);
        Assert.Equal("b456", offer.BroadcastId);
    }

    [Fact]
    public void Offer_WithUnsupportedTransport_ParsesAsUnsupported()
    {
        using var doc = Offer("""
            "fromDeviceId": "d789",
            "broadcast": {
              "broadcastId": "b456", "deviceId": "d789", "mode": "AUDIO_ONLY",
              "descriptor": {
                "schemaVersion": 1,
                "url": "p2p://something",
                "mode": "AUDIO_ONLY",
                "title": "Unsupported Stream",
                "isLive": true,
                "endpoints": [ { "url": "p2p://something", "transport": "P2P", "mode": "AUDIO_ONLY" } ]
              }
            }
            """);

        Assert.True(ExchangeCastOffer.TryParse(doc.RootElement, out var offer));
        Assert.Equal(ExchangeBroadcastSupport.Unsupported, offer!.Support);
    }

    [Fact]
    public void Offer_WithHigherSchemaVersion_ParsesAsUnsupportedSchema()
    {
        using var doc = Offer("""
            "fromDeviceId": "d789",
            "broadcast": {
              "broadcastId": "b456", "deviceId": "d789", "mode": "AUDIO_ONLY",
              "descriptor": {
                "schemaVersion": 99,
                "url": "http://192.168.1.50/stream",
                "mode": "AUDIO_ONLY",
                "title": "Future Stream",
                "isLive": true
              }
            }
            """);

        Assert.True(ExchangeCastOffer.TryParse(doc.RootElement, out var offer));
        Assert.Equal(ExchangeBroadcastSupport.UnsupportedSchema, offer!.Support);
    }

    [Fact]
    public void Offer_WhoseRecordHasNoDescriptor_IsUnsupportedAndStillAnswerable()
    {
        using var doc = Offer("""
            "fromDeviceId": "d789",
            "broadcast": { "broadcastId": "b456", "deviceId": "d789", "mode": "AUDIO_ONLY" }
            """);

        Assert.True(ExchangeCastOffer.TryParse(doc.RootElement, out var offer));
        Assert.Equal("b456", offer!.BroadcastId);
        Assert.Null(offer.Descriptor);
        Assert.Equal(ExchangeBroadcastSupport.Unsupported, offer.Support);
    }

    [Theory]
    [InlineData("\"castId\": \"c1\", \"broadcastId\": \"b456\"")]
    [InlineData("\"fromDeviceId\": \"d789\", \"broadcast\": { \"deviceId\": \"d789\", \"mode\": \"AUDIO_ONLY\" }")]
    [InlineData("\"fromDeviceId\": \"d789\", \"broadcast\": { \"broadcastId\": \" \", \"mode\": \"AUDIO_ONLY\" }")]
    [InlineData("\"fromDeviceId\": \"d789\", \"broadcast\": \"b456\"")]
    public void Offer_WithoutABroadcastRecordCarryingAnId_Fails(string members)
    {
        using var doc = Offer(members);

        Assert.False(ExchangeCastOffer.TryParse(doc.RootElement, out var offer));
        Assert.Null(offer);
    }

    [Fact]
    public void Stop_ParsesSuccessfully()
    {
        using var doc = JsonDocument.Parse("""
            { "schemaVersion": 2, "type": "cast-stop", "castId": "c123", "broadcastId": "b456" }
            """);

        Assert.True(ExchangeCastStop.TryParse(doc.RootElement, out var stop));
        Assert.NotNull(stop);
        Assert.Equal("c123", stop.CastId);
        Assert.Equal("b456", stop.BroadcastId);
    }

    [Fact]
    public void Stop_WithOnlyTheBroadcastId_ParsesWithoutACastId()
    {
        using var doc = JsonDocument.Parse("""
            { "schemaVersion": 2, "type": "cast-stop", "broadcastId": "b456", "receiverDeviceId": "r1" }
            """);

        Assert.True(ExchangeCastStop.TryParse(doc.RootElement, out var stop));
        Assert.Null(stop!.CastId);
        Assert.Equal("b456", stop.BroadcastId);
    }

    [Fact]
    public void Answers_AreKeyedByBroadcastIdAndCarryNoCastIdByDefault()
    {
        // DEVICE-EXCHANGE 7.8: the answer is `broadcastId`, `accepted`, `reason`.
        var accept = JsonSerializer.Serialize(ExchangeCastAnswer.Accept("b1"));
        Assert.Contains("\"type\":\"cast-answer\"", accept);
        Assert.Contains("\"broadcastId\":\"b1\"", accept);
        Assert.Contains("\"accepted\":true", accept);
        Assert.DoesNotContain("\"castId\"", accept);

        var decline = JsonSerializer.Serialize(ExchangeCastAnswer.Decline("b1"));
        Assert.Contains("\"broadcastId\":\"b1\"", decline);
        Assert.Contains("\"accepted\":false", decline);
        Assert.Contains("\"reason\":\"declined\"", decline);
        Assert.DoesNotContain("\"castId\"", decline);

        var unsupported = JsonSerializer.Serialize(ExchangeCastAnswer.Unsupported("b1"));
        Assert.Contains("\"accepted\":false", unsupported);
        Assert.Contains("\"reason\":\"unsupported\"", unsupported);

        var timeout = JsonSerializer.Serialize(ExchangeCastAnswer.Timeout("b1"));
        Assert.Contains("\"accepted\":false", timeout);
        Assert.Contains("\"reason\":\"timeout\"", timeout);
    }

    [Fact]
    public void Answers_EchoALegacyCastIdWhenTheOfferCarriedOne()
    {
        var accept = JsonSerializer.Serialize(ExchangeCastAnswer.Accept("b1", "c9"));

        Assert.Contains("\"broadcastId\":\"b1\"", accept);
        Assert.Contains("\"castId\":\"c9\"", accept);
    }

    [Theory]
    [InlineData(ExchangeCastDecision.Accepted, true, null)]
    [InlineData(ExchangeCastDecision.Declined, false, "declined")]
    [InlineData(ExchangeCastDecision.TimedOut, false, "timeout")]
    public void AnswerFor_MapsTheDecisionToTheContractsReason(ExchangeCastDecision decision, bool accepted, string? reason)
    {
        var offer = new ExchangeCastOffer("b1", "d1", "Phone", "Stream", null, ExchangeBroadcastSupport.Supported);

        using var doc = JsonSerializer.SerializeToDocument(ExchangeCastAnswer.For(offer, decision));

        Assert.Equal("b1", ExchangeProtocol.String(doc.RootElement, "broadcastId"));
        Assert.Equal(accepted, doc.RootElement.GetProperty("accepted").GetBoolean());
        Assert.Equal(reason, ExchangeProtocol.String(doc.RootElement, "reason"));
    }

    private static ExchangeCastOffer Offered(string castId, string broadcastId) =>
        new(broadcastId, "d1", "Phone", "Stream " + broadcastId, null, ExchangeBroadcastSupport.Supported, castId);

    [Fact]
    public async Task Coordinator_FoldsRepeatedOffersForSameBroadcast()
    {
        var coordinator = new ExchangeCastCoordinator();
        var promptCalls = 0;
        var promptTcs = new TaskCompletionSource<bool>();

        var task1 = coordinator.RequestDecisionAsync(Offered("c1", "b1"), async (_, _) =>
        {
            Interlocked.Increment(ref promptCalls);
            return await promptTcs.Task;
        }, CancellationToken.None);

        await Until(() => Volatile.Read(ref promptCalls) == 1);

        var task2 = coordinator.RequestDecisionAsync(Offered("c2", "b1"), (_, _) =>
        {
            Interlocked.Increment(ref promptCalls);
            return Task.FromResult(true);
        }, CancellationToken.None);

        promptTcs.SetResult(true);

        Assert.Equal(ExchangeCastDecision.Accepted, await task1);
        Assert.Equal(ExchangeCastDecision.Accepted, await task2);
        Assert.Equal(1, promptCalls);
    }

    [Fact]
    public async Task Coordinator_QueuesDifferentBroadcastsSequentially()
    {
        var coordinator = new ExchangeCastCoordinator();
        var promptCalls = 0;
        var prompt1Tcs = new TaskCompletionSource<bool>();

        var task1 = coordinator.RequestDecisionAsync(Offered("c1", "b1"), async (_, _) =>
        {
            Interlocked.Increment(ref promptCalls);
            return await prompt1Tcs.Task;
        }, CancellationToken.None);

        await Until(() => Volatile.Read(ref promptCalls) == 1);

        var task2 = coordinator.RequestDecisionAsync(Offered("c2", "b2"), (_, _) =>
        {
            Interlocked.Increment(ref promptCalls);
            return Task.FromResult(false);
        }, CancellationToken.None);

        await Task.Delay(50);
        Assert.Equal(1, Volatile.Read(ref promptCalls));

        prompt1Tcs.SetResult(true);

        Assert.Equal(ExchangeCastDecision.Accepted, await task1);
        Assert.Equal(ExchangeCastDecision.Declined, await task2);
        Assert.Equal(2, promptCalls);
    }

    [Fact]
    public async Task Coordinator_APromptThatNeverAnswers_TimesOutAndIsToldToDismiss()
    {
        // expected: the 60 s window of DEVICE-EXCHANGE item R (here shortened) ends the question as TimedOut.
        var coordinator = new ExchangeCastCoordinator(TimeSpan.FromMilliseconds(150));
        CancellationToken promptToken = default;

        var decision = await coordinator.RequestDecisionAsync(Offered("c1", "b1"), (_, token) =>
        {
            promptToken = token;
            return new TaskCompletionSource<bool>().Task;
        }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ExchangeCastDecision.TimedOut, decision);
        Assert.True(promptToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Coordinator_APromptThatHonoursTheToken_AlsoReadsAsTimedOut()
    {
        var coordinator = new ExchangeCastCoordinator(TimeSpan.FromMilliseconds(150));

        var decision = await coordinator.RequestDecisionAsync(Offered("c1", "b1"),
            async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return true;
            }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ExchangeCastDecision.TimedOut, decision);
    }

    [Fact]
    public async Task Coordinator_AnOfferBehindAnAbandonedPrompt_IsNotStuckWaitingForIt()
    {
        var coordinator = new ExchangeCastCoordinator(TimeSpan.FromMilliseconds(150));
        var neverAnswers = (ExchangeCastOffer _, CancellationToken __) => new TaskCompletionSource<bool>().Task;

        var first = coordinator.RequestDecisionAsync(Offered("c1", "b1"), neverAnswers, CancellationToken.None);
        var second = coordinator.RequestDecisionAsync(Offered("c2", "b2"), neverAnswers, CancellationToken.None);

        Assert.Equal(ExchangeCastDecision.TimedOut, await first.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(ExchangeCastDecision.TimedOut, await second.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Coordinator_AnAnswerInsideTheWindow_IsNotATimeout()
    {
        var coordinator = new ExchangeCastCoordinator(TimeSpan.FromSeconds(30));

        var decision = await coordinator.RequestDecisionAsync(Offered("c1", "b1"),
            (_, _) => Task.FromResult(true), CancellationToken.None);

        Assert.Equal(ExchangeCastDecision.Accepted, decision);
    }

    [Fact]
    public async Task Coordinator_TheSessionEnding_ReadsAsDeclinedAndDoesNotThrow()
    {
        var coordinator = new ExchangeCastCoordinator();
        using var session = new CancellationTokenSource();
        var promptOpen = new TaskCompletionSource();

        var pending = coordinator.RequestDecisionAsync(Offered("c1", "b1"), async (_, token) =>
        {
            promptOpen.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return true;
        }, session.Token);
        await promptOpen.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await session.CancelAsync();

        Assert.Equal(ExchangeCastDecision.Declined, await pending.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Coordinator_CancelAll_AnswersFoldedAndQueuedOffersDeclined()
    {
        var coordinator = new ExchangeCastCoordinator();
        var promptOpen = new TaskCompletionSource();
        var release = new TaskCompletionSource<bool>();

        var primary = coordinator.RequestDecisionAsync(Offered("c1", "b1"), async (_, _) =>
        {
            promptOpen.SetResult();
            return await release.Task;
        }, CancellationToken.None);
        await promptOpen.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var folded = coordinator.RequestDecisionAsync(Offered("c2", "b1"), (_, _) => Task.FromResult(true), CancellationToken.None);

        coordinator.CancelAll();

        Assert.Equal(ExchangeCastDecision.Declined, await folded.WaitAsync(TimeSpan.FromSeconds(10)));
        release.SetResult(true);
        await primary.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(10, deadline.Token);
        }
    }
}
