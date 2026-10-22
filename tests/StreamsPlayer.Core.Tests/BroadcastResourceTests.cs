using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// 196 phase 4, the Core half: the `.spres` reader, the URL composition, the de-duplicating import,
/// the export-safety widening, the additive channel fields and the launch file target. The reader
/// must never throw on hostile input - a malformed document is a status, not an exception.
/// </summary>
public sealed class BroadcastResourceTests
{
    private const string SampleDocument =
        """
        {
          "schemaVersion": 1,
          "kind": "broadcast",
          "resourceName": "Dune (2021)",
          "protocol": "http",
          "mediaKind": "video",
          "playlist": false,
          "itemCount": 1,
          "accessPaths": [
            { "kind": "lan", "host": "192.168.1.100", "port": 55272 },
            { "kind": "ipv6", "host": "2a02::1", "port": 55272 },
            { "kind": "portforward", "host": "46.54.0.135", "port": 55272 }
          ],
          "path": "/b/hT9xAbCd",
          "durationSeconds": 8520,
          "expiresAt": "2026-08-09T21:00:00Z",
          "accessNote": "Forward port 55272",
          "createdAt": "2026-08-08T21:00:00Z"
        }
        """;

    // ---- reader ----------------------------------------------------------------

    [Fact]
    public void The_document_lifetime_ends_at_its_expiry_instant()
    {
        var resource = BroadcastResourceReader.Read(SampleDocument).Resource!;
        var expiry = resource.ExpiresAt!.Value;
        Assert.False(resource.IsExpiredAt(expiry.AddTicks(-1)));
        Assert.True(resource.IsExpiredAt(expiry));
        Assert.True(resource.IsExpiredAt(expiry.AddTicks(1)));
    }

    [Theory]
    [InlineData("/b/token?secret=other")]
    [InlineData("/b/token/../other")]
    [InlineData("//other.example/b/token")]
    public void A_resource_path_cannot_inject_another_address_or_request(string path)
    {
        var text = SampleDocument.Replace("/b/hT9xAbCd", path, StringComparison.Ordinal);
        Assert.Equal(BroadcastResourceStatus.InvalidDocument, BroadcastResourceReader.Read(text).Status);
    }

    [Theory]
    [InlineData("user:password@other.example")]
    [InlineData("other.example/path")]
    [InlineData("other.example?query")]
    [InlineData("[other.example")]
    [InlineData("[other.example]")]
    public void A_host_cannot_inject_credentials_or_a_path(string host)
    {
        var text = SampleDocument.Replace("192.168.1.100", host, StringComparison.Ordinal)
            .Replace("2a02::1", host, StringComparison.Ordinal)
            .Replace("46.54.0.135", host, StringComparison.Ordinal);
        Assert.Equal(BroadcastResourceStatus.InvalidDocument, BroadcastResourceReader.Read(text).Status);
    }

    [Fact]
    public void A_compressed_payload_is_bounded_while_inflating()
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionMode.Compress, leaveOpen: true))
        {
            gzip.Write(Encoding.UTF8.GetBytes(new string(' ', BroadcastResourceReader.MaximumDocumentBytes + 1)));
        }

        var text = BroadcastResourceReader.CompressedPrefix + Convert.ToBase64String(output.ToArray());
        Assert.Equal(BroadcastResourceStatus.InvalidDocument, BroadcastResourceReader.Read(text).Status);
    }

    [Fact]
    public void An_escaped_lone_surrogate_is_a_refusal_instead_of_an_exception()
    {
        var text = SampleDocument.Replace("Dune (2021)", "\\uD800", StringComparison.Ordinal);
        Assert.Equal(BroadcastResourceStatus.InvalidDocument, BroadcastResourceReader.Read(text).Status);
    }

    [Fact]
    public void A_valid_document_reads_with_every_fact()
    {
        var read = BroadcastResourceReader.Read(SampleDocument);
        Assert.True(read.IsAccepted);
        var resource = read.Resource!;
        Assert.Equal(1, resource.SchemaVersion);
        Assert.Equal("Dune (2021)", resource.ResourceName);
        Assert.Equal("video", resource.MediaKind);
        Assert.False(resource.Playlist);
        Assert.Equal(1, resource.ItemCount);
        Assert.Equal("/b/hT9xAbCd", resource.Path);
        Assert.Equal(8520, resource.DurationSeconds);
        Assert.Equal(3, resource.AccessPaths.Count);
        Assert.Equal("Forward port 55272", resource.AccessNote);
        Assert.NotNull(resource.ExpiresAt);
        Assert.NotNull(resource.CreatedAt);
        Assert.False(resource.IsExpiredAt(DateTimeOffset.Parse("2026-08-09T20:00:00Z", CultureInfo.InvariantCulture)));
        Assert.True(resource.IsExpiredAt(DateTimeOffset.Parse("2026-08-09T22:00:00Z", CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void An_empty_lifetime_is_until_revoked()
    {
        var document = """
            { "schemaVersion": 1, "kind": "broadcast", "protocol": "http", "mediaKind": "audio",
              "itemCount": 1, "accessPaths": [{ "kind": "lan", "host": "192.168.1.100", "port": 55272 }],
              "path": "/b/tok", "expiresAt": "" }
            """;
        var resource = BroadcastResourceReader.Read(document).Resource!;
        Assert.Null(resource.ExpiresAt);
        Assert.False(resource.HasLifetime);
    }

    [Fact]
    public void A_newer_schema_version_is_refused_by_name()
    {
        var document = SampleDocument.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2");
        var read = BroadcastResourceReader.Read(document);
        Assert.Equal(BroadcastResourceStatus.UnsupportedVersion, read.Status);
        Assert.Null(read.Resource);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"some\":\"other document\"}")]
    public void Anything_else_is_not_a_resource_document(string text)
    {
        Assert.Equal(BroadcastResourceStatus.NotResourceDocument, BroadcastResourceReader.Read(text).Status);
    }

    [Fact]
    public void The_kind_discriminator_is_required()
    {
        var document = SampleDocument.Replace("\"kind\": \"broadcast\",", "");
        Assert.Equal(BroadcastResourceStatus.NotResourceDocument, BroadcastResourceReader.Read(document).Status);
    }

    [Fact]
    public void A_protocol_other_than_http_is_an_invalid_document()
    {
        var document = SampleDocument.Replace("\"protocol\": \"http\"", "\"protocol\": \"https\"");
        Assert.Equal(BroadcastResourceStatus.InvalidDocument, BroadcastResourceReader.Read(document).Status);
    }

    [Theory]
    [InlineData("\"path\": \"b/no-slash\"")]
    [InlineData("\"path\": \"\"")]
    [InlineData("\"itemCount\": 0")]
    [InlineData("\"mediaKind\": \"film\"")]
    [InlineData("\"accessPaths\": []")]
    [InlineData("\"expiresAt\": \"next tuesday\"")]
    public void A_v1_document_that_is_not_readable_is_invalid(string broken)
    {
        var document = SampleDocument.Replace("\"path\": \"/b/hT9xAbCd\"", broken);
        Assert.Equal(BroadcastResourceStatus.InvalidDocument, BroadcastResourceReader.Read(document).Status);
    }

    [Fact]
    public void Unknown_access_path_kinds_are_skipped_and_unknown_fields_ignored()
    {
        var document = """
            { "schemaVersion": 1, "kind": "broadcast", "protocol": "http", "mediaKind": "video",
              "itemCount": 1, "aFieldOfTheFuture": { "anything": true },
              "accessPaths": [
                { "kind": "quic", "host": "203.0.113.9", "port": 55272 },
                { "kind": "lan", "host": "192.168.1.100", "port": 55272 },
                { "kind": "lan", "host": "", "port": 55272 }
              ],
              "path": "/b/tok" }
            """;
        var resource = BroadcastResourceReader.Read(document).Resource!;
        Assert.Single(resource.AccessPaths);
        Assert.Equal("lan", resource.AccessPaths[0].Kind);
    }

    [Fact]
    public void A_compressed_qr_payload_reads_to_the_same_resource()
    {
        var compressed = Convert.ToBase64String(Gzip(Encoding.UTF8.GetBytes(SampleDocument)));
        var read = BroadcastResourceReader.Read("SPRES1:" + compressed);
        Assert.True(read.IsAccepted);
        Assert.Equal("Dune (2021)", read.Resource!.ResourceName);
    }

    [Fact]
    public void A_broken_compressed_payload_is_invalid_not_silent()
    {
        Assert.Equal(BroadcastResourceStatus.InvalidDocument, BroadcastResourceReader.Read("SPRES1:not base64 !").Status);
    }

    [Fact]
    public async Task Hostile_input_never_throws()
    {
        var hostile = new[]
        {
            new string('{', 5000),
            new string('x', 10_000_000),
            "{\"schemaVersion\":1,\"kind\":\"broadcast\",\"protocol\":\"http\",\"path\":\"/b\",\"accessPaths\":" + new string('[', 200) + "}",
            "SPRES1:" + new string('A', 5000),
            "\u0000\u0001\u0002"
        };
        foreach (var text in hostile)
        {
            // The statuses may differ; the contract is that each is answered, never thrown.
            var status = await Task.Run(() => BroadcastResourceReader.Read(text).Status);
            Assert.True(Enum.IsDefined(status));
        }
    }

    // ---- URL composition ----------------------------------------------------------

    [Fact]
    public void The_url_is_composed_and_an_ipv6_host_is_bracketed()
    {
        Assert.Equal("http://192.168.1.100:55272/b/hT9xAbCd", BroadcastUrl.Compose("192.168.1.100", 55272, "/b/hT9xAbCd"));
        Assert.Equal("http://[2a02::1]:55272/b/hT9xAbCd", BroadcastUrl.Compose("2a02::1", 55272, "/b/hT9xAbCd"));
        Assert.Equal("http://[2a02::1]:55272/b/hT9xAbCd", BroadcastUrl.Compose("[2a02::1]", 55272, "/b/hT9xAbCd"));
    }

    [Fact]
    public void Every_access_path_composes_in_document_order()
    {
        var resource = BroadcastResourceReader.Read(SampleDocument).Resource!;
        var urls = BroadcastUrl.ComposeAll(resource);
        Assert.Equal(3, urls.Count);
        Assert.Equal("http://192.168.1.100:55272/b/hT9xAbCd", urls[0]);
        Assert.Equal("http://[2a02::1]:55272/b/hT9xAbCd", urls[1]);
        Assert.Equal("http://46.54.0.135:55272/b/hT9xAbCd", urls[2]);
    }

    [Fact]
    public async Task The_race_returns_the_first_address_that_answers_not_the_first_stored()
    {
        var dead = "http://127.0.0.1:1/b/tok"; // nothing listens on the discard.. nothing port
        using var live = new MinimalHttpServer();
        using var client = new HttpClient();

        // The DEAD address is stored first: the race must still find the live one.
        var winner = await BroadcastUrl.RaceAsync(client, [dead, live.Url], TimeSpan.FromSeconds(10));
        Assert.Equal(live.Url, winner);
    }

    [Fact]
    public async Task The_race_falls_back_to_a_ranged_get_when_the_server_refuses_head()
    {
        using var server = new MinimalHttpServer(allowHead: false);
        using var client = new HttpClient();
        var winner = await BroadcastUrl.RaceAsync(client, [server.Url], TimeSpan.FromSeconds(10));
        Assert.Equal(server.Url, winner);
    }

    [Fact]
    public async Task A_broadcast_that_answers_nothing_races_to_null()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        Assert.Null(await BroadcastUrl.RaceAsync(client, ["http://127.0.0.1:1/b/tok"], TimeSpan.FromMilliseconds(500)));
    }

    // ---- import -----------------------------------------------------------------

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-12T12:00:00Z", CultureInfo.InvariantCulture);

    [Fact]
    public void An_import_adds_one_imported_row_with_the_alternates_and_the_key()
    {
        var resource = BroadcastResourceReader.Read(SampleDocument).Resource!;
        var applied = BroadcastChannelImport.Apply([], resource, Now);
        Assert.True(applied.Added);
        var channel = applied.Channel;
        Assert.Equal(SourceOrigin.Imported, channel.SourceOrigin);
        Assert.Equal(MediaKind.Video, channel.MediaKind);
        Assert.Equal("http://192.168.1.100:55272/b/hT9xAbCd", channel.Url);
        Assert.Equal(2, channel.AlternateUrls!.Count);
        Assert.Equal("/b/hT9xAbCd", channel.ResourceKey);
        Assert.Equal(resource.ExpiresAt, channel.ResourceExpiresAt);
        Assert.False(channel.IsPlaylistResource);
    }

    [Fact]
    public void A_reimported_document_updates_the_row_instead_of_adding_a_second_one()
    {
        var resource = BroadcastResourceReader.Read(SampleDocument).Resource!;
        var first = BroadcastChannelImport.Apply([], resource, Now);
        var existing = first.Channels;

        // A refreshed document: new addresses (the PC moved), same resource path, same title.
        var refreshedDocument = SampleDocument
            .Replace("192.168.1.100", "10.0.0.7")
            .Replace("\"resourceName\": \"Dune (2021)\"", "\"resourceName\": \"Dune (2021) - recut\"");
        var refreshed = BroadcastResourceReader.Read(refreshedDocument).Resource!;

        var second = BroadcastChannelImport.Apply(existing, refreshed, Now.AddDays(1));
        Assert.False(second.Added);
        Assert.Single(second.Channels);
        Assert.Equal(first.Channel.Id, second.Channel.Id);
        Assert.Equal("http://10.0.0.7:55272/b/hT9xAbCd", second.Channel.Url);
        Assert.Equal("Dune (2021) - recut", second.Channel.Title);
        Assert.Equal(Now, second.Channel.AddedAt); // the row's own history survives the refresh
    }

    [Fact]
    public void A_different_resource_adds_a_second_row()
    {
        var first = BroadcastChannelReaderResource(SampleDocument);
        var second = BroadcastChannelReaderResource(SampleDocument.Replace("/b/hT9xAbCd", "/b/otherToken"));
        var applied = BroadcastChannelImport.Apply(
            BroadcastChannelImport.Apply([], first, Now).Channels, second, Now);
        Assert.True(applied.Added);
        Assert.Equal(2, applied.Channels.Count);
    }

    private static BroadcastResource BroadcastChannelReaderResource(string document) =>
        BroadcastResourceReader.Read(document).Resource!;

    // ---- export safety ------------------------------------------------------------

    [Fact]
    public void A_broadcast_row_is_a_secret_bearing_row_for_the_export_gate()
    {
        var resource = BroadcastResourceReader.Read(SampleDocument).Resource!;
        var channel = BroadcastChannelImport.BuildChannel(resource, Now);

        // The URL has no user:pass@ - the credential rule alone does not see the token in the path.
        Assert.False(CatalogUrlIdentity.HasCredentials(channel.Url));

        // The row gate does: a ResourceKey row gets the same export confirmation.
        Assert.Contains(ChannelHandOutWarnings.ExportCredentialKey, ChannelHandOutWarnings.ExportKeysForRows([channel]));

        // A plain user row still needs no confirmation.
        var manual = new StreamChannel
        {
            Id = Guid.NewGuid(), Url = "http://example.test/stream", Title = "Radio",
            MediaKind = MediaKind.Audio, SourceOrigin = SourceOrigin.Manual, SortIndex = 0, AddedAt = Now
        };
        Assert.Empty(ChannelHandOutWarnings.ExportKeysForRows([manual]));
    }

    // ---- additive channel fields ----------------------------------------------------

    [Fact]
    public void An_older_state_file_deserializes_to_the_initializer_defaults()
    {
        // The pre-broadcast shape of a row: no alternateUrls, no resourceKey, no expiresAt, no playlist flag.
        var legacy = """
            {"id":"8ef1b0ab-2a48-4353-8fff-9b6e02d1c14f","url":"http://example.test/stream","title":"Radio",
             "mediaKind":0,"sourceOrigin":1,"sortIndex":0,"pinned":false,
             "addedAt":"2026-01-01T00:00:00Z","faviconSource":0,"access":0}
            """;
        var channel = JsonSerializer.Deserialize<StreamChannel>(legacy, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(channel);
        Assert.Null(channel.AlternateUrls);
        Assert.Null(channel.ResourceKey);
        Assert.Null(channel.ResourceExpiresAt);
        Assert.False(channel.IsPlaylistResource);
        Assert.Null(channel.ResourceAccessNote);
    }

    // ---- launch -------------------------------------------------------------------

    [Fact]
    public void A_single_spres_argument_is_a_file_launch_target()
    {
        var request = StreamLaunchRequest.Parse(["C:\\Users\\me\\Film.spres"]);
        Assert.Equal(StreamLaunchTargetKind.File, request.Kind);
        Assert.Equal("C:\\Users\\me\\Film.spres", request.File);

        Assert.Equal(StreamLaunchTargetKind.Invalid, StreamLaunchRequest.Parse(["C:\\Users\\me\\notes.txt"]).Kind);
        Assert.Equal(StreamLaunchTargetKind.Url, StreamLaunchRequest.Parse(["--url", "http://example.test/live"]).Kind);
        Assert.Equal(StreamLaunchTargetKind.Invalid, StreamLaunchRequest.Parse(["--url", "C:\\file.spres"]).Kind);
    }

    // ---- helpers ----------------------------------------------------------------

    private static byte[] Gzip(byte[] input)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionMode.Compress, leaveOpen: true))
        {
            gzip.Write(input);
        }

        return output.ToArray();
    }

    /// <summary>
    /// A minimal HTTP responder over a raw socket - no HttpListener, so no URL ACL is needed and the
    /// test runs anywhere the suite runs. Answers HEAD (or refuses it) and GET, exactly like the
    /// gateway: 200, Content-Length, no body for HEAD.
    /// </summary>
    private sealed class MinimalHttpServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private readonly bool _allowHead;

        public MinimalHttpServer(bool allowHead = true)
        {
            _allowHead = allowHead;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/b/tok";
            _loop = Task.Run(AcceptLoopAsync);
        }

        public string Url { get; }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (SocketException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                _ = ServeAsync(client);
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var buffer = new byte[4096];
                    var read = await stream.ReadAsync(buffer, _cts.Token);
                    var request = Encoding.ASCII.GetString(buffer, 0, read);
                    var method = request.Split(' ')[0];
                    var response = (method == "HEAD" && !_allowHead)
                        ? "HTTP/1.1 405 Method Not Allowed\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                        : "HTTP/1.1 200 OK\r\nContent-Length: 8\r\nAccept-Ranges: bytes\r\nConnection: close\r\n\r\npayload";
                    var bytes = Encoding.ASCII.GetBytes(response);
                    await stream.WriteAsync(bytes, _cts.Token);
                    await stream.FlushAsync(_cts.Token);
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
                {
                    // a client that hangs up early is not a failure of the test server
                }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            try
            {
                _loop.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
            }

            _cts.Dispose();
        }
    }
}
