using System.Text.Json;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0228 definition of done item 3, the account-lifecycle transcripts of the FMS_Win server: DEVICE-EXCHANGE items AO
/// (account kinds), AP to AQ (registration), AT (password change), AW (the pace of sign-in checks) and AX to AZ, as the
/// conformance paragraph of 0.28 names them. It replays `vectors/fmsw-224/frames-v2.json`, vendored byte for byte
/// (sha256 f5c6ae0db7dd47454467184c5df3378104e35bfe09609acfd37f703e833541d8, contract 0.28, rung 2), through the product's
/// own wire code: the `registration`, `password` and `pace` transcripts, and the seven transcripts it shares with
/// server-frames-v2.json in their 0.28 form (`accountKind` on `enrolled` and `welcome`, the added refusals). The ten shared
/// transcripts also stand in fmsw-234/frames-v2.json, whose sealed assertions ExchangeSealedDirectoryTests holds; they are
/// not repeated here. This product signs in and never registers, so `register` and `account-delete` are accounted for and not
/// produced. The contract adopts the file as evidence for the server; no joint run is claimed.
/// </summary>
public sealed class ExchangeAccountLifecycleReplayTests
{
    private const string FramesSha256 = "f5c6ae0db7dd47454467184c5df3378104e35bfe09609acfd37f703e833541d8";

    private static readonly FramesCorpus Corpus = ExchangeFramesCorpus.Load(ExchangeFramesCorpus.AccountFrames);
    private static readonly FramesCorpus ServerCorpus = ExchangeFramesCorpus.Load(ExchangeFramesCorpus.ServerFrames);

    private static IEnumerable<Step> ServerFrames(string transcript, string type) =>
        Corpus.Named(transcript).Steps.Where(step => step.ServerToClient && step.IsFrame && step.Type == type);

    // ---------------------------------------------------------------- the file

    [Fact]
    public void VendoredFile_IsTheCatalogsBytes_AndItsProvenanceNamesTheSameHash()
    {
        Assert.Equal(FramesSha256, ExchangeFramesCorpus.Sha256Of(ExchangeFramesCorpus.AccountFrames));
        Assert.Contains(FramesSha256, File.ReadAllText(ExchangeFramesCorpus.PathOf("fmsw-224/PROVENANCE.txt")));
    }

    [Fact]
    public void Corpus_ShapeIsPinned_AndItsSharedTranscriptsKeepTheSameChunkListAndConstants()
    {
        Assert.Equal("0.28", Corpus.ContractVersion);
        Assert.Equal(2, Corpus.Rung);
        Assert.Equal(["enroll-and-hello", "pairing", "refusals", "directory", "tunnels", "relay", "cast", "registration", "password", "pace"],
            Corpus.Transcripts.Select(transcript => transcript.Name));
        Assert.Equal(249, Corpus.Steps.Count());
        Assert.Equal(217, Corpus.Frames.Count());
        Assert.Equal(111, Corpus.Frames.Count(step => step.ClientToServer));
        Assert.Equal(106, Corpus.Frames.Count(step => step.ServerToClient));
        Assert.Equal(22, Corpus.Steps.Count(step => step.Closed));
        Assert.Equal(ServerCorpus.Constants, Corpus.Constants);
        Assert.Equal(ServerCorpus.Chunks.Select(item => (item.Name, Convert.ToHexString(item.Frame), item.Verdict)),
            Corpus.Chunks.Select(item => (item.Name, Convert.ToHexString(item.Frame), item.Verdict)));
        Assert.Equal(ExchangeProtocol.OpeningLimit, Corpus.Constants.MaxFrame);
        Assert.Equal(ExchangeProtocol.AuthenticatedLimit, Corpus.Constants.MaxFrameAuth);
    }

    // ---------------------------------------------------------------- the whole file through the shared replay

    [Fact]
    public async Task EveryFrame_HasItsLengthPrefix_FitsItsLimit_AndIsDecodedByTheProductsReader()
    {
        Assert.Empty(await ExchangeFramesReplay.FramingAndDecodeFailuresAsync(Corpus));
    }

    [Fact]
    public async Task TheFirstFrameOfEveryOpenedStream_PassesTheProductsHandshake()
    {
        var (failures, streams) = await ExchangeFramesReplay.OpeningReplayAsync(Corpus);
        Assert.Empty(failures);
        // The 26 streams of the shared transcripts, the six of `password` and the four of `pace`. The nine streams of
        // `registration` open with `register`, a frame this product does not produce, so the check does not open them.
        Assert.Equal(26 + 6 + 4, streams);
    }

    [Fact]
    public void EveryServerFrame_IsReadAsTheFileSaysItIs()
    {
        Assert.Empty(ExchangeFramesReplay.ReadingFailures(Corpus));
        var types = Corpus.Frames.Where(step => step.ServerToClient).GroupBy(step => step.Type!)
            .ToDictionary(group => group.Key, group => group.Count());
        Assert.Equal(new Dictionary<string, int>
        {
            ["account-deleted"] = 1, ["broadcast-started"] = 3, ["cast-offer"] = 4, ["cast-result"] = 5, ["cast-stop"] = 2,
            ["changed"] = 4, ["connected"] = 3, ["directory"] = 2, ["enrolled"] = 21, ["keepalive"] = 13, ["open"] = 3,
            ["pairing-code"] = 2, ["password-changed"] = 1, ["published"] = 4, ["refused"] = 28, ["upload-accepted"] = 1,
            ["welcome"] = 9
        }, types);
    }

    [Fact]
    public void Directory_ReplaysTheSameAsInTheServerCorpus()
    {
        var (failures, tally) = ExchangeFramesReplay.DirectoryReplay(Corpus);
        Assert.Empty(failures);
        Assert.Equal(new DirectoryTally(Fulls: 2, ChangesApplied: 1, Relists: 3, BroadcastRecords: 0), tally);
    }

    [Fact]
    public async Task CastAnswersAndUploadChunks_AreByteForByteWhatTheProductWrites()
    {
        Assert.Empty(await ExchangeFramesReplay.CastAnswerFailuresAsync(Corpus));
        Assert.Empty(await ExchangeFramesReplay.UploadChunkFailuresAsync(Corpus));
    }

    [Fact]
    public async Task Enroll_CarriesEveryMemberTheCorpusEnrollsDo_AndTheCorpusEnrollsAreInsideItemAM()
    {
        using var withPassword = await ExchangeFramesReplay.ProductEnrollAsync(pairingCode: false);
        using var withPairingCode = await ExchangeFramesReplay.ProductEnrollAsync(pairingCode: true);
        Assert.Empty(ExchangeFramesReplay.EnrollShapeFailures(Corpus, withPassword.RootElement, withPairingCode.RootElement));
        // 16 in the seven shared transcripts (the rate-limited sign-in of 0.17 is a `hello` in 0.28), 3 in `password`, 4 in `pace`.
        Assert.Equal(23, Corpus.Frames.Count(step => step.ClientToServer && step.Type == "enroll"));
    }

    // ---------------------------------------------------------------- account kinds (item AO)

    [Fact]
    public void EnrolledAndWelcome_NameTheirAccountKind_AndOnlyTheVisitorIsAGuest()
    {
        var frames = Corpus.Frames.Where(step => step.ServerToClient && step.Type is "enrolled" or "welcome").ToArray();
        var kinds = frames.Select(step =>
        {
            using var document = step.Parse();
            return (step.Label, Named: ExchangeProtocol.String(document.RootElement, "accountKind"),
                Kind: ExchangeAccountKinds.Parse(ExchangeProtocol.String(document.RootElement, "accountKind")));
        }).ToArray();
        Assert.Equal(30, kinds.Length);
        Assert.All(kinds, item => Assert.NotNull(item.Named));
        Assert.Equal(["registration#18(guest)"], kinds.Where(item => item.Kind == ExchangeAccountKind.Guest).Select(item => item.Label));
        Assert.Equal("guest", kinds.Single(item => item.Kind == ExchangeAccountKind.Guest).Named);
    }

    // ---------------------------------------------------------------- registration (this product never registers)

    [Fact]
    public void Registration_TheFourEnrolledAreMemberMemberGuestMember_WithTheirOwnTokens()
    {
        var enrolled = ServerFrames("registration", "enrolled").ToArray();
        Assert.Equal(["member", "guest", "member", "member"], enrolled.Select(step =>
        {
            using var document = step.Parse();
            return ExchangeAccountKinds.Parse(ExchangeProtocol.String(document.RootElement, "accountKind")).ToString().ToLowerInvariant();
        }));
        var tokens = enrolled.Select(step =>
        {
            using var document = step.Parse();
            return ExchangeProtocol.String(document.RootElement, "deviceToken")!;
        }).ToArray();
        Assert.Equal(4, tokens.Distinct().Count());
        Assert.All(tokens, token => Assert.True(ExchangeFramesReplay.IsDeviceToken(token), token));
    }

    [Fact]
    public void Registration_TheSevenRefusals_ShowTheirRows_AndTheGuestOnesNameNoRequest()
    {
        var reasons = ServerFrames("registration", "refused").Select(step =>
        {
            using var document = step.Parse();
            return ExchangeProtocol.String(document.RootElement, "reason")!;
        }).ToArray();
        Assert.Equal(["invitation-invalid", "login-taken", "password-policy", "invitation-invalid", "not-permitted", "not-permitted",
            "limit-reached"], reasons);
        Assert.Equal(["ExchangeRefused", "ExchangeRefused", "ExchangePasswordPolicy", "ExchangeRefused", "ExchangeNotPermitted",
            "ExchangeNotPermitted", "ExchangeLimitReached"], reasons.Select(reason => ExchangeProtocol.RefusalKey(reason)));

        // `invitation-invalid`, `login-taken` and `protection-unsupported` are the generic refusal by design: the product never
        // registers (SP-0221) and publishes no protected resource, and section 7.10 shows an unknown reason as a generic one.
        foreach (var reason in new[] { "invitation-invalid", "login-taken", "protection-unsupported" })
        {
            Assert.Same(ExchangeFailureRows.Refused, ExchangeFailureRows.OfWire(reason));
        }

        // Each refusal answers one request and names none: `reason` is its only member, which is also what lets the guest
        // refusals (steps 20 and 22, the stream stays open) reach the product with no broadcastId.
        Assert.Empty(ServerFrames("registration", "refused").Select(step => ExchangeFramesReplay.RefusalFailure(step, ExchangeFramesReplay.RefusalRows))
            .OfType<string>());
    }

    [Fact]
    public void RegisterAndAccountDelete_AreNotInTheProductsVocabulary_AndNoSourceLiteralNamesThem()
    {
        // Sign-in only (SP-0221): the nine `register` frames and the two `account-delete` frames are the server's side of the
        // conversation with a client this product is not. Nothing in the vocabulary and no string literal of the App or Core
        // sources can produce them.
        foreach (var type in new[] { "register", "account-delete", "account-deleted" })
        {
            Assert.False(ExchangeProtocol.IsKnownType(type), type);
        }

        Assert.Equal(9, Corpus.Frames.Count(step => step.ClientToServer && step.Type == "register"));
        Assert.Equal(2, Corpus.Frames.Count(step => step.ClientToServer && step.Type == "account-delete"));

        var sources = new[] { AppSourceFile.Directory, Path.Combine(AppSourceFile.Directory, "..", "core-source") }
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.cs")).ToArray();
        Assert.True(sources.Length > 100, "the App and Core sources must be present as data");
        var offenders = sources.Select(path => AppSourceFile.Parse(Path.GetFileName(path), File.ReadAllText(path)))
            .Where(source => source.Literals.Values.Any(literal => literal is "register" or "account-delete" or "account-deleted"))
            .Select(source => source.Name).ToArray();
        Assert.Empty(offenders);
    }

    // ---------------------------------------------------------------- password (item AT)

    [Fact]
    public async Task PasswordChange_IsByteForByteWhatTheProductWrites_ForAllThreeChanges()
    {
        Assert.Empty(await ExchangeFramesReplay.PasswordChangeFailuresAsync(Corpus));
        Assert.Equal(3, Corpus.Frames.Count(step => step.ClientToServer && step.Type == "password-change"));
    }

    [Fact]
    public void Password_TheAnswersMapToTheirRows_AndTheNewTokenIsAToken()
    {
        var transcript = Corpus.Named("password");
        // wrong old password, too-short new password (stays signed in), the other device is signed out, a deleted account.
        var rows = transcript.Steps.Where(step => step.ServerToClient && step.Type == "refused").Select(step =>
        {
            using var document = step.Parse();
            return ExchangeProtocol.RefusalKey(ExchangeProtocol.String(document.RootElement, "reason"));
        });
        Assert.Equal(["ExchangeBadCredentials", "ExchangePasswordPolicy", "ExchangeSignedOut", "ExchangeBadCredentials", "ExchangeBadCredentials"], rows);

        var changed = ServerFrames("password", "password-changed").Single();
        using var document = changed.Parse();
        var token = ExchangeProtocol.String(document.RootElement, "deviceToken")!;
        Assert.True(ExchangeFramesReplay.IsDeviceToken(token));
        // The next hello of that device carries it: the rotation the product's HandlePasswordChanged stores.
        var next = transcript.Steps.Single(step => step is { Stream: "a-new-token", ClientToServer: true, IsFrame: true });
        using var hello = next.Parse();
        Assert.Equal(token, ExchangeProtocol.String(hello.RootElement, "deviceToken"));
    }

    [Fact]
    public void Password_AccountDeletedIsAFrameTheProductDoesNotKnow_SoItsControlLoopIgnoresIt()
    {
        // Server-only: `account-deleted` answers `account-delete`, which this product has no way to send (no UI, not in its
        // vocabulary). A type it does not know is read and ignored by the control loop, and ends the stream only by closing.
        var deleted = ServerFrames("password", "account-deleted").Single();
        Assert.False(ExchangeProtocol.IsKnownType(deleted.Type));
        Assert.Equal(["account-deleted"], Corpus.Named("password").Steps.Where(step => step.ServerToClient && step.IsFrame
            && !ExchangeProtocol.IsKnownType(step.Type)).Select(step => step.Type!));
    }

    // ---------------------------------------------------------------- pace (item AW)

    [Fact]
    public void Pace_BadCredentialsTwice_ThenRateLimited_ThenTheRightPasswordIsAnswered()
    {
        // The slot timing is the server's; the client's duty is to handle `rate-limited` on any check and to show the rows.
        var pace = Corpus.Named("pace");
        var answers = pace.Steps.Where(step => step.ServerToClient && step.IsFrame).Select(step =>
        {
            using var document = step.Parse();
            return step.Type == "refused" ? ExchangeProtocol.RefusalKey(ExchangeProtocol.String(document.RootElement, "reason")) : step.Type!;
        });
        Assert.Equal(["ExchangeBadCredentials", "ExchangeBadCredentials", "ExchangeRateLimited", "enrolled"], answers);
        Assert.Equal(["first", "second", "third", "right-password"],
            pace.Steps.Where(step => step.ServerToClient && step.IsFrame).Select(step => step.Stream));
    }

    // ---------------------------------------------------------------- negative proofs

    [Fact]
    public async Task NegativeProof_OneChangedByteOfAPasswordChangeTheProductWrites_IsNotTheFilesFrame()
    {
        var step = Corpus.Frames.First(candidate => candidate.ClientToServer && candidate.Type == "password-change");
        using var document = step.Parse();
        var change = new ExchangePasswordChange(
            ExchangeProtocol.String(document.RootElement, "oldPassword")!.ToCharArray(),
            ExchangeProtocol.String(document.RootElement, "newPassword")!.ToCharArray());
        var written = await ExchangeFramesReplay.WrittenAsync(change);
        Assert.Null(ExchangeFramesReplay.ByteExactFailure(step, written));
        written[^4] ^= 0x01; // a character of the new password
        Assert.NotNull(ExchangeFramesReplay.ByteExactFailure(step, written));

        // The same frame for another new password is another frame: the replay compares bytes, not "a password-change".
        var other = new ExchangePasswordChange(
            ExchangeProtocol.String(document.RootElement, "oldPassword")!.ToCharArray(), "another-new-pw".ToCharArray());
        Assert.NotNull(ExchangeFramesReplay.ByteExactFailure(step, await ExchangeFramesReplay.WrittenAsync(other)));
    }

    [Fact]
    public void NegativeProof_ATokenOneCharacterShort_AndAWrongRow_FailTheChecks()
    {
        var enrolled = ServerFrames("registration", "enrolled").First();
        Assert.Null(ExchangeFramesReplay.AccountFrameFailure(enrolled));
        using var document = enrolled.Parse();
        var token = ExchangeProtocol.String(document.RootElement, "deviceToken")!;
        var shortened = enrolled with { Json = enrolled.Json!.Replace(token, token[1..], StringComparison.Ordinal) };
        Assert.NotNull(ExchangeFramesReplay.AccountFrameFailure(shortened));

        var limited = Corpus.Named("pace").Steps.Single(step => step.ServerToClient && step.IsFrame
            && step.Json!.Contains("rate-limited", StringComparison.Ordinal));
        Assert.Null(ExchangeFramesReplay.RefusalFailure(limited, ExchangeFramesReplay.RefusalRows));
        var wrong = new Dictionary<string, string>(ExchangeFramesReplay.RefusalRows) { ["rate-limited"] = "ExchangeBadCredentials" };
        Assert.NotNull(ExchangeFramesReplay.RefusalFailure(limited, wrong));
    }
}
