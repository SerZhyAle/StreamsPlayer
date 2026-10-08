using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace StreamsPlayer.Core;

/// <summary>
/// SP-0173: what one <see cref="XmltvParser"/> read observed about itself. A diagnostic and test hook; the
/// schedule itself carries none of it.
/// </summary>
public sealed class XmltvParseReport
{
    /// <summary>Programmes dropped because their start or stop was present but could not be represented.</summary>
    public int SkippedProgrammes { get; internal set; }

    /// <summary>The most programmes held at once while reading; never above the programme cap.</summary>
    public int PeakProgrammesHeld { get; internal set; }
}

/// <summary>
/// SP-0075: reads an XMLTV document into the bounded schedule the product keeps.
///
/// <para>Streaming, because a source is untrusted and may be large: the reader never builds a DOM, never
/// resolves a DTD or an external entity, and stops at <see cref="TvScheduleLimits.MaximumDecompressedBytes"/>.
/// Only programmes that end after <c>now</c> and start before <c>now + WindowAhead</c> are kept.</para>
///
/// <para>A malformed programme (no channel, no title, a start or stop that cannot be represented) is skipped
/// rather than failing the document: partial coverage is the normal state of a schedule. A document that is
/// not XML at all, or not XMLTV, throws <see cref="InvalidDataException"/>.</para>
///
/// <para>SP-0173: the programme cap is enforced while reading, so memory follows the cap and not the file,
/// and the read observes its cancellation token once per node.</para>
/// </summary>
public static class XmltvParser
{
    // How far outside the window a programme is still read, so the programme that closes an open-ended one
    // at the window's edge is available when the open end is resolved.
    private static readonly TimeSpan ClosingMargin = TimeSpan.FromHours(12);

    // DateTimeOffset refuses any offset beyond 14 hours in total, so "+1430" is unreadable, not merely odd.
    private const int MaximumOffsetMinutes = 14 * 60;

    private static readonly string[] TimeFormats =
    [
        "yyyyMMddHHmmss", "yyyyMMddHHmm", "yyyyMMddHH", "yyyyMMdd"
    ];

    /// <summary>Parses a downloaded body, decompressing it first when it carries the gzip signature.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static IReadOnlyList<TvScheduleChannel> Parse(
        byte[] body,
        DateTimeOffset now,
        CancellationToken cancellationToken = default,
        XmltvParseReport? report = null)
    {
        using var raw = new MemoryStream(body, writable: false);
        if (body.Length >= 2 && body[0] == 0x1F && body[1] == 0x8B)
        {
            using var gzip = new GZipStream(raw, CompressionMode.Decompress);
            return Parse(gzip, now, cancellationToken, report);
        }

        return Parse(raw, now, cancellationToken, report);
    }

    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static IReadOnlyList<TvScheduleChannel> Parse(
        Stream source,
        DateTimeOffset now,
        CancellationToken cancellationToken = default,
        XmltvParseReport? report = null)
    {
        report ??= new XmltvParseReport();
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            IgnoreProcessingInstructions = true,
            CloseInput = false
        };

        var names = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var order = new List<string>();
        var programmes = new ProgrammeBuffer(TvScheduleLimits.MaximumProgrammes, report);
        var windowEnd = now + TvScheduleLimits.WindowAhead;
        var sawRoot = false;

        try
        {
            using var bounded = new BoundedReadStream(source, TvScheduleLimits.MaximumDecompressedBytes);
            using var reader = XmlReader.Create(bounded, settings);
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                switch (reader.LocalName)
                {
                    case "tv" when reader.Depth == 0:
                        sawRoot = true;
                        break;
                    case "channel" when reader.Depth == 1:
                        ReadChannel(reader, names, order);
                        break;
                    case "programme" when reader.Depth == 1:
                        ReadProgramme(reader, programmes, report, now, windowEnd);
                        break;
                }
            }
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException("The schedule is not well-formed XML.", exception);
        }

        if (!sawRoot)
        {
            throw new InvalidDataException("The document is not an XMLTV schedule.");
        }

        return Assemble(names, order, programmes.Drain(), now, windowEnd);
    }

    private static void ReadChannel(XmlReader reader, Dictionary<string, List<string>> names, List<string> order)
    {
        var id = Clip(reader.GetAttribute("id"));
        if (reader.IsEmptyElement || id is null)
        {
            return;
        }

        if (!names.TryGetValue(id, out var list))
        {
            if (names.Count >= TvScheduleLimits.MaximumChannels)
            {
                // Its children sit deeper than any element the main loop reads, so returning skips them.
                return;
            }

            list = [];
            names[id] = list;
            order.Add(id);
        }

        ReadChildTexts(reader, "display-name", name =>
        {
            if (name is not null && list.Count < TvScheduleLimits.MaximumNamesPerChannel &&
                !list.Contains(name, StringComparer.Ordinal))
            {
                list.Add(name);
            }
        });
    }

    private static void ReadProgramme(
        XmlReader reader,
        ProgrammeBuffer programmes,
        XmltvParseReport report,
        DateTimeOffset now,
        DateTimeOffset windowEnd)
    {
        var channel = Clip(reader.GetAttribute("channel"));
        var stopText = reader.GetAttribute("stop");
        var start = ParseTime(reader.GetAttribute("start"));
        var stop = ParseTime(stopText);

        // A stop that is present but cannot be represented is not "open-ended": guessing its end from the
        // next programme could put the wrong title on air, so the programme goes with it. Returning early
        // leaves the children to the main loop, which ignores anything below depth 1.
        if (start is null || (stop is null && !string.IsNullOrWhiteSpace(stopText)))
        {
            report.SkippedProgrammes++;
            return;
        }

        // Dropped while reading, so a week-long guide never holds a week of programmes in memory. The margin
        // on both sides keeps the successor that closes an open-ended programme: dropping a recently ended
        // one would let a later programme close it instead and put the wrong title on air.
        if ((stop is { } ended && ended <= now - ClosingMargin) ||
            start.Value >= windowEnd + ClosingMargin || (stop is null && start.Value < now - ClosingMargin))
        {
            return;
        }

        string? title = null;
        ReadChildTexts(reader, "title", text => title ??= text);

        if (channel is null || start is null || title is null || (stop is { } end && end <= start))
        {
            return;
        }

        // An open-ended programme is closed later by the next start on its channel.
        programmes.Add(channel, new TvProgramme(start.Value, stop ?? start.Value, title), stop is null);
    }

    private static IReadOnlyList<TvScheduleChannel> Assemble(
        Dictionary<string, List<string>> names,
        List<string> order,
        List<(string Channel, TvProgramme Programme, bool OpenEnded)> programmes,
        DateTimeOffset now,
        DateTimeOffset windowEnd)
    {
        var byChannel = new Dictionary<string, List<TvProgramme>>(StringComparer.Ordinal);
        foreach (var group in programmes.GroupBy(item => item.Channel, StringComparer.Ordinal))
        {
            var sorted = group.OrderBy(item => item.Programme.Start).ToList();
            var kept = new List<TvProgramme>();
            for (var index = 0; index < sorted.Count; index++)
            {
                var (_, programme, openEnded) = sorted[index];
                if (openEnded)
                {
                    // No stop and no successor: its end is unknown, so it cannot honestly be called current.
                    if (index + 1 >= sorted.Count || sorted[index + 1].Programme.Start <= programme.Start)
                    {
                        continue;
                    }

                    programme = programme with { Stop = sorted[index + 1].Programme.Start };
                }

                if (programme.Stop > now && programme.Start < windowEnd)
                {
                    kept.Add(programme);
                }
            }

            if (kept.Count > 0)
            {
                byChannel[group.Key] = kept;
            }
        }

        var result = new List<TvScheduleChannel>();
        foreach (var id in order.Concat(byChannel.Keys.Where(key => !names.ContainsKey(key))))
        {
            if (result.Count >= TvScheduleLimits.MaximumChannels)
            {
                break;
            }

            var channelNames = names.TryGetValue(id, out var list) ? list : [];
            var channelProgrammes = byChannel.TryGetValue(id, out var kept) ? kept : [];
            if (channelNames.Count == 0 && channelProgrammes.Count == 0)
            {
                continue;
            }

            result.Add(new TvScheduleChannel(id, channelNames, channelProgrammes));
        }

        return result;
    }

    /// <summary>
    /// XMLTV time: <c>YYYYMMDDhhmmss</c> or a leading part of it, optionally followed by an offset such as
    /// <c>+0200</c>. The format prescribes UTC when the offset is absent.
    /// </summary>
    public static DateTimeOffset? ParseTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (!DateTime.TryParseExact(parts[0], TimeFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var local))
        {
            return null;
        }

        var offset = TimeSpan.Zero;
        if (parts.Length == 2)
        {
            var zone = parts[1].Trim();
            if (zone.Length == 5 && (zone[0] == '+' || zone[0] == '-') &&
                int.TryParse(zone.AsSpan(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var hours) &&
                int.TryParse(zone.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) &&
                minutes < 60 && hours * 60 + minutes <= MaximumOffsetMinutes)
            {
                offset = new TimeSpan(hours, minutes, 0);
                if (zone[0] == '-')
                {
                    offset = -offset;
                }
            }
            else if (!zone.Equals("UTC", StringComparison.OrdinalIgnoreCase) &&
                     !zone.Equals("GMT", StringComparison.OrdinalIgnoreCase) &&
                     !zone.Equals("Z", StringComparison.OrdinalIgnoreCase))
            {
                // An offset we cannot read would put the programme at the wrong hour, which is worse than
                // not showing it (strategic risk: a one-hour error makes the feature harmful).
                return null;
            }
        }

        // DateTimeOffset throws when the UTC instant falls outside DateTime's range, which a year-1 or
        // year-9999 date with an offset reaches; one such programme must cost only itself (SP-0173).
        var utcTicks = local.Ticks - offset.Ticks;
        if (utcTicks < DateTime.MinValue.Ticks || utcTicks > DateTime.MaxValue.Ticks)
        {
            return null;
        }

        return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), offset);
    }

    /// <summary>
    /// Hands the text of every direct child named <paramref name="elementName"/> to <paramref name="onText"/>
    /// and leaves the reader on the parent's end tag. Text is gathered node by node rather than with
    /// ReadElementContentAsString, which throws on markup inside a title and would fail the whole document.
    /// </summary>
    private static void ReadChildTexts(XmlReader reader, string elementName, Action<string?> onText)
    {
        if (reader.IsEmptyElement)
        {
            return;
        }

        var depth = reader.Depth;
        reader.Read();
        while (!reader.EOF && reader.Depth > depth)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Depth == depth + 1 && reader.LocalName == elementName)
            {
                onText(ReadText(reader));
                continue;
            }

            reader.Read();
        }
    }

    private static string? ReadText(XmlReader reader)
    {
        if (reader.IsEmptyElement)
        {
            reader.Read();
            return null;
        }

        var depth = reader.Depth;
        var text = new StringBuilder();
        reader.Read();
        while (!reader.EOF && reader.Depth > depth)
        {
            if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace &&
                text.Length < TvScheduleLimits.MaximumTextLength * 4)
            {
                text.Append(reader.Value);
            }

            reader.Read();
        }

        reader.Read();
        return Clip(text.ToString());
    }

    private static string? Clip(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        // SP-0184 (S16-4): Truncate never splits a text element, so a first element longer than the limit
        // leaves nothing. That is "no text", the same as a blank one, and not an empty id or title.
        var clipped = TextBoundary.Truncate(text, TvScheduleLimits.MaximumTextLength).TrimEnd();
        return clipped.Length == 0 ? null : clipped;
    }

    /// <summary>
    /// SP-0173: the programmes kept while reading, never more than the cap. When full, the latest start is
    /// the one to go, so what remains is exactly the earliest programmes - the ones "now" and "next" are
    /// made of - and any programme that could close an open-ended one is either held or later than every
    /// held one.
    /// </summary>
    private sealed class ProgrammeBuffer(int capacity, XmltvParseReport report)
    {
        // A min-heap over the negated key, so its root is the latest start; the file-order sequence breaks
        // ties so that, as before, the programme read first wins.
        private readonly PriorityQueue<(string Channel, TvProgramme Programme, bool OpenEnded), (long, long)> _held = new();
        private long _sequence;

        public void Add(string channel, TvProgramme programme, bool openEnded)
        {
            var item = (channel, programme, openEnded);
            var priority = (-programme.Start.UtcTicks, -_sequence++);
            if (_held.Count < capacity)
            {
                _held.Enqueue(item, priority);
            }
            else if (_held.TryPeek(out _, out var latest) && priority.CompareTo(latest) > 0)
            {
                _held.EnqueueDequeue(item, priority);
            }

            report.PeakProgrammesHeld = Math.Max(report.PeakProgrammesHeld, _held.Count);
        }

        /// <summary>Everything held, earliest start first (file order among equal starts).</summary>
        public List<(string Channel, TvProgramme Programme, bool OpenEnded)> Drain()
        {
            var items = new List<(string Channel, TvProgramme Programme, bool OpenEnded)>(_held.Count);
            while (_held.TryDequeue(out var item, out _))
            {
                items.Add(item);
            }

            items.Reverse();
            return items;
        }
    }

    /// <summary>Fails the read once more than the allowed number of bytes has come through.</summary>
    private sealed class BoundedReadStream(Stream inner, long limit) : Stream
    {
        private long _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            _read += read;
            if (_read > limit)
            {
                throw new InvalidDataException(
                    $"The schedule expands beyond {limit} bytes and was not read further.");
            }

            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
