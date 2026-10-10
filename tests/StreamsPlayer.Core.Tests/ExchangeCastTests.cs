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

    [Fact]
    public async Task Coordinator_APromptAbandonedByItsWindow_DoesNotTimeOutALaterRepeatOfTheBroadcast()
    {
        // expected: the repeat, which was never asked and whose own window is still open, is asked itself
        // and answered by the user | actual (before): it inherited the leader's TimedOut.
        var coordinator = new ExchangeCastCoordinator(TimeSpan.FromMilliseconds(600));
        var leaderAsked = new TaskCompletionSource();
        Task<bool> Prompt(ExchangeCastOffer offer, CancellationToken token)
        {
            if (offer.CastId == "leader")
            {
                leaderAsked.TrySetResult();
                return new TaskCompletionSource<bool>().Task;
            }

            return Task.FromResult(true);
        }

        var leader = coordinator.RequestDecisionAsync(Offered("leader", "b1"), Prompt, CancellationToken.None);
        await leaderAsked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(300);
        var repeat = coordinator.RequestDecisionAsync(Offered("repeat", "b1"), Prompt, CancellationToken.None);

        Assert.Equal(ExchangeCastDecision.TimedOut, await leader.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(ExchangeCastDecision.Accepted, await repeat.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Coordinator_ALeaderThatExpiresInTheQueue_DoesNotTimeOutALaterRepeat()
    {
        var coordinator = new ExchangeCastCoordinator(TimeSpan.FromMilliseconds(600));
        var blockerAsked = new TaskCompletionSource();
        Task<bool> Prompt(ExchangeCastOffer offer, CancellationToken token)
        {
            if (offer.BroadcastId == "b0")
            {
                blockerAsked.TrySetResult();
                return new TaskCompletionSource<bool>().Task;
            }

            return offer.CastId == "leader" ? new TaskCompletionSource<bool>().Task : Task.FromResult(true);
        }

        var blocker = coordinator.RequestDecisionAsync(Offered("blocker", "b0"), Prompt, CancellationToken.None);
        await blockerAsked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var leader = coordinator.RequestDecisionAsync(Offered("leader", "b1"), Prompt, CancellationToken.None);
        await Task.Delay(300);
        var repeat = coordinator.RequestDecisionAsync(Offered("repeat", "b1"), Prompt, CancellationToken.None);

        Assert.Equal(ExchangeCastDecision.TimedOut, await blocker.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(ExchangeCastDecision.TimedOut, await leader.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(ExchangeCastDecision.Accepted, await repeat.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Coordinator_ARepeatAfterTheUserAnswered_SharesThatAnswer()
    {
        var coordinator = new ExchangeCastCoordinator();
        var asked = new TaskCompletionSource();
        var answer = new TaskCompletionSource<bool>();
        var calls = 0;
        Task<bool> Prompt(ExchangeCastOffer offer, CancellationToken token)
        {
            Interlocked.Increment(ref calls);
            asked.TrySetResult();
            return answer.Task;
        }

        var leader = coordinator.RequestDecisionAsync(Offered("leader", "b1"), Prompt, CancellationToken.None);
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var repeat = coordinator.RequestDecisionAsync(Offered("repeat", "b1"), Prompt, CancellationToken.None);
        answer.SetResult(false);

        Assert.Equal(ExchangeCastDecision.Declined, await leader.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(ExchangeCastDecision.Declined, await repeat.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public void Ledger_AFullList_GivesUpOnlyItsOldestCast()
    {
        // expected: the 17th cast evicts the 1st and the live ones stay stoppable | actual (before): the
        // whole list was cleared, so the cast that was playing could no longer be stopped.
        var ledger = new ExchangeCastLedger(capacity: 4);
        var casts = Enumerable.Range(0, 5)
            .Select(index => ledger.Track("b" + index, null, _ => true))
            .ToList();

        Assert.Equal(4, ledger.Count);
        Assert.Null(ledger.Stop(new ExchangeCastStop(null, "b0")));
        Assert.Same(casts[4], ledger.Stop(new ExchangeCastStop(null, "b4")));
        Assert.Same(casts[1], ledger.Stop(new ExchangeCastStop(null, "b1")));
    }

    [Fact]
    public void Ledger_DropsCastsWhosePlaybackEnded_BeforeItEvictsALiveOne()
    {
        var ledger = new ExchangeCastLedger(capacity: 2);
        var ended = ledger.Track("b-ended", null, _ => true);
        ended.ChannelId = Guid.NewGuid();
        ended.Settled = true;
        var live = ledger.Track("b-live", null, _ => true);
        live.ChannelId = Guid.NewGuid();
        live.Settled = true;

        var added = ledger.Track("b-new", null, channel => channel == live.ChannelId);

        Assert.Equal(2, ledger.Count);
        Assert.Null(ledger.Stop(new ExchangeCastStop(null, "b-ended")));
        Assert.Same(live, ledger.Stop(new ExchangeCastStop(null, "b-live")));
        Assert.NotNull(added);
    }

    [Fact]
    public void Ledger_AStopDuringTheImport_FindsTheCastAndFlagsItBeforeAnyChannelExists()
    {
        // expected: the entry exists as soon as the offer is accepted, so a stop during the whole-state save is
        // not dropped and the continuation can see it | actual (before): no entry until the save returned.
        var ledger = new ExchangeCastLedger();
        var cast = ledger.Track("b1", "c1", _ => true);
        Assert.Null(cast.ChannelId);

        var stopped = ledger.Stop(new ExchangeCastStop("c1", null));

        Assert.Same(cast, stopped);
        Assert.True(cast.StopRequested);
        Assert.Equal(0, ledger.Count);
        Assert.Null(ledger.Stop(new ExchangeCastStop(null, "b1")));
    }

    [Fact]
    public void Ledger_ARepeatedOffer_IsTheSameCastAndAStopReachesIt()
    {
        var ledger = new ExchangeCastLedger();
        var first = ledger.Track("b1", "c1", _ => true);
        var again = ledger.Track("b1", null, _ => true);

        Assert.Same(first, again);
        Assert.Equal("c1", again.CastId);
        Assert.Equal(1, ledger.Count);
    }

    [Fact]
    public void Ledger_AStopForACastThisDeviceDidNotStart_IsNotActedOn()
    {
        var ledger = new ExchangeCastLedger();
        ledger.Track("b1", null, _ => true);

        Assert.Null(ledger.Stop(new ExchangeCastStop(null, "b-other")));
        Assert.Null(ledger.Stop(new ExchangeCastStop("c-other", null)));
        Assert.Null(ledger.Stop(new ExchangeCastStop(null, null)));
        Assert.Equal(1, ledger.Count);
    }

    [Fact]
    public void Offer_WithAnIdOverTheBound_IsNotParsed()
    {
        var longId = new string('x', ExchangeCastLimits.MaximumIdLength + 1);
        foreach (var members in new[]
                 {
                     $$"""{ "fromDeviceId": "d1", "broadcast": { "broadcastId": "{{longId}}", "deviceId": "d1" } }""",
                     $$"""{ "fromDeviceId": "d1", "castId": "{{longId}}", "broadcast": { "broadcastId": "b1", "deviceId": "d1" } }""",
                     $$"""{ "fromDeviceId": "d1", "offerId": "{{longId}}", "broadcast": { "broadcastId": "b1", "deviceId": "d1" } }""",
                     $$"""{ "fromDeviceId": "{{longId}}", "broadcast": { "broadcastId": "b1" } }"""
                 })
        {
            using var doc = Offer(members.Trim('{', '}'));
            Assert.False(ExchangeCastOffer.TryParse(doc.RootElement, out var offer));
            Assert.Null(offer);
        }
    }

    [Fact]
    public void Offer_AtTheIdBound_StillParsesAndItsAnswerStaysFarInsideTheFrameLimit()
    {
        var id = new string('x', ExchangeCastLimits.MaximumIdLength);
        using var doc = Offer($$"""
            "castId": "{{id}}", "fromDeviceId": "d1",
            "broadcast": { "broadcastId": "{{id}}", "deviceId": "d1", "mode": "AUDIO_ONLY" }
            """);

        Assert.True(ExchangeCastOffer.TryParse(doc.RootElement, out var offer));
        var answer = JsonSerializer.SerializeToUtf8Bytes(ExchangeCastAnswer.For(offer!, ExchangeCastDecision.Declined));
        Assert.True(answer.Length < 1024, "answer is " + answer.Length + " bytes");
    }

    [Fact]
    public void Offer_WhoseRecordIsOverTheCeiling_IsAnsweredUnsupportedAndReadsNothingFromIt()
    {
        // expected: an oversize record is skipped as DEVICE-EXCHANGE 6 says, yet the offer stays answerable |
        // actual (before): the whole record - title and descriptor included - was read and shown.
        var padding = new string('p', ExchangeDirectoryState.MaximumRecordBytes);
        using var doc = Offer($$"""
            "fromDeviceId": "d1",
            "broadcast": { "broadcastId": "b-big", "deviceId": "d1", "title": "Never shown", "padding": "{{padding}}",
              "mode": "AUDIO_ONLY", "descriptor":
            """ + HttpAudioDescriptor + "}");

        Assert.True(ExchangeCastOffer.TryParse(doc.RootElement, out var offer));
        Assert.Equal("b-big", offer!.BroadcastId);
        Assert.Null(offer.Descriptor);
        Assert.Equal(ExchangeBroadcastSupport.Unsupported, offer.Support);
        Assert.Equal("Live Broadcast", offer.Title);
    }

    [Fact]
    public void Offer_WithAnOverlongDeviceName_ShowsNoNameInsteadOfAHugeOne()
    {
        var name = new string('n', ExchangeCastLimits.MaximumDeviceNameLength + 1);
        using var doc = Offer($$"""
            "fromDeviceId": "d1", "fromDeviceName": "{{name}}",
            "broadcast": { "broadcastId": "b1", "deviceId": "d1", "mode": "AUDIO_ONLY" }
            """);

        Assert.True(ExchangeCastOffer.TryParse(doc.RootElement, out var offer));
        Assert.Null(offer!.DeviceName);
    }

    [Fact]
    public void Stop_WithAnIdOverTheBound_IsNotParsed()
    {
        var longId = new string('x', ExchangeCastLimits.MaximumIdLength + 1);
        using var doc = JsonDocument.Parse($$"""{ "schemaVersion": 2, "type": "cast-stop", "broadcastId": "{{longId}}" }""");

        Assert.False(ExchangeCastStop.TryParse(doc.RootElement, out var stop));
        Assert.Null(stop);
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
