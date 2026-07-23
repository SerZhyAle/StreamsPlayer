# Phase 02 — Core ICY streaming reader

**Produces:** `StreamsPlayer.Core.IcyMetadataReader`
**Consumes:** Phase 01 (`IcyMetadataParser`); style reference `StreamCatalogService`

## Change

Create `src/StreamsPlayer.Core/IcyMetadataReader.cs`:

- `public sealed class IcyMetadataReader` with an injected `HttpClient`
  (constructor mirrors `StreamCatalogService`; the caller supplies a client whose
  `Timeout` is `Timeout.InfiniteTimeSpan` so a long-lived streaming read is
  bounded only by the token).
- `public async Task ReadAsync(string url, IProgress<string?> onTitleChanged,
  CancellationToken cancellationToken)`:
  1. Build `HttpRequestMessage(HttpMethod.Get, url)`, add header
     `Icy-MetaData: 1`.
  2. Send with `HttpCompletionOption.ResponseHeadersRead`, using a linked CTS that
     `CancelAfter(TimeSpan.FromSeconds(15))` **for the header phase only** (spec
     Part D connect timeout); the read loop below uses `cancellationToken`.
  3. Read `icy-metaint` from the response headers (`response.Headers` /
     `content` headers, non-standard header — read via `TryGetValues`). If absent
     or unparsable → return without reporting (AC #2: non-ICY stream is a clean
     no-op).
  4. Loop on `response.Content.ReadAsStream(...)`: skip `metaInt` audio bytes,
     read 1 length byte `L`, read `L * 16` metadata bytes, decode as UTF-8
     (lenient/replacement — encodings vary), call
     `IcyMetadataParser.ExtractStreamTitle`. Report via `onTitleChanged.Report`
     only when the value **changes** from the last reported value.
  5. Read fully into fixed buffers; treat a short/zero read as end-of-stream and
     return.
- **Never throws** for its own reason: wrap the body so `OperationCanceledException`
  (expected teardown) and any network/IO/parse exception are swallowed — metadata
  is best-effort and must not affect playback (AC #4, constraint "malformed /
  absent block must not interrupt audio"). Do not add logging (Core is log-free).
- Nothing is persisted or sent anywhere; the reader only reports in-memory
  strings to the caller (AC #5).

Keep the file under ~150 lines. No WPF, no App reference.

## Rationale

`MediaElement` cannot surface ICY, so a dedicated best-effort connection is the
only mechanism. Injecting `HttpClient` and reporting via `IProgress<string?>`
keeps Core platform-neutral while letting the App marshal updates to the UI
thread automatically (a `Progress<T>` constructed on the UI thread posts back to
it).

## Static check

`dotnet build src/StreamsPlayer.Core -c Debug`
expected: build succeeds | actual: Build succeeded, 0 Warning(s), 0 Error(s). Reader path additionally covered by loopback integration tests in Phase 03.
