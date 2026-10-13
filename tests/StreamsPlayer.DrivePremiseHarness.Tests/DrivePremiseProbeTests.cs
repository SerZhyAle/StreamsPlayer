using System.Text;
using System.Text.Json;
using StreamsPlayer.DrivePremiseHarness;

namespace StreamsPlayer.DrivePremiseHarness.Tests;

/// <summary>
/// The probe of SP-0202's premise experiment must be a DEVICE-EXCHANGE 8.1 wrapper in the exact shape of the
/// contract's Drive corpus, or the listing result says nothing: a file refused for its shape would read as the
/// premise failing. These tests hold the wrapper, the reader the listing prints, and the PKCE pair against the
/// RFC's own vector - all with no network, like every other check this repo runs in CI.
/// </summary>
public sealed class DrivePremiseProbeTests
{
    private static readonly DateTimeOffset WrittenAt = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Wrapper_CarriesTheCorpusMembers()
    {
        var broadcastId = "mFEAE7N2xCbgXQ3oxPtY2A";
        var wrapper = DrivePremiseProbe.BuildWrapper(broadcastId, WrittenAt);
        using var document = System.Text.Json.JsonDocument.Parse(wrapper);
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("broadcast", root.GetProperty("type").GetString());
        Assert.Equal("2026-10-14T12:00:00.000Z", root.GetProperty("writtenAt").GetString());
        Assert.Equal(DrivePremiseProbe.BroadcastTtlSeconds, root.GetProperty("ttlSeconds").GetInt32());
        var record = root.GetProperty("record");
        Assert.Equal(broadcastId, record.GetProperty("broadcastId").GetString());
        Assert.Equal(DrivePremiseProbe.ProbeDeviceId, record.GetProperty("deviceId").GetString());
        Assert.Equal("AUDIO_ONLY", record.GetProperty("mode").GetString());
        Assert.Equal("SP-0202 premise probe", record.GetProperty("title").GetString());
        Assert.Equal("2026-10-14T12:00:00Z", record.GetProperty("startedAt").GetString());
        Assert.Equal("2026-10-14T12:00:00Z", record.GetProperty("updatedAt").GetString());
        Assert.Equal(JsonValueKind.Object, record.GetProperty("descriptor").ValueKind);
    }

    [Fact]
    public void Wrapper_IsUnderTheContractRecordCeiling()
    {
        var wrapper = DrivePremiseProbe.BuildWrapper(DrivePremiseProbe.NewBroadcastId(), WrittenAt);
        Assert.True(Encoding.UTF8.GetByteCount(wrapper) <= DrivePremiseProbe.MaxWrapperBytes);
    }

    [Fact]
    public void FileName_IsTheCorpusBroadcastName()
    {
        Assert.Equal("fmsx-broadcast--DfFxPt42N4q_uTZ-zB66w.json", DrivePremiseProbe.FileNameFor("-DfFxPt42N4q_uTZ-zB66w"));
    }

    [Theory]
    [InlineData("fmsx-broadcast-abc.json", true)]
    [InlineData("fmsx-device-abc.json", true)]
    [InlineData("fmsx-broadcast-abc.txt", false)]
    [InlineData("fmsx-other-abc.json", false)]
    [InlineData("notes.json", false)]
    public void IsFmsxName_MatchesOnlyTheContractPrefixes(string name, bool expected)
    {
        Assert.Equal(expected, DrivePremiseProbe.IsFmsxName(name));
    }

    [Fact]
    public void TryParseWrapper_ReadsACorpusWrapper()
    {
        // The device-minimal case of vectors/records-v1.json, section 8.1: a wrapper written by the other side
        // of the contract, not by this harness - the reader must read it all the same.
        var corpus = """
            {"schemaVersion":1,"type":"device","writtenAt":"2026-10-07T12:00:00.000Z","ttlSeconds":1800,"record":{"deviceId":"j5AmM0pZUz46odtkwdXCgQ","deviceName":"Minimal","lastSeen":"2026-10-07T12:00:00Z","presence":"online","product":"streamsplayer-windows","updatedAt":"2026-10-07T12:00:00Z"}}
            """;
        Assert.True(DrivePremiseProbe.TryParseWrapper(Encoding.UTF8.GetBytes(corpus), out var wrapper));
        Assert.Equal(1, wrapper!.SchemaVersion);
        Assert.Equal("device", wrapper.Type);
        Assert.Equal(1800, wrapper.TtlSeconds);
        Assert.Equal("j5AmM0pZUz46odtkwdXCgQ", wrapper.DeviceId);
    }

    [Fact]
    public void TryParseWrapper_RejectsWhatHasNoVersionOrType()
    {
        Assert.False(DrivePremiseProbe.TryParseWrapper("{}"u8, out _));
        Assert.False(DrivePremiseProbe.TryParseWrapper("not json"u8, out _));
    }

    [Fact]
    public void NewBroadcastId_IsTheCorpusIdShape()
    {
        var id = DrivePremiseProbe.NewBroadcastId();
        Assert.Equal(22, id.Length);
        Assert.All(id.ToCharArray(), character => Assert.True(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'));
    }
}

/// <summary>The PKCE pair of the experiment's sign-in, against the RFC 7636 appendix B vector.</summary>
public sealed class DrivePremisePkceTests
{
    [Fact]
    public void Challenge_MatchesTheRfcVector()
    {
        Assert.Equal(
            "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            DrivePremisePkce.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
    }

    [Fact]
    public void NewVerifier_HasTheRfcLengthAndAlphabet()
    {
        var verifier = DrivePremisePkce.NewVerifier();
        Assert.Equal(DrivePremisePkce.VerifierLength, verifier.Length);
        Assert.All(verifier.ToCharArray(), character => Assert.True(char.IsAsciiLetterOrDigit(character) || character is '-' or '.' or '_' or '~'));
    }

    [Fact]
    public void NewState_IsReadableHex()
    {
        var state = DrivePremisePkce.NewState();
        Assert.Equal(32, state.Length);
        Assert.All(state.ToCharArray(), character => Assert.True(Uri.IsHexDigit(character)));
    }
}
