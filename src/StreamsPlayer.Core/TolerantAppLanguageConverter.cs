using System.Text.Json;
using System.Text.Json.Serialization;

namespace StreamsPlayer.Core;

/// <summary>
/// SP-0034 decision 7: reads <see cref="CatalogState.Language"/> without ever throwing.
/// <para>
/// The default <see cref="JsonStringEnumConverter"/> throws <see cref="JsonException"/> on a name it
/// does not know, and that exception aborts the whole document - so a state file written by a newer
/// build ("language": "Hindi") made an older build fail to load its catalog, collections, history and
/// window preferences, and the first save afterwards overwrote the user's real file with an empty one.
/// The recovery has to be at field level: an unreadable preference is a preference we do not have.
/// </para>
/// <para>
/// It also rejects numbers outside the defined members. Left to the default converter, an integer was
/// accepted silently and lived on in the state as a cast value no picker could match.
/// </para>
/// </summary>
public sealed class TolerantAppLanguageConverter : JsonConverter<AppLanguage?>
{
    public override AppLanguage? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;

            case JsonTokenType.String:
                var name = reader.GetString();
                return Enum.TryParse<AppLanguage>(name, ignoreCase: true, out var parsed)
                    && InterfaceLanguages.IsShipped(parsed)
                        ? parsed
                        : null;

            case JsonTokenType.Number:
                return reader.TryGetInt32(out var number)
                    && InterfaceLanguages.IsShipped((AppLanguage)number)
                        ? (AppLanguage)number
                        : null;

            default:
                // Structural garbage where a language belongs is still only a lost preference. Skip the
                // value so the reader stays positioned and the rest of the document survives.
                reader.Skip();
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, AppLanguage? value, JsonSerializerOptions options)
    {
        if (value is null || !InterfaceLanguages.IsShipped(value.Value))
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStringValue(value.Value.ToString());
    }
}
