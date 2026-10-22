using System.Text.Json;
using System.Text.Json.Serialization;

namespace StreamsPlayer.Core;

/// <summary>
/// DEVICE-EXCHANGE item AT: <c>password-change</c> on the control stream, carrying the old and the new password.
/// The two secrets are character arrays that are serialised straight into the frame and cleared by the caller, so
/// neither ever exists as an immutable string in this product (the same discipline as the enrollment's secret).
/// </summary>
[JsonConverter(typeof(ExchangePasswordChangeConverter))]
public sealed class ExchangePasswordChange(char[] oldPassword, char[] newPassword)
{
    internal char[] Old { get; } = oldPassword;
    internal char[] New { get; } = newPassword;

    public void Clear()
    {
        Array.Clear(Old);
        Array.Clear(New);
    }
}

internal sealed class ExchangePasswordChangeConverter : JsonConverter<ExchangePasswordChange>
{
    public override ExchangePasswordChange Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("A password change is only ever written.");

    public override void Write(Utf8JsonWriter writer, ExchangePasswordChange value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", 2);
        writer.WriteString("type", "password-change");
        writer.WriteString("oldPassword", value.Old);
        writer.WriteString("newPassword", value.New);
        writer.WriteEndObject();
    }
}
