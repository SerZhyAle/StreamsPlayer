using System.Text.RegularExpressions;
using StreamsPlayer.Core;
using Digest = StreamsPlayer.Core.Tests.BroadcastDigestTestClient;

namespace StreamsPlayer.Core.Tests;

/// <summary>LIVE-BROADCAST item M: the producer's HTTP Digest (RFC 7616) check, in memory, with a clock the test moves.</summary>
public sealed partial class BroadcastDigestAuthenticatorTests
{
    private const string Method = "GET";
    private const string Target = "/live-audio.aac";

    private static ExchangeRelayCredentials Pair(string user = "listener", string password = "correct horse") => new(user, password);

    private static BroadcastDigestAuthenticator Required(BroadcastTestClock? clock = null, params ExchangeRelayCredentials[] pairs)
    {
        var authenticator = new BroadcastDigestAuthenticator(clock);
        authenticator.SetPairs(pairs.Length == 0 ? [Pair()] : pairs);
        return authenticator;
    }

    [GeneratedRegex(@"nonce=""[^""]*""")]
    private static partial Regex NonceField();

    private static string WithoutNonce(string line) => NonceField().Replace(line, "nonce=<n>");

    // RFC 7616 section 3.9.1: user Mufasa, password "Circle of Life", realm "http-auth@example.org", GET /dir/index.html.
    // The expected responses are the RFC's own, printed in its examples.
    private const string RfcNonce = "7ypf/xlj9XXwfDPEoM4URrv/xwf94BcCAzFZH4GiTo0v";
    private const string RfcCnonce = "f2/wE4q74E6zIJEtWaHKaf5wv/H5QzzpXusqGemxURZJ";

    [Theory]
    [InlineData("SHA-256", "753927fa0e85d155564e2e272a28d1802ca10daf4496794697cf8db5856cb6c1")]
    [InlineData("MD5", "8ca523f5e9506fed4657c9700eebdbec")]
    public void TheResponseReproducesRfc7616Section391(string algorithm, string expected)
    {
        var ha1 = BroadcastDigest.Ha1(algorithm, "Mufasa", "http-auth@example.org", "Circle of Life");

        var response = BroadcastDigest.Response(algorithm, ha1, "GET", "/dir/index.html", RfcNonce, "00000001", RfcCnonce);

        Assert.Equal(expected, response);
        Assert.Equal(expected, Digest.Response(
            algorithm, "Mufasa", "http-auth@example.org", "Circle of Life", "GET", "/dir/index.html", RfcNonce, "00000001", RfcCnonce));
    }

    [Fact]
    public void TheRfcAuthorizationFieldParsesWithItsRealmAndItsOpaque()
    {
        const string field = """
            Digest username="Mufasa", realm="fms-broadcast", uri="/dir/index.html", algorithm=MD5,
            nonce="7ypf/xlj9XXwfDPEoM4URrv/xwf94BcCAzFZH4GiTo0v", nc=00000001,
            cnonce="f2/wE4q74E6zIJEtWaHKaf5wv/H5QzzpXusqGemxURZJ", qop=auth,
            response="8ca523f5e9506fed4657c9700eebdbec", opaque="FQhe/qaU925kfnzjCev0ciny7QMkPqMAFRtzCUYo5tdS"
            """;

        var answer = BroadcastDigest.ParseAnswer(field.ReplaceLineEndings(" "));

        Assert.NotNull(answer);
        Assert.Equal("Mufasa", answer.User);
        Assert.Equal("MD5", answer.Algorithm);
        Assert.Equal(1, answer.Nc);
        Assert.Equal("/dir/index.html", answer.Uri);
        Assert.Equal("8ca523f5e9506fed4657c9700eebdbec", answer.Response);
    }

    [Fact]
    public void WithNoPairsEveryRequestIsAllowedAndNothingIsChallenged()
    {
        var authenticator = new BroadcastDigestAuthenticator();

        var result = authenticator.Check(Method, Target, null);

        Assert.False(authenticator.Required);
        Assert.True(result.Allowed);
        Assert.Null(result.AuthenticatedUser);
        Assert.Empty(result.Challenge);
        Assert.True(authenticator.Check(Method, Target, "Digest garbage").Allowed);
    }

    [Fact]
    public void ARequestWithoutCredentialsGetsBothChallengesWithSha256First()
    {
        var authenticator = Required();

        var result = authenticator.Check(Method, Target, null);

        Assert.True(authenticator.Required);
        Assert.False(result.Allowed);
        Assert.False(result.CredentialsRefused);
        Assert.Equal(2, result.Challenge.Count);
        var first = Digest.ParseChallenge(result.Challenge[0]);
        var second = Digest.ParseChallenge(result.Challenge[1]);
        Assert.Equal("SHA-256", first["algorithm"]);
        Assert.Equal("MD5", second["algorithm"]);
        Assert.All(new[] { first, second }, fields =>
        {
            Assert.Equal("fms-broadcast", fields["realm"]);
            Assert.Equal("auth", fields["qop"]);
            Assert.False(string.IsNullOrEmpty(fields["nonce"]));
            Assert.False(fields.ContainsKey("stale"));
        });
        Assert.Equal(first["nonce"], second["nonce"]);
    }

    [Theory]
    [InlineData("SHA-256")]
    [InlineData("MD5")]
    public void ARightAnswerInEitherAlgorithmIsAllowed(string algorithm)
    {
        var authenticator = Required();
        var challenge = authenticator.Check(Method, Target, null).Challenge;

        var result = authenticator.Check(Method, Target, Digest.Authorize(challenge, algorithm, "listener", "correct horse", Method, Target));

        Assert.True(result.Allowed);
        Assert.Equal("listener", result.AuthenticatedUser);
        Assert.Empty(result.Challenge);
        Assert.NotNull(result.Grant);
        Assert.True(authenticator.IsCurrent(result.Grant));
    }

    [Fact]
    public void AnAnswerWithoutAnAlgorithmFieldIsReadAsMd5()
    {
        var authenticator = Required();
        var challenge = authenticator.Check(Method, Target, null).Challenge;
        var withField = Digest.Authorize(challenge, "MD5", "listener", "correct horse", Method, Target);

        var result = authenticator.Check(Method, Target, withField.Replace("algorithm=MD5, ", ""));

        Assert.True(result.Allowed);
    }

    [Fact]
    public void ANonAsciiPasswordIsHashedAsUtf8()
    {
        var authenticator = Required(null, Pair("listener", "pässwörd-密码"));
        var challenge = authenticator.Check(Method, Target, null).Challenge;

        Assert.True(authenticator.Check(Method, Target,
            Digest.Authorize(challenge, "SHA-256", "listener", "pässwörd-密码", Method, Target)).Allowed);
    }

    [Fact]
    public void AWrongPasswordAnUnknownUserAndNoCredentialsGetTheSameChallengeAndNothingFiner()
    {
        var authenticator = Required();
        var baseline = authenticator.Check(Method, Target, null);
        var challenge = baseline.Challenge;

        var wrongPassword = authenticator.Check(Method, Target, Digest.Authorize(challenge, "SHA-256", "listener", "not it", Method, Target));
        var unknownUser = authenticator.Check(Method, Target, Digest.Authorize(challenge, "SHA-256", "nobody", "correct horse", Method, Target));

        Assert.All(new[] { wrongPassword, unknownUser }, result =>
        {
            Assert.False(result.Allowed);
            Assert.Null(result.AuthenticatedUser);
            Assert.Null(result.Grant);
            Assert.True(result.CredentialsRefused);
            Assert.Equal(baseline.Challenge.Select(WithoutNonce), result.Challenge.Select(WithoutNonce));
        });
    }

    [Fact]
    public void AReplayedNonceCountIsRefusedAndAHigherOneIsNot()
    {
        var authenticator = Required();
        var challenge = authenticator.Check(Method, Target, null).Challenge;
        string Answer(int nc) => Digest.Authorize(challenge, "SHA-256", "listener", "correct horse", Method, Target, nc);

        Assert.True(authenticator.Check(Method, Target, Answer(1)).Allowed);
        var replayed = authenticator.Check(Method, Target, Answer(1));
        Assert.False(replayed.Allowed);
        Assert.False(replayed.CredentialsRefused);
        Assert.All(replayed.Challenge, line => Assert.Equal("true", Digest.ParseChallenge(line)["stale"]));
        Assert.True(authenticator.Check(Method, Target, Answer(2)).Allowed);
        Assert.False(authenticator.Check(Method, Target, Answer(2)).Allowed);
        Assert.False(authenticator.Check(Method, Target, Answer(1)).Allowed);
        Assert.True(authenticator.Check(Method, Target, Answer(7)).Allowed);
    }

    [Fact]
    public void ANonceIsHonouredForFiveMinutesAndThenAnswersStaleOnlyToARightPassword()
    {
        var clock = new BroadcastTestClock();
        var authenticator = Required(clock);
        var challenge = authenticator.Check(Method, Target, null).Challenge;
        clock.Advance(BroadcastDigestAuthenticator.NonceLifetime - TimeSpan.FromSeconds(1));
        Assert.True(authenticator.Check(Method, Target, Digest.Authorize(challenge, "SHA-256", "listener", "correct horse", Method, Target)).Allowed);

        clock.Advance(TimeSpan.FromSeconds(2));
        var expired = authenticator.Check(Method, Target, Digest.Authorize(challenge, "MD5", "listener", "correct horse", Method, Target));
        var wrong = authenticator.Check(Method, Target, Digest.Authorize(challenge, "MD5", "listener", "wrong", Method, Target));

        Assert.False(expired.Allowed);
        Assert.False(expired.CredentialsRefused);
        Assert.All(expired.Challenge, line => Assert.Equal("true", Digest.ParseChallenge(line)["stale"]));
        Assert.Equal(2, expired.Challenge.Count);
        Assert.False(wrong.Allowed);
        Assert.All(wrong.Challenge, line => Assert.False(Digest.ParseChallenge(line).ContainsKey("stale")));

        // The stale challenge carries a fresh nonce a client can answer straight away.
        Assert.True(authenticator.Check(Method, Target,
            Digest.Authorize(expired.Challenge, "SHA-256", "listener", "correct horse", Method, Target)).Allowed);
    }

    [Fact]
    public void AnAnswerThatDeclaresOrComputesAnotherAlgorithmIsRefused()
    {
        var authenticator = Required();
        var challenge = authenticator.Check(Method, Target, null).Challenge;
        string Check(string header) => authenticator.Check(Method, Target, header).Allowed ? "allowed" : "refused";

        var sha = Digest.Authorize(challenge, "SHA-256", "listener", "correct horse", Method, Target, nc: 1);
        Assert.Equal("refused", Check(Digest.Authorize(challenge, "SHA-256", "listener", "correct horse", Method, Target, nc: 2, algorithmToDeclare: "MD5")));
        Assert.Equal("refused", Check(Digest.Authorize(challenge, "SHA-256", "listener", "correct horse", Method, Target, nc: 3, algorithmToDeclare: "SHA-512-256")));
        Assert.Equal("refused", Check(Digest.Authorize(challenge, "SHA-256", "listener", "correct horse", Method, Target, nc: 4, algorithmToDeclare: "MD5-sess")));
        Assert.Equal("allowed", Check(sha));
    }

    [Fact]
    public void AnAnswerForAnotherUriOrMethodOrRealmIsRefused()
    {
        var authenticator = Required();
        var challenge = authenticator.Check(Method, Target, null).Challenge;

        Assert.False(authenticator.Check(Method, "/other", Digest.Authorize(challenge, "SHA-256", "listener", "correct horse", Method, Target, nc: 1)).Allowed);
        Assert.False(authenticator.Check(Method, Target, Digest.Authorize(challenge, "SHA-256", "listener", "correct horse", Method, Target, nc: 2, uriToHash: "/other")).Allowed);
        Assert.False(authenticator.Check("HEAD", Target, Digest.Authorize(challenge, "SHA-256", "listener", "correct horse", Method, Target, nc: 3)).Allowed);
        var otherRealm = Digest.Authorize(challenge, "SHA-256", "listener", "correct horse", Method, Target, nc: 4).Replace("fms-broadcast", "elsewhere");
        Assert.False(authenticator.Check(Method, Target, otherRealm).Allowed);
        Assert.True(authenticator.Check(Method, Target, Digest.Authorize(challenge, "SHA-256", "listener", "correct horse", Method, Target, nc: 5)).Allowed);
    }

    [Fact]
    public void TheUriMustBeTheRequestTargetAsReceivedQueryIncluded()
    {
        var authenticator = Required();
        var challenge = authenticator.Check(Method, Target, null).Challenge;
        const string withQuery = Target + "?listener=1";

        Assert.False(authenticator.Check(Method, withQuery, Digest.Authorize(challenge, "SHA-256", "listener", "correct horse", Method, Target, nc: 1)).Allowed);
        Assert.True(authenticator.Check(Method, withQuery, Digest.Authorize(challenge, "SHA-256", "listener", "correct horse", Method, withQuery, nc: 2)).Allowed);
    }

    [Fact]
    public void ATamperedForeignOrFutureNonceIsRefusedWithoutBeingCalledStale()
    {
        var clock = new BroadcastTestClock();
        var authenticator = Required(clock);
        var challenge = authenticator.Check(Method, Target, null).Challenge;
        var nonce = Digest.ParseChallenge(challenge[0])["nonce"];

        string Tampered(string value) => Digest.Authorize(
            challenge.Select(line => line.Replace(nonce, value)).ToList(), "SHA-256", "listener", "correct horse", Method, Target);

        var flipped = nonce[10] == 'A' ? 'B' : 'A';
        // Well-formed, but signed by another instance's key.
        var foreignNonce = Digest.ParseChallenge(Required(clock).Check(Method, Target, null).Challenge[0])["nonce"];
        var results = new[]
        {
            authenticator.Check(Method, Target, Tampered(nonce[..10] + flipped + nonce[11..])),
            authenticator.Check(Method, Target, Tampered(nonce[..^2])),
            authenticator.Check(Method, Target, Tampered("AAAA")),
            authenticator.Check(Method, Target, Tampered(foreignNonce))
        };

        Assert.All(results, result =>
        {
            Assert.False(result.Allowed);
            Assert.True(result.CredentialsRefused);
            Assert.All(result.Challenge, line => Assert.False(Digest.ParseChallenge(line).ContainsKey("stale")));
        });

        // A nonce stamped after now (the clock was turned back) is not one this server issued either.
        clock.Advance(TimeSpan.FromMinutes(-1));
        Assert.True(authenticator.Check(Method, Target,
            Digest.Authorize(challenge, "SHA-256", "listener", "correct horse", Method, Target, nc: 1)).CredentialsRefused);
    }

    [Fact]
    public void AnAnswerWithoutQopWithAnotherQopOrWithARepeatedFieldIsRefused()
    {
        var authenticator = Required();
        var challenge = authenticator.Check(Method, Target, null).Challenge;
        var good = Digest.Authorize(challenge, "SHA-256", "listener", "correct horse", Method, Target);

        var refused = new[]
        {
            good.Replace("qop=auth, ", ""),
            good.Replace("qop=auth", "qop=auth-int"),
            good + ", username=\"listener\"",
            good.Replace("Digest ", "Basic "),
            "Digest"
        };

        Assert.All(refused, header => Assert.False(authenticator.Check(Method, Target, header).Allowed));
        Assert.True(authenticator.Check(Method, Target, good).Allowed);
    }

    [Fact]
    public void AnOversizedFieldAndAnUserHashAnswerAreRefused()
    {
        var authenticator = Required();
        var challenge = authenticator.Check(Method, Target, null).Challenge;
        var good = Digest.Authorize(challenge, "SHA-256", "listener", "correct horse", Method, Target);

        Assert.False(authenticator.Check(Method, Target, good + ", x=\"" + new string('a', 5000) + "\"").Allowed);
        Assert.False(authenticator.Check(Method, Target, good + ", userhash=true").Allowed);
        Assert.False(authenticator.Check(Method, Target, good.Replace("nc=00000001", "nc=0")).Allowed);
        Assert.False(authenticator.Check(Method, Target, good.Replace("nc=00000001", "nc=00000000")).Allowed);
        Assert.True(authenticator.Check(Method, Target, good).Allowed);
    }

    [Fact]
    public void SwappingThePairsOnAirRevokesTheOldPasswordAtOnce()
    {
        var authenticator = Required(null, Pair("listener", "old password"));
        var challenge = authenticator.Check(Method, Target, null).Challenge;
        var admitted = authenticator.Check(Method, Target, Digest.Authorize(challenge, "SHA-256", "listener", "old password", Method, Target, nc: 1));
        Assert.True(admitted.Allowed);
        var revision = authenticator.Revision;
        var changes = 0;
        authenticator.PairsChanged += () => changes++;

        authenticator.SetPairs([Pair("listener", "new password")]);

        Assert.Equal(1, changes);
        Assert.Equal(revision + 1, authenticator.Revision);
        Assert.False(authenticator.IsCurrent(admitted.Grant!));
        Assert.False(authenticator.Check(Method, Target, Digest.Authorize(challenge, "SHA-256", "listener", "old password", Method, Target, nc: 2)).Allowed);
        Assert.True(authenticator.Check(Method, Target, Digest.Authorize(challenge, "SHA-256", "listener", "new password", Method, Target, nc: 3)).Allowed);
    }

    [Fact]
    public void AnUnchangedPairKeepsItsGrantAndSettingTheSameSetRaisesNothing()
    {
        var authenticator = Required(null, Pair("listener", "same"));
        var challenge = authenticator.Check(Method, Target, null).Challenge;
        var grant = authenticator.Check(Method, Target, Digest.Authorize(challenge, "SHA-256", "listener", "same", Method, Target)).Grant!;
        var changes = 0;
        authenticator.PairsChanged += () => changes++;

        authenticator.SetPairs([Pair("listener", "same")]);
        Assert.Equal(0, changes);

        authenticator.SetPairs([Pair("listener", "same"), Pair("account", "another")]);
        Assert.Equal(1, changes);
        Assert.True(authenticator.IsCurrent(grant));

        authenticator.SetPairs([Pair("account", "another")]);
        Assert.Equal(2, changes);
        Assert.False(authenticator.IsCurrent(grant));
    }

    [Fact]
    public void AnEmptyListOpensTheServerAgainAndRaisesTheChange()
    {
        var authenticator = Required();
        var changes = 0;
        authenticator.PairsChanged += () => changes++;

        authenticator.SetPairs([]);

        Assert.Equal(1, changes);
        Assert.False(authenticator.Required);
        Assert.True(authenticator.Check(Method, Target, null).Allowed);
    }

    [Fact]
    public void AnInvalidSetSwapsNothing()
    {
        var authenticator = Required();
        var revision = authenticator.Revision;

        Assert.Throws<ArgumentException>(() => authenticator.SetPairs([Pair("fine"), Pair("fine", "again")]));
        Assert.Throws<ArgumentException>(() => authenticator.SetPairs([Pair("a:b")]));
        Assert.Throws<ArgumentException>(() => authenticator.SetPairs([new ExchangeRelayCredentials("listener", "")]));

        Assert.Equal(revision, authenticator.Revision);
        Assert.True(authenticator.Required);
    }

    [Fact]
    public void AFullReplayTableForgetsTheOldestNonceAndStaleAnswersItInsteadOfAcceptingAReplay()
    {
        var clock = new BroadcastTestClock();
        var authenticator = new BroadcastDigestAuthenticator(clock, replayCapacity: 1);
        authenticator.SetPairs([Pair()]);
        string Answer(IReadOnlyList<string> challenge, int nc) =>
            Digest.Authorize(challenge, "SHA-256", "listener", "correct horse", Method, Target, nc);

        var first = authenticator.Check(Method, Target, null).Challenge;
        clock.Advance(TimeSpan.FromSeconds(1));
        var second = authenticator.Check(Method, Target, null).Challenge;
        Assert.True(authenticator.Check(Method, Target, Answer(first, 1)).Allowed);
        Assert.True(authenticator.Check(Method, Target, Answer(second, 1)).Allowed);

        // The first nonce's count was dropped to make room. Honouring it again would accept the captured answer a second time.
        // The clock moves on so the fresh nonce of the stale answer is issued after the eviction floor.
        clock.Advance(TimeSpan.FromSeconds(1));
        var replay = authenticator.Check(Method, Target, Answer(first, 1));
        Assert.False(replay.Allowed);
        Assert.All(replay.Challenge, line => Assert.Equal("true", Digest.ParseChallenge(line)["stale"]));
        Assert.False(authenticator.Check(Method, Target, Answer(second, 1)).Allowed);
        Assert.True(authenticator.Check(Method, Target, Answer(replay.Challenge, 1)).Allowed);
    }

    [Fact]
    public void OneAnswerPresentedByManyThreadsAtOnceIsAllowedExactlyOnce()
    {
        var authenticator = Required();
        var answer = Digest.Authorize(authenticator.Check(Method, Target, null).Challenge, "SHA-256", "listener", "correct horse", Method, Target);

        var allowed = Enumerable.Range(0, 64).AsParallel().WithDegreeOfParallelism(16)
            .Select(_ => authenticator.Check(Method, Target, answer).Allowed)
            .Count(value => value);

        Assert.Equal(1, allowed);
    }

    [Fact]
    public void ChecksRacingASwapNeverThrow()
    {
        var authenticator = Required();
        var challenge = authenticator.Check(Method, Target, null).Challenge;

        Parallel.For(0, 200, index =>
        {
            if (index % 10 == 0)
            {
                authenticator.SetPairs([Pair("listener", "pw" + index)]);
            }
            else
            {
                _ = authenticator.Check(Method, Target, Digest.Authorize(challenge, "MD5", "listener", "correct horse", Method, Target, nc: index + 1));
            }
        });

        Assert.True(authenticator.Required);
    }

    [Fact]
    public void EveryChallengeCarriesAFreshNonce()
    {
        var authenticator = Required();

        var nonces = Enumerable.Range(0, 50)
            .Select(_ => Digest.ParseChallenge(authenticator.Check(Method, Target, null).Challenge[0])["nonce"])
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(50, nonces.Count);
    }

    [Fact]
    public void AGrantPrintsItsUserAndNoHash()
    {
        var authenticator = Required();
        var challenge = authenticator.Check(Method, Target, null).Challenge;

        var grant = authenticator.Check(Method, Target, Digest.Authorize(challenge, "SHA-256", "listener", "correct horse", Method, Target)).Grant!;

        Assert.DoesNotContain(Digest.Hex("SHA-256", "listener:fms-broadcast:correct horse"), grant.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("correct horse", grant.ToString(), StringComparison.Ordinal);
    }
}
