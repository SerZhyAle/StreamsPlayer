using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>LIVE-BROADCAST item R: the generated password and the one rule a password the person sets must pass.</summary>
public sealed class BroadcastLinkPasswordTests
{
    private const string LookAlikes = "lI10O";

    [Fact]
    public void AGeneratedPasswordIsSixteenCharactersWithoutLookAlikes()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < 200; index++)
        {
            var password = BroadcastLinkPassword.Generate();
            Assert.Equal(16, password.Length);
            Assert.All(password, character =>
            {
                Assert.True(char.IsAsciiLetterOrDigit(character), password);
                Assert.DoesNotContain(character, LookAlikes);
            });
            Assert.True(BroadcastLinkPassword.IsAcceptable(password));
            seen.Add(password);
        }

        Assert.Equal(200, seen.Count);
    }

    [Fact]
    public void TheDefaultChoiceIsGeneratedAndTheSharedUsersAreTheFamilys()
    {
        Assert.Equal(BroadcastLinkChoice.Generated, default);
        Assert.Equal("account", BroadcastLinkPassword.OwnUser);
        Assert.Equal("listener", BroadcastLinkPassword.LinkUser);
        Assert.Equal(64, BroadcastLinkPassword.MaximumCodePoints);
    }

    public static TheoryData<string> Acceptable => new()
    {
        "a",
        "1234",
        new string('x', 64),
        " ",
        "  spaces are kept, not trimmed  ",
        "пароль",
        "pass" + char.ConvertFromUtf32(0xA0) + "word",  // no-break space: a space, not a control
        "emoji \U0001F511 inside",
        // 64 code points that are 128 UTF-16 units: the limit counts code points.
        string.Concat(Enumerable.Repeat("\U0001F511", 64)),
        "a" + char.ConvertFromUtf32(0x200D) + "b"  // zero-width joiner: a format character, not a control
    };

    [Theory]
    [MemberData(nameof(Acceptable))]
    public void AnyTextOfOneToSixtyFourCodePointsWithoutControlsIsAcceptable(string text) =>
        Assert.True(BroadcastLinkPassword.IsAcceptable(text));

    // A loop in one fact rather than a theory: the runner would have to print control characters and unpaired
    // surrogates in a test name, and an unpaired surrogate is not text any report can carry.
    private static readonly string?[] Refused =
    [
        null,
        "",
        new string('x', 65),
        string.Concat(Enumerable.Repeat("\U0001F511", 65)),
        "a\0b",
        "tab\there",
        "line\nbreak",
        "carriage\rreturn",
        "\u001Fend",
        "del\u007F",
        "\u0080",
        "next-line\u0085",
        "\u009F",
        "line-separator" + char.ConvertFromUtf32(0x2028),
        "paragraph-separator" + char.ConvertFromUtf32(0x2029),
        "lone-high\uD83D",
        "\uDD11lone-low",
        "reversed\uDD11\uD83D"
    ];

    [Fact]
    public void AnEmptyTooLongOrControlBearingTextIsRefused()
    {
        for (var index = 0; index < Refused.Length; index++)
        {
            Assert.False(BroadcastLinkPassword.IsAcceptable(Refused[index]), "refused sample #" + index);
        }
    }

    [Fact]
    public void ThereIsNoComplexityRule()
    {
        Assert.True(BroadcastLinkPassword.IsAcceptable("1"));
        Assert.True(BroadcastLinkPassword.IsAcceptable("password"));
        Assert.True(BroadcastLinkPassword.IsAcceptable("aaaa"));
    }
}
