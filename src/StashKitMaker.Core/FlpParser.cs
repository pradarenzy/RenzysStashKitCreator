using System.Text;

namespace StashKitMaker.Core;

public interface IFlpParser { ProjectAnalysis ParseProject(string path); }

/// <summary>
/// Read-only FLhd/FLdt event-stream parser. Besides sampler references it retains
/// the pattern/channel note records needed to produce independent Standard MIDI files.
/// Unknown events are skipped and the source FLP is never opened for writing.
/// </summary>
public sealed class EventStreamFlpParser : IFlpParser
{
    public const string ParserVersion = "event-stream-2-midi";
    private const byte NewChannelEvent = 0x40;
    private const byte NewPatternEvent = 0x41;
    private const byte LegacyTempoEvent = 0x42;
    private const byte LegacyTempoFineEvent = 0x5D;
    private const byte ModernTempoEvent = 0x9C;
    private const byte Fl25ThreeByteEvent = 0xAC;
    private const byte VersionBannerEvent = 0xC0;
    private const byte PatternNameEvent = 0xC1;
    private const byte SamplePathEvent = 0xC4;
    private const byte VersionEvent = 0xC7;
    private const byte ChannelNameEvent = 0xCB;
    private const byte LegacyPatternNotesEvent = 0xD0;
    private const byte Fl25PatternNotesEvent = 0xE0;
    private const byte MixerSlotBoundaryEvent = 0x62;

    public ProjectAnalysis ParseProject(string path)
    {
        var warnings = new List<string>();
        var channels = new Dictionary<int, ChannelInfo>();
        var looseReferences = new List<SampleReference>();
        var patternNames = new Dictionary<int, string>();
        var notesByPatternChannel = new Dictionary<(int PatternId, int ChannelId), List<MidiNoteEvent>>();
        string? version = null;
        int? currentChannelId = null;
        int? currentPatternId = null;
        var channelScope = false;
        var ppq = 96;
        double? modernTempo = null;
        ushort? legacyTempo = null;
        ushort legacyTempoFine = 0;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
            if (new string(reader.ReadChars(4)) != "FLhd") throw new InvalidDataException("FLhd signature not found.");
            var headerLength = reader.ReadUInt32();
            if (headerLength < 6 || headerLength > 1024) throw new InvalidDataException("Invalid FLP header length.");
            var headerStart = stream.Position;
            _ = reader.ReadUInt16();
            _ = reader.ReadUInt16();
            ppq = Math.Max(1, (int)reader.ReadUInt16());
            stream.Position = headerStart + headerLength;
            if (new string(reader.ReadChars(4)) != "FLdt") throw new InvalidDataException("FLdt event stream not found.");
            var dataLength = reader.ReadUInt32();
            var end = Math.Min(stream.Length, stream.Position + dataLength);

            while (stream.Position < end)
            {
                var id = reader.ReadByte();
                if (id < 0x40)
                {
                    _ = reader.ReadByte();
                    continue;
                }

                if (id < 0x80)
                {
                    var value = reader.ReadUInt16();
                    if (id == NewChannelEvent)
                    {
                        currentChannelId = value;
                        channelScope = true;
                        if (!channels.ContainsKey(value)) channels[value] = new ChannelInfo(value);
                    }
                    else if (id == NewPatternEvent) currentPatternId = value;
                    else if (id == LegacyTempoEvent) legacyTempo = value;
                    else if (id == LegacyTempoFineEvent) legacyTempoFine = value;
                    else if (id == MixerSlotBoundaryEvent) channelScope = false;
                    continue;
                }

                if (id < 0xC0)
                {
                    if (id == Fl25ThreeByteEvent && MajorVersion(version) >= 25)
                    {
                        if (stream.Position + 3 > end) throw new InvalidDataException("Invalid FL Studio 25 event payload.");
                        _ = reader.ReadBytes(3);
                        continue;
                    }
                    var value = reader.ReadUInt32();
                    if (id == ModernTempoEvent && value > 0) modernTempo = value / 1000d;
                    continue;
                }

                var length = ReadVariableLength(reader, end);
                if (length > int.MaxValue || stream.Position + (long)length > end) throw new InvalidDataException("Invalid FLP event length.");
                var bytes = reader.ReadBytes((int)length);

                if (id is VersionEvent or VersionBannerEvent)
                {
                    var candidate = DecodeText(bytes).TrimEnd('\0').Trim();
                    if (!string.IsNullOrWhiteSpace(candidate) && (id == VersionEvent || candidate.Contains("FL Studio", StringComparison.OrdinalIgnoreCase))) version = candidate;
                }
                else if (id == ChannelNameEvent && channelScope && currentChannelId is int channelNameId)
                {
                    channels[channelNameId].Name = DecodeText(bytes).TrimEnd('\0').Trim();
                }
                else if (id == SamplePathEvent)
                {
                    var samplePath = DecodeText(bytes).TrimEnd('\0').Trim();
                    if (!string.IsNullOrWhiteSpace(samplePath) && LooksLikeAudioPath(samplePath))
                    {
                        if (currentChannelId is int sampleChannelId)
                        {
                            var channel = channels.GetValueOrDefault(sampleChannelId) ?? (channels[sampleChannelId] = new ChannelInfo(sampleChannelId));
                            channel.SamplePath = samplePath;
                        }
                        else looseReferences.Add(new SampleReference(samplePath));
                    }
                }
                else if (id == PatternNameEvent && currentPatternId is int namedPattern)
                {
                    var name = DecodeText(bytes).TrimEnd('\0').Trim();
                    if (!string.IsNullOrWhiteSpace(name)) patternNames[namedPattern] = name;
                }
                else if (id is LegacyPatternNotesEvent or Fl25PatternNotesEvent)
                {
                    if (currentPatternId is not int patternId)
                    {
                        warnings.Add("A pattern-note block had no pattern identifier and was skipped.");
                        continue;
                    }
                    if (bytes.Length % 24 != 0)
                    {
                        warnings.Add($"Pattern {patternId} contains an unsupported note block ({bytes.Length} bytes).");
                        continue;
                    }
                    foreach (var (noteChannelId, note) in DecodeNotes(bytes))
                    {
                        var key = (patternId, noteChannelId);
                        if (!notesByPatternChannel.TryGetValue(key, out var notes)) notesByPatternChannel[key] = notes = new List<MidiNoteEvent>();
                        notes.Add(note);
                    }
                }
            }

            var references = channels.Values.Where(channel => !string.IsNullOrWhiteSpace(channel.SamplePath))
                .OrderBy(channel => channel.Id)
                .Select(channel => new SampleReference(channel.SamplePath!, channel.DisplayName, null, channel.Id))
                .Concat(looseReferences)
                .ToArray();

            var midiPatterns = notesByPatternChannel.OrderBy(pair => pair.Key.PatternId).ThenBy(pair => pair.Key.ChannelId)
                .Select(pair =>
                {
                    var channel = channels.GetValueOrDefault(pair.Key.ChannelId);
                    return new PatternMidiSequence(
                        pair.Key.PatternId,
                        patternNames.GetValueOrDefault(pair.Key.PatternId) ?? $"Pattern {pair.Key.PatternId}",
                        pair.Key.ChannelId,
                        channel?.DisplayName ?? $"Channel {pair.Key.ChannelId}",
                        channel?.SamplePath,
                        pair.Value.OrderBy(note => note.Position).ThenBy(note => note.Key).ToArray());
                }).ToArray();

            warnings.Add("Partial parser: sampler paths and pattern MIDI notes are supported; plugin-owned notes, automation, and unknown future FLP events are not interpreted.");
            return new ProjectAnalysis(Path.GetFileNameWithoutExtension(path), Path.GetFullPath(path), version, ParseStatus.Partial, references, warnings)
            {
                Ppq = ppq,
                TempoBpm = modernTempo ?? ((legacyTempo ?? 130) + legacyTempoFine / 1000d),
                MidiPatterns = midiPatterns
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException)
        {
            warnings.Add(ex.Message);
            return new ProjectAnalysis(Path.GetFileNameWithoutExtension(path), Path.GetFullPath(path), version, ParseStatus.ParseError, Array.Empty<SampleReference>(), warnings)
            {
                Ppq = ppq,
                TempoBpm = modernTempo ?? ((legacyTempo ?? 130) + legacyTempoFine / 1000d)
            };
        }
    }

    private static IEnumerable<(int ChannelId, MidiNoteEvent Note)> DecodeNotes(byte[] payload)
    {
        using var stream = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(stream);
        while (stream.Position + 24 <= stream.Length)
        {
            var position = (int)Math.Min(int.MaxValue, (long)reader.ReadUInt32());
            _ = reader.ReadUInt16();
            var channelId = reader.ReadUInt16();
            var length = (int)Math.Max(1, Math.Min(int.MaxValue, (long)reader.ReadUInt32()));
            var key = Math.Clamp((int)reader.ReadUInt16(), 0, 127);
            _ = reader.ReadUInt16();
            _ = reader.ReadByte();
            _ = reader.ReadByte();
            _ = reader.ReadByte();
            var midiChannel = reader.ReadByte() & 0x0F;
            _ = reader.ReadByte();
            var velocity = Math.Clamp((int)reader.ReadByte(), 1, 127);
            _ = reader.ReadByte();
            _ = reader.ReadByte();
            yield return (channelId, new MidiNoteEvent(position, length, key, velocity, midiChannel));
        }
    }

    private static uint ReadVariableLength(BinaryReader reader, long end)
    {
        uint value = 0;
        var shift = 0;
        while (reader.BaseStream.Position < end && shift <= 28)
        {
            var next = reader.ReadByte();
            value |= (uint)(next & 0x7F) << shift;
            if ((next & 0x80) == 0) return value;
            shift += 7;
        }
        throw new InvalidDataException("Invalid variable-length FLP event.");
    }

    private static int MajorVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return 0;
        var digits = new string(version.SkipWhile(character => !char.IsDigit(character)).TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var major) ? major : 0;
    }

    private static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 2 && (bytes[1] == 0 || (bytes.Length >= 4 && bytes[3] == 0))) return Encoding.Unicode.GetString(bytes);
        return Encoding.UTF8.GetString(bytes);
    }

    private static bool LooksLikeAudioPath(string value) => new[] { ".wav", ".mp3", ".flac", ".ogg", ".aif", ".aiff" }.Any(extension => value.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    private sealed class ChannelInfo
    {
        public ChannelInfo(int id) => Id = id;
        public int Id { get; }
        public string Name { get; set; } = "";
        public string? SamplePath { get; set; }
        public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"Channel {Id}" : Name;
    }
}
