using System.Text.Json;
using System.Text.Json.Serialization;

namespace StreamsPlayer.Core;

/// <summary>
/// SP-0035: reads one persisted enum without ever aborting the document.
/// <para>
/// <see cref="JsonStringEnumConverter"/> throws on a name it does not know, and a
/// <see cref="JsonException"/> anywhere in <c>catalog-state.json</c> fails the whole deserialization -
/// so the app falls back to an empty state and the next save overwrites the user's real catalog,
/// collections, history and preferences with nothing. The trigger is ordinary: a state file written by
/// a newer build and read by an older one. SP-0034 fixed that for the language field alone; this is the
/// same recovery for every other enum the state carries, so adding a member to any of them stays a
/// cheap, non-destructive change.
/// </para>
/// <para>
/// An unreadable value degrades to a caller-supplied fallback - the value that field would hold on a
/// fresh install - so one unknown token costs one field, never the file. Numbers outside the defined
/// members are refused for the same reason the language converter refuses them: silently casting one
/// keeps a value in the state that no UI can match.
/// </para>
/// </summary>
/// <param name="fallback">Value substituted for anything unreadable; use the field's own default.</param>
public sealed class TolerantEnumConverter<TEnum>(TEnum fallback) : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return Enum.TryParse<TEnum>(reader.GetString(), ignoreCase: true, out var parsed)
                    && Enum.IsDefined(parsed)
                        ? parsed
                        : fallback;

            case JsonTokenType.Number:
                return reader.TryGetInt32(out var number) && Enum.IsDefined(typeof(TEnum), number)
                    ? (TEnum)Enum.ToObject(typeof(TEnum), number)
                    : fallback;

            case JsonTokenType.Null:
                return fallback;

            default:
                // Structural garbage where an enum belongs is still only a lost field. Skipping keeps the
                // reader positioned on the value's end so the rest of the document reads normally.
                reader.Skip();
                return fallback;
        }
    }

    /// <summary>
    /// Always writes the member name, matching what <see cref="JsonStringEnumConverter"/> wrote before -
    /// the persisted tokens are a compatibility contract with older builds. A value outside the enum
    /// (a cast integer that reached memory some other way) is normalized to the fallback rather than
    /// persisted, so the file never carries a token this build itself cannot read back.
    /// </summary>
    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
        writer.WriteStringValue(Enum.IsDefined(value) ? value.ToString() : fallback.ToString());
}

/// <summary>
/// SP-0035: the same tolerance for an optional enum, where "unreadable" has an honest representation -
/// absent. Used where a substituted value would be a claim the state does not support: an unknown
/// <see cref="PlayOutcome"/> must not be read as a recorded failure.
/// </summary>
public sealed class TolerantNullableEnumConverter<TEnum> : JsonConverter<TEnum?>
    where TEnum : struct, Enum
{
    public override TEnum? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return Enum.TryParse<TEnum>(reader.GetString(), ignoreCase: true, out var parsed)
                    && Enum.IsDefined(parsed)
                        ? parsed
                        : null;

            case JsonTokenType.Number:
                return reader.TryGetInt32(out var number) && Enum.IsDefined(typeof(TEnum), number)
                    ? (TEnum)Enum.ToObject(typeof(TEnum), number)
                    : null;

            case JsonTokenType.Null:
                return null;

            default:
                reader.Skip();
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, TEnum? value, JsonSerializerOptions options)
    {
        if (value is not { } definite || !Enum.IsDefined(definite))
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStringValue(definite.ToString());
    }
}
