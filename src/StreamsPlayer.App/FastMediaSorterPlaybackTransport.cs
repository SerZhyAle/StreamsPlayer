using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace StreamsPlayer.App;

/// <summary>
/// Opens one FastMediaSorter broadcast leg and exposes both its first HTTP response and body.
/// The caller owns the returned connection until playback ends; no status or metadata request is made.
/// </summary>
internal sealed class FastMediaSorterPlaybackTransport
{
    private static readonly HttpClient Client = CreateClient();

    public async Task<FastMediaSorterPlaybackConnection> OpenAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.UserAgent.ParseAdd("StreamsPlayer/0.1");

        try
        {
            var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var elapsed = stopwatch.Elapsed;
            if (!response.IsSuccessStatusCode)
            {
                return new FastMediaSorterPlaybackConnection(endpoint, (int)response.StatusCode, elapsed, response, null, null);
            }

            try
            {
                var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                return new FastMediaSorterPlaybackConnection(endpoint, (int)response.StatusCode, elapsed, response, stream, null);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
            {
                response.Dispose();
                return new FastMediaSorterPlaybackConnection(endpoint, (int)response.StatusCode, elapsed, null, null, ex);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            return new FastMediaSorterPlaybackConnection(endpoint, null, stopwatch.Elapsed, null, null, ex);
        }
    }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }
}

/// <summary>
/// The resource owned by one playback leg. <see cref="Stream"/> is the response body from the same GET
/// whose status is exposed by <see cref="StatusCode"/>.
/// </summary>
internal sealed class FastMediaSorterPlaybackConnection : IDisposable
{
    private HttpResponseMessage? _response;
    private Stream? _stream;

    internal FastMediaSorterPlaybackConnection(
        Uri endpoint,
        int? statusCode,
        TimeSpan responseElapsed,
        HttpResponseMessage? response,
        Stream? stream,
        Exception? transportError)
    {
        Endpoint = endpoint;
        StatusCode = statusCode;
        ResponseElapsed = responseElapsed;
        _response = response;
        _stream = stream is null ? null : new ObservedReadStream(stream, OnEndOfStream, OnReadError);
        TransportError = transportError;
    }

    public Uri Endpoint { get; }
    public int? StatusCode { get; }
    public TimeSpan ResponseElapsed { get; }
    public Stream? Stream => _stream;
    public Exception? TransportError { get; private set; }
    public bool EndOfStream { get; private set; }

    private void OnEndOfStream() => EndOfStream = true;

    private void OnReadError(Exception exception) => TransportError ??= exception;

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
        _response?.Dispose();
        _response = null;
    }

    private sealed class ObservedReadStream(Stream inner, Action endOfStream, Action<Exception> readError) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.FromException(new NotSupportedException());
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            try
            {
                var read = inner.Read(buffer, offset, count);
                Observe(read);
                return read;
            }
            catch (Exception ex)
            {
                readError(ex);
                throw;
            }
        }

        public override int Read(Span<byte> buffer)
        {
            try
            {
                var read = inner.Read(buffer);
                Observe(read);
                return read;
            }
            catch (Exception ex)
            {
                readError(ex);
                throw;
            }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                var read = await inner.ReadAsync(buffer, cancellationToken);
                Observe(read);
                return read;
            }
            catch (Exception ex)
            {
                readError(ex);
                throw;
            }
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            try
            {
                var read = await inner.ReadAsync(buffer, offset, count, cancellationToken);
                Observe(read);
                return read;
            }
            catch (Exception ex)
            {
                readError(ex);
                throw;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        private void Observe(int read)
        {
            if (read == 0)
            {
                endOfStream();
            }
        }
    }
}
