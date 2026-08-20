using System.Text;
using System.Text.Json;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0091, source contract item F. The manifest is what replaces a compiled-in asset revision, so what
/// is gated here is the two jobs it took over: naming the build that landed, and refusing a pair whose
/// halves came from different builds.
/// </summary>
public sealed class ArtworkManifestTests
{
    // The 2026-08-20 publish, copied from the live artwork-manifest.json. Kept whole rather than
    // minimised: a hand-written sample proves the parser reads what the parser expects, and the point of
    // this one is that it reads what the publisher actually emits - the "sets" nesting, a stamp that
    // happens to equal the tile pack's own hash, and a second set this client does not consume.
    private const string Published = """
    {
      "schemaVersion": 1,
      "generatedAt": "2026-08-20T09:44:49Z",
      "sets": {
        "channelPreview": {
          "stamp": "f954f493b7b3c07470787bb2798def420a1eeecf3ed2aed62cba2b14359f4905",
          "files": [
            {
              "name": "channel-preview-tiles.zip",
              "size": 14605058,
              "sha256": "f954f493b7b3c07470787bb2798def420a1eeecf3ed2aed62cba2b14359f4905"
            },
            {
              "name": "channel-preview-coords.json",
              "size": 201259,
              "sha256": "34b842b09a3ea58dcb3731135a1daffcbefd650acfb000b983e6fbfc1a6fcd40"
            }
          ]
        },
        "streamLogo": {
          "stamp": "64bed7f58d81244c6b16459712813358d7dc3e5782f53c37e17827dd6995ceed",
          "files": [
            {
              "name": "stream-logo-tiles.zip",
              "size": 10005648,
              "sha256": "64bed7f58d81244c6b16459712813358d7dc3e5782f53c37e17827dd6995ceed"
            }
          ]
        }
      }
    }
    """;

    [Fact]
    public void Parse_ReadsThePublishedManifest()
    {
        var manifest = ArtworkManifest.Parse(Published);

        Assert.Equal(1, manifest.SchemaVersion);
        Assert.Equal(DateTimeOffset.Parse("2026-08-20T09:44:49Z"), manifest.GeneratedAt);

        var previews = manifest.Set(ArtworkManifest.ChannelPreviewSet);
        Assert.Equal("f954f493b7b3c07470787bb2798def420a1eeecf3ed2aed62cba2b14359f4905", previews.Stamp);
        Assert.Equal(14605058, previews.File("channel-preview-tiles.zip").Size);
        Assert.Equal(201259, previews.File("channel-preview-coords.json").Size);
        Assert.NotNull(manifest.Set(ArtworkManifest.StreamLogoSet));
    }

    // The set we do not consume must not be able to break the set we do. A logo entry that goes
    // malformed in a later publish is the publisher's problem with the logo set, not an outage of the
    // channel previews.
    [Fact]
    public void Parse_KeepsAGoodSetWhenAnotherSetIsMalformed()
    {
        var manifest = ArtworkManifest.Parse("""
        {
          "schemaVersion": 1,
          "sets": {
            "channelPreview": { "stamp": "abc", "files": [ { "name": "t.zip", "size": 4, "sha256": "ff" } ] },
            "streamLogo": [ "not", "an", "object" ],
            "somethingNew": { "files": [] }
          }
        }
        """);

        Assert.Equal("abc", manifest.Set(ArtworkManifest.ChannelPreviewSet).Stamp);
        Assert.Throws<InvalidDataException>(() => manifest.Set(ArtworkManifest.StreamLogoSet));
    }

    // An unknown schemaVersion is not a reason to refuse: rejecting one would rebuild, on a different
    // field, exactly the pin this class exists to remove. The publisher bumps it for additions.
    [Fact]
    public void Parse_DoesNotRefuseAnUnknownSchemaVersion()
    {
        var manifest = ArtworkManifest.Parse("""
        {"schemaVersion": 99, "sets": {"channelPreview": {"stamp": "s", "files": [{"name":"t.zip","size":1,"sha256":"aa"}]}}}
        """);

        Assert.Equal(99, manifest.SchemaVersion);
        Assert.Equal("s", manifest.Set(ArtworkManifest.ChannelPreviewSet).Stamp);
    }

    // A file entry without a hash is the one thing that is never tolerated - it would let an unverified
    // payload through under the manifest's authority. Dropped at parse, and named when it is asked for.
    [Fact]
    public void File_NamesTheEntryItCannotFindRatherThanReturningNull()
    {
        var set = ArtworkManifest
            .Parse("""{"sets":{"channelPreview":{"stamp":"s","files":[{"name":"t.zip","size":1}]}}}""")
            .Set(ArtworkManifest.ChannelPreviewSet);

        var error = Assert.Throws<InvalidDataException>(() => set.File("t.zip"));
        Assert.Contains("t.zip", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Set_NamesTheSetsItHasWhenTheOneAskedForIsAbsent()
    {
        var manifest = ArtworkManifest.Parse("""{"schemaVersion":2,"sets":{"streamLogo":{"stamp":"s","files":[]}}}""");

        var error = Assert.Throws<InvalidDataException>(() => manifest.Set(ArtworkManifest.ChannelPreviewSet));
        Assert.Contains("streamLogo", error.Message, StringComparison.Ordinal);
        Assert.Contains("schemaVersion 2", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[1,2,3]")]
    [InlineData("\"text\"")]
    public void Parse_TreatsBlankOrNonObjectAsNothingPublished(string json)
    {
        Assert.Empty(ArtworkManifest.Parse(json).Sets);
    }

    [Fact]
    public void Parse_ThrowsOnMalformedJson()
    {
        Assert.ThrowsAny<JsonException>(() => ArtworkManifest.Parse("{not json"));
    }

    // NIST vector for "abc", so the check is against a hash this test did not compute itself.
    private const string AbcSha256 = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    [Fact]
    public void Verify_AcceptsTheDeclaredBytesInEitherHexCase()
    {
        var payload = Encoding.UTF8.GetBytes("abc");

        new ArtworkFile("t.bin", 3, AbcSha256).Verify(payload);
        new ArtworkFile("t.bin", 3, AbcSha256.ToUpperInvariant()).Verify(payload);
    }

    // The torn pair this exists for: both halves answer 200, both are internally valid, and their index
    // spaces disagree. Nothing downstream can notice, so it has to be refused here.
    [Fact]
    public void Verify_RefusesBytesFromAnotherBuild()
    {
        var file = new ArtworkFile("channel-preview-tiles.zip", 3, AbcSha256);

        var error = Assert.Throws<InvalidDataException>(() => file.Verify(Encoding.UTF8.GetBytes("abd")));
        Assert.Contains("channel-preview-tiles.zip", error.Message, StringComparison.Ordinal);
        Assert.Contains("sha256", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_RefusesAShortTransferBySizeBeforeHashing()
    {
        var error = Assert.Throws<InvalidDataException>(
            () => new ArtworkFile("t.bin", 3, AbcSha256).Verify(Encoding.UTF8.GetBytes("ab")));

        Assert.Contains("2 bytes", error.Message, StringComparison.Ordinal);
        Assert.Contains("declares 3", error.Message, StringComparison.Ordinal);
    }
}
