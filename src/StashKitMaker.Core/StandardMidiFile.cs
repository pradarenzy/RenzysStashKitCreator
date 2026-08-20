using System.Security.Cryptography;
using System.Text;

namespace StashKitMaker.Core;

/// <summary>Creates a format-0 Standard MIDI file without mutating its FLP source.</summary>
public static class StandardMidiFile
{
    public static byte[] Create(PatternMidiSequence sequence, int ppq, double tempoBpm)
    {
        ppq = Math.Clamp(ppq, 1, short.MaxValue);
        tempoBpm = double.IsFinite(tempoBpm) ? Math.Clamp(tempoBpm, 1, 999) : 130;

        using var track = new MemoryStream();
        WriteTrackName(track, $"{sequence.PatternName} - {sequence.ChannelName}");
        WriteTempo(track, tempoBpm);

        var events = new List<MidiMessage>(sequence.Notes.Count * 2);
        foreach (var note in sequence.Notes)
        {
            var start = Math.Max(0, note.Position);
            var end = (int)Math.Min(int.MaxValue, (long)start + Math.Max(1, note.Length));
            var channel = Math.Clamp(note.MidiChannel, 0, 15);
            var key = Math.Clamp(note.Key, 0, 127);
            var velocity = Math.Clamp(note.Velocity, 1, 127);
            events.Add(new MidiMessage(start, 1, (byte)(0x90 | channel), (byte)key, (byte)velocity));
            events.Add(new MidiMessage(end, 0, (byte)(0x80 | channel), (byte)key, 0));
        }

        var previousTick = 0;
        foreach (var message in events.OrderBy(message => message.Tick).ThenBy(message => message.Order))
        {
            WriteVariableLength(track, Math.Max(0, message.Tick - previousTick));
            track.WriteByte(message.Status);
            track.WriteByte(message.Key);
            track.WriteByte(message.Velocity);
            previousTick = message.Tick;
        }
        WriteVariableLength(track, 0);
        track.Write(new byte[] { 0xFF, 0x2F, 0x00 });

        using var file = new MemoryStream();
        file.Write(Encoding.ASCII.GetBytes("MThd"));
        WriteBigEndian(file, 6, 4);
        WriteBigEndian(file, 0, 2);
        WriteBigEndian(file, 1, 2);
        WriteBigEndian(file, ppq, 2);
        file.Write(Encoding.ASCII.GetBytes("MTrk"));
        WriteBigEndian(file, checked((int)track.Length), 4);
        track.Position = 0;
        track.CopyTo(file);
        return file.ToArray();
    }

    public static string Fingerprint(byte[] midiBytes, string? soundIdentity = null)
    {
        using var stream = new MemoryStream();
        stream.Write(midiBytes);
        if (!string.IsNullOrWhiteSpace(soundIdentity)) stream.Write(Encoding.UTF8.GetBytes("\n" + soundIdentity.Trim().ToUpperInvariant()));
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static void WriteTrackName(Stream stream, string name)
    {
        var bytes = Encoding.UTF8.GetBytes(name);
        WriteVariableLength(stream, 0);
        stream.WriteByte(0xFF);
        stream.WriteByte(0x03);
        WriteVariableLength(stream, bytes.Length);
        stream.Write(bytes);
    }

    private static void WriteTempo(Stream stream, double tempoBpm)
    {
        var microseconds = Math.Clamp((int)Math.Round(60_000_000d / tempoBpm), 1, 0xFFFFFF);
        WriteVariableLength(stream, 0);
        stream.Write(new byte[] { 0xFF, 0x51, 0x03, (byte)(microseconds >> 16), (byte)(microseconds >> 8), (byte)microseconds });
    }

    private static void WriteVariableLength(Stream stream, int value)
    {
        value = Math.Clamp(value, 0, 0x0FFFFFFF);
        Span<byte> buffer = stackalloc byte[4];
        var index = 3;
        buffer[index] = (byte)(value & 0x7F);
        while ((value >>= 7) > 0) buffer[--index] = (byte)((value & 0x7F) | 0x80);
        stream.Write(buffer[index..]);
    }

    private static void WriteBigEndian(Stream stream, int value, int byteCount)
    {
        for (var shift = (byteCount - 1) * 8; shift >= 0; shift -= 8) stream.WriteByte((byte)(value >> shift));
    }

    private readonly record struct MidiMessage(int Tick, int Order, byte Status, byte Key, byte Velocity);
}
