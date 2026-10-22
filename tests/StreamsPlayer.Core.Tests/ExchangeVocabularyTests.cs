using System.Text.Json;
using System.Text.RegularExpressions;

namespace StreamsPlayer.Core.Tests;

public sealed class ExchangeVocabularyTests
{
    private static readonly Regex TransportJargon = new(
        @"\b(?:relays?|tunnels?|relais|rilancio|relé|túnel|túneis)\b|ретрансля|тунн?ел|релей|релеї|中继|隧道|ریلے|ٹنل|रिले|टनल|টানেল|রিলে",
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
