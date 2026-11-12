using System.Text.Json;
using System.Text.RegularExpressions;

namespace StreamsPlayer.Core.Tests;

public sealed class ExchangeVocabularyTests
{
    // The contract's words for the server's fan-out and the forwarded connection, in every shipped language. They stay in
    // code, logs and contract pointers; the French "prend le relais" (takes over) is an idiom, not the contract word.
    internal static readonly Regex TransportJargon = new(
        @"\b(?:relays?|tunnels?|rilancio|relé|túnel|túneis|retransmissor(?:es)?)\b|(?<!prend le )\brelais\b|ретрансля|тунн?ел|релей|релеї|中继|隧道|ریلے|ٹنل|रिले|टनल|টানেল|রিলে|(?<!\p{L})(?:نفق|مرحّ?ل)(?!\p{L})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [Theory]
    [InlineData("en", "Exchange server", "Broadcasting", "Cast to your own device")]
    [InlineData("ru", "Сервер обмена", "Вещание", "Cast на своё устройство")]
    [InlineData("uk", "Сервер обміну", "Мовлення", "Cast на свій пристрій")]
    public void FixedTermsMatchTheSharedDictionary(string code, string server, string broadcasting, string cast)
    {
        var values = LocalizationDictionary.Load(Path.Combine(LocalizationDictionary.Directory, $"Localization.{code}.xaml")).Values;
        Assert.Equal(server, values["ExchangeTitle"]);
        Assert.Equal(broadcasting, values["BroadcastTitle"]);
        Assert.Equal(cast, values["BroadcastCastMenu"]);
        Assert.Contains("Fast Media Sorter & Organizer for Android", values["ExchangeLoginOnly"]);
        Assert.Contains("Fast Media Sorter & Sharing for Windows", values["ExchangeLoginOnly"]);
    }

    // DEVICE-EXCHANGE item CW, "Drive channel": "via Google Drive" (short "via GD") in English, «через Google Drive» in
    // Russian and Ukrainian. The Drive keys sit outside the term map, so the three fixed forms are held here.
    [Theory]
    [InlineData("en", "Via Google Drive", "via Google Drive")]
    [InlineData("ru", "Через Google Drive", "через Google Drive")]
    [InlineData("uk", "Через Google Drive", "через Google Drive")]
    public void TheDriveChannelIsNamedWithTheDictionaryPreposition(string code, string title, string phrase)
    {
        var values = LocalizationDictionary.Load(Path.Combine(LocalizationDictionary.Directory, $"Localization.{code}.xaml")).Values;
        Assert.Equal(title, values["DriveTitle"]);
        foreach (var key in new[] { "DriveNoCast", "DrivePublishFailed", "BroadcastWhereDrive" })
        {
            // Ignoring case: the phrase opens a sentence in some strings ("Через Google Drive нельзя..") and ends one in others.
            Assert.Contains(phrase, values[key], StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void EveryExchangeKeyHasATermMapAndEveryLanguageAvoidsTransportJargon()
    {
        using var map = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "exchange-term-map.json")));
        var mapped = map.RootElement.GetProperty("groups").EnumerateObject()
            .SelectMany(group => group.Value.EnumerateArray().Select(key => key.GetString()!)).ToArray();
        Assert.Equal(mapped.Length, mapped.Distinct(StringComparer.Ordinal).Count());
        var problems = new List<string>();
        foreach (var dictionary in LocalizationDictionary.LoadAll())
        {
            var keys = dictionary.Values.Keys.Where(IsExchangeKey).Order(StringComparer.Ordinal);
            Assert.Equal(mapped.Order(StringComparer.Ordinal), keys);
            foreach (var key in mapped)
            {
                if (TransportJargon.IsMatch(dictionary.Values[key])) problems.Add($"{dictionary.Code}/{key}");
            }
        }
        Assert.True(problems.Count == 0, "User-facing transport jargon: " + string.Join(", ", problems));
    }

    private static bool IsExchangeKey(string key) => key.StartsWith("Exchange", StringComparison.Ordinal)
        || key.StartsWith("Broadcast", StringComparison.Ordinal)
        || key.StartsWith("FmsBroadcast", StringComparison.Ordinal)
        || key.StartsWith("MediaExchange", StringComparison.Ordinal);
}
