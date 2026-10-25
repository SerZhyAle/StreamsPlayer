using System.Text.Json;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>DEVICE-EXCHANGE item BF: the verifiers a broadcaster uploads so the relay can check a listener without a password.</summary>
public sealed class BroadcastVerifierTests
{
    // These two answers were computed by Python 3.10, not by this code:
    //   hashlib.pbkdf2_hmac('sha256', password.encode('utf-8'), salt, 600000, 32)
    // then base64.urlsafe_b64encode(..).rstrip('='). They pin PBKDF2-HMAC-SHA256 at 600000 rounds, the UTF-8 password
    // and the unpadded base64url spelling against an implementation that shares nothing with Rfc2898DeriveBytes.
    private const string AsciiPassword = "correct horse battery staple";
    private const string AsciiSalt = "AAECAwQFBgcICQoLDA0ODw"; // bytes 0x00..0x0F
    private const string AsciiHash = "7xdxRO7JQgy8EJPSqLNEqSvFBtDU7JwCjdGfgyTYweY";

    private const string UnicodePassword = "pässwörd-密码-\U0001F511";
    private const string UnicodeSalt = "paWlpaWlpaWlpaWlpaWlpQ"; // sixteen 0xA5 bytes
    private const string UnicodeHash = "oy3bn8KnqVJ6H0EA7f1a03WdCks-HgYquNp-4KKa4mI";

    private static ExchangeRelayCredentials Pair(string user = "listener", string password = "pw") => new(user, password);

    [Fact]
    public void TheHashMatchesAnIndependentPbkdf2ForAnAsciiPassword()
    {
        var verifier = BroadcastVerifier.Create(Pair("listener", AsciiPassword), Enumerable.Range(0, 16).Select(i => (byte)i).ToArray());

        Assert.Equal(new BroadcastVerifier("listener", AsciiSalt, AsciiHash), verifier);
    }

    [Fact]
    public void ThePasswordIsHashedAsUtf8()
    {
        var verifier = BroadcastVerifier.Create(Pair("account", UnicodePassword), Enumerable.Repeat((byte)0xA5, 16).ToArray());

        Assert.Equal(new BroadcastVerifier("account", UnicodeSalt, UnicodeHash), verifier);
    }

    [Fact]
    public void TheContractConstantsAreTheOnesItemBfFixes()
    {
        Assert.Equal(600_000, BroadcastVerifier.Iterations);
        Assert.Equal(16, BroadcastVerifier.SaltBytes);
        Assert.Equal(32, BroadcastVerifier.HashBytes);
        Assert.Equal(64, BroadcastVerifier.MaximumEntries);
    }

    [Fact]
    public void ASaltIsRandomPerVerifierAndBothValuesAreUnpaddedBase64Url()
    {
        var first = BroadcastVerifier.Create(Pair());
        var second = BroadcastVerifier.Create(Pair());

        Assert.NotEqual(first.Salt, second.Salt);
        Assert.NotEqual(first.Hash, second.Hash);
        foreach (var value in new[] { first.Salt, first.Hash })
        {
            Assert.DoesNotContain('=', value);
            Assert.DoesNotContain('+', value);
            Assert.DoesNotContain('/', value);
        }

        Assert.True(ExchangeBase64Url.TryDecode(first.Salt, out var salt));
        Assert.Equal(16, salt.Length);
        Assert.True(ExchangeBase64Url.TryDecode(first.Hash, out var hash));
        Assert.Equal(32, hash.Length);
    }

    private static readonly string[] RefusedUsers =
    [
        "",
        "a:b",
        new string('u', 65),
        // 33 two-byte characters are 66 bytes: the relay measures the user in bytes.
        new string('д', 33),
        "line\nbreak",
        "tab\tuser",
        "del\u007F",
        "lone\uD83D"
    ];

    [Fact]
    public void AUserTheRelayWouldRefuseIsRefusedHere()
    {
        for (var index = 0; index < RefusedUsers.Length; index++)
        {
            var user = RefusedUsers[index];
            Assert.False(BroadcastVerifier.IsValidUser(user), "refused user #" + index);
            Assert.Throws<ArgumentException>(() => BroadcastVerifier.Create(Pair(user)));
        }
    }

    [Fact]
    public void ASixtyFourByteUserIsTheLongestOne()
    {
        Assert.True(BroadcastVerifier.IsValidUser(new string('u', 64)));
        Assert.True(BroadcastVerifier.IsValidUser(new string('д', 32)));
        Assert.True(BroadcastVerifier.IsValidUser("guest-1a2b3c4d"));
        Assert.False(BroadcastVerifier.IsValidUser(null));
    }

    [Fact]
    public void ABadPasswordOrSaltIsRefused()
    {
        Assert.Throws<ArgumentException>(() => BroadcastVerifier.Create(Pair(password: "")));
        Assert.Throws<ArgumentException>(() => BroadcastVerifier.Create(Pair(password: "unpaired\uD83D")));
        Assert.Throws<ArgumentException>(() => BroadcastVerifier.Create(Pair(), new byte[15]));
        Assert.Throws<ArgumentException>(() => BroadcastVerifier.Create(Pair(), new byte[17]));
    }

    [Fact]
    public void CreateAllKeepsTheOrderOfThePairs()
    {
        var verifiers = BroadcastVerifier.CreateAll([Pair("account", "one"), Pair("listener", "two")]);

        Assert.Equal(new[] { "account", "listener" }, verifiers.Select(verifier => verifier.User).ToArray());
        Assert.Empty(BroadcastVerifier.CreateAll([]));
    }

    [Fact]
    public void ADuplicateUserOrTooManyPairsAreRefusedBeforeAnyHashing()
    {
        Assert.Throws<ArgumentException>(() => BroadcastVerifier.CreateAll([Pair("same", "one"), Pair("same", "two")]));
        var tooMany = Enumerable.Range(0, 65).Select(index => Pair("user" + index)).ToList();
        Assert.Throws<ArgumentException>(() => BroadcastVerifier.CreateAll(tooMany));
    }

    [Fact]
    public void TheEnvelopeMemberIsAnArrayOfCamelCaseEntries()
    {
        var verifier = new BroadcastVerifier("listener", "c2FsdA", "aGFzaA");

        var json = JsonSerializer.Serialize(
            new Dictionary<string, object?> { ["verifiers"] = BroadcastVerifier.ToEnvelopeMember([verifier]) },
            ExchangeProtocol.JsonOptions);

        Assert.Equal("""{"verifiers":[{"user":"listener","salt":"c2FsdA","hash":"aGFzaA"}]}""", json);
    }

    [Fact]
    public void NoVerifiersIsAnEmptyArrayNotAMissingMember()
    {
        var json = JsonSerializer.Serialize(
            new Dictionary<string, object?> { ["verifiers"] = BroadcastVerifier.ToEnvelopeMember([]) },
            ExchangeProtocol.JsonOptions);

        Assert.Equal("""{"verifiers":[]}""", json);
    }

    [Fact]
    public void AVerifierPrintsNoneOfItsMembers()
    {
        var verifier = new BroadcastVerifier("listener", "c2FsdFNBTFRzYWx0", "aGFzaEhBU0hoYXNo");

        var text = verifier.ToString();

        Assert.DoesNotContain("c2FsdFNBTFRzYWx0", text, StringComparison.Ordinal);
        Assert.DoesNotContain("aGFzaEhBU0hoYXNo", text, StringComparison.Ordinal);
    }
}
