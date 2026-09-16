# Streams Source Spec — 11 — Live Broadcast Contract

**Source:** FastMediaSorter, ticket S3050.  
**Copied revision:** 2026-09-12.  
**Update rule:** FastMediaSorter notifies consumers when a section moves from `[PROPOSED]` to `[CONTRACT]`.
Replace this copy from the producer's contract, then rerun the matching StreamsPlayer checks. This document
is a consumer contract, not FastMediaSorter application code.

Sections marked **[CONTRACT]** bind every consumer now. Sections marked **[PROPOSED]** describe a future
producer shape and bind nobody until a later revision marks them contract. Scope is live broadcasts only;
the on-demand catalog is specified separately.

## 1. Live broadcast

A FastMediaSorter phone or watch captures its microphone (and later camera) and serves the result directly.
The consumer connects to the device. Nothing is stored or replayed: the listener hears capture from the
moment it connects.

There are three equivalent hand-off channels — link, import file and barcode — and direct LAN is today's
transport. Relay/P2P through a data-exchange server is planned, but does not alter the direct LAN contract.

## 2. Stream kinds

### 2.1 Phone audio **[CONTRACT]** — shipped

- URL: `http://<lan-ipv4>:<port>/live-audio.aac`; `/live-audio` is also accepted. Default port is `8768`
  and is user-configurable.
- Response: `HTTP/1.1 200 OK`, `Content-Type: audio/aac`, `Transfer-Encoding: chunked`,
  `Accept-Ranges: none`, `icy-name`, `icy-genre`, `icy-pub`, `icy-br`; there is no `icy-metaint`.
- Payload: ADTS-framed AAC-LC, mono, 44 100 Hz, normally 128 kbit/s. A late joiner can decode from the
  next frame.
- There is no listener cap. The chunked body ends cleanly when broadcast stops.

### 2.2 Watch audio **[CONTRACT]** — shipped

- URL: `http://<lan-ipv4>:<port>/listen`. The port can change between broadcasts.
- Response: `HTTP/1.0 200 OK`, `Content-Type: audio/aac`, `Cache-Control: no-cache`, `Connection: close`;
  there is no `Content-Length` and no chunking.
- Payload: ADTS AAC-LC, mono, 44 100 Hz, 64 kbit/s.
- At most four listeners are allowed. A fifth gets `HTTP/1.0 503 Service Unavailable` and is closed.
  Every additional request a consumer makes to the same URL — including ICY metadata or health probe —
  spends one listener slot.
- The socket closes without a terminator when broadcast stops; the consumer sees EOF or reset.

### 2.3 Phone audio + video **[PROPOSED]**

RTSP, H.264 video plus AAC audio, RTP over TCP, default port 8554. Consumers must not assume path or SDP
until this section is `[CONTRACT]`.

### 2.4 Phone video without audio **[PROPOSED]**

As 2.3, without an audio track.

## 3. Broadcast descriptor

### 3.1 Shipped shape, `schemaVersion` 1 **[CONTRACT]**

The descriptor is one UTF-8 JSON object:

- `schemaVersion`: integer `1`.
- `url`: required non-blank string, address of section 2.
- `mode`: required non-blank string; `AUDIO_ONLY` today, `VIDEO_AUDIO` and `VIDEO_ONLY` reserved.
- `title`: optional string.
- `sourceId`: optional stable broadcasting-device id. A consumer that stores broadcasts replaces the
  stored one with matching `sourceId` instead of adding a duplicate.

Unknown members are ignored. A `schemaVersion` above the consumer's supported value is refused with an
update-the-app explanation, not “invalid”. A mode the consumer does not support is refused clearly; a
consumer must never infer transport or mode from URL spelling.

### 3.2 Descriptor v2 fields **[CONTRACT]** — shipped

`schemaVersion` remains `1`. The following are optional additive fields:

- top-level `isLive` boolean and `targetLatencyMs` long;
- `endpoints` list; every endpoint may declare `url`, `transport` (`HTTP`, `RTSP`, `P2P`), `mode`,
  `videoCodec`, `audioCodec`, `sampleRate`, `bitrate`, `isLive` and `targetLatencyMs`.

Legacy consumers continue to read the top-level `url` and `mode` and ignore the new fields.

## 4. Hand-off channels

### 4.1 Barcode **[CONTRACT]**

Payload is `FMSBCAST1:` followed by standard-Base64 of GZIP-compressed descriptor JSON.

### 4.2 Import file **[CONTRACT]**

The file is plain descriptor JSON, named `broadcast_<n>.fmsbcast`, MIME
`application/vnd.fms.bcast+json`. Consumers accept plain JSON and `FMSBCAST1:` text and read at most
64 KiB.

### 4.3 Link **[CONTRACT]** today

“Send link” shares an Android `intent://` URI carrying URL-encoded `FMSBCAST1:` descriptor text. Its target
is `fmsbcast://import?payload=<url-encoded FMSBCAST1 payload>`. The consumer may read that payload
directly. The browser fallback is a public import page. A bare section-2 stream URL also works in any
player, but carries neither title, `sourceId` nor mode.

A future clickable link form is **[PROPOSED]** until its scheme is fixed.

## 5. Latency and buffering **[CONTRACT]**

The producer measured first byte after the request at 6 ms (phone) and 11 ms (watch) on the same Wi-Fi;
bytes arrive at real-time rate from the first moment, with no retained backlog. The phone capture buffer is
about 0.09 s. On LAN, anything over half a second end-to-end is added by the consumer.

The consumer must:

1. Start at the live edge with a small buffer: aim for sound within 1 second of connecting and no more than
   2 seconds behind source in LAN steady state.
2. After a stall or reconnect, rejoin the live edge and drop missed content.
3. Keep resilience in bounded reconnect/backoff, not a deep buffer.
4. Raise buffer only for a future transport marked as needing it, then still correct drift towards target.
5. **Open one connection per listener.** A metadata probe or health check against the same URL is another
   listener slot on the watch.

### 5.3 Knowing a stream is live **[CONTRACT]**

Descriptor v2 can carry `isLive: true` and `targetLatencyMs`. Every stream arriving through a section-4
hand-off is live, as are the section-2 phone and watch URL shapes.

StreamsPlayer's observed ordinary WPF audio path took 4.4 seconds to open a phone stream and yielded about
6 seconds audible delay. A consumer must not compensate quiet producer audio with its own gain.

## 6. Verification handshake

When FastMediaSorter promotes a stream kind to `[CONTRACT]`, it names the section. The consumer then checks
all three hand-off forms, playback, live-edge delay and delay after forced reconnect against a live device.
