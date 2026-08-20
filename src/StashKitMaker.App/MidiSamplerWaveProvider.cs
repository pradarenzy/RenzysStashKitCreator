using NAudio.Wave;
using StashKitMaker.Core;
using System.IO;

namespace StashKitMaker.App;

/// <summary>
/// Streams an FL-style sampler preview: MIDI key changes playback rate/pitch and
/// every new note on the channel immediately chokes and restarts the prior hit.
/// </summary>
public sealed class MidiSamplerWaveProvider : IWaveProvider
{
    public const int DefaultRootMidiNote = 60; // FL Studio C5 / Standard MIDI middle C.

    private readonly float[] sourceSamples;
    private readonly int channels;
    private readonly int sourceFrames;
    private readonly ScheduledNote[] schedule;
    private int nextNoteIndex;
    private long outputFrame;
    private double sourceFrame;
    private double pitchRatio = 1;
    private float velocityGain;

    public MidiSamplerWaveProvider(float[] sourceSamples, int channels, int sampleRate, IReadOnlyList<MidiNoteEvent> notes, int ppq, double tempoBpm, int rootMidiNote = DefaultRootMidiNote)
    {
        if (sourceSamples is null || sourceSamples.Length == 0) throw new InvalidDataException("The matching sound contains no decodable audio samples.");
        if (channels is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(channels), "Sampler preview supports mono or stereo audio.");
        if (sourceSamples.Length % channels != 0) throw new InvalidDataException("The matching sound has an incomplete audio frame.");
        if (notes is null || notes.Count == 0) throw new InvalidDataException("The extracted MIDI sequence contains no notes.");

        this.sourceSamples = sourceSamples;
        this.channels = channels;
        sourceFrames = sourceSamples.Length / channels;
        WaveFormat = new WaveFormat(Math.Clamp(sampleRate, 8000, 192000), 16, channels);
        ppq = Math.Max(1, ppq);
        tempoBpm = double.IsFinite(tempoBpm) ? Math.Clamp(tempoBpm, 1, 999) : 130;
        rootMidiNote = Math.Clamp(rootMidiNote, 0, 127);

        var ordered = notes.OrderBy(note => note.Position).ThenBy(note => note.Key).Take(4096).ToArray();
        var firstTick = ordered[0].Position;
        var framesPerTick = WaveFormat.SampleRate * 60d / tempoBpm / ppq;
        schedule = ordered.Select(note => new ScheduledNote(
            Math.Max(0, (long)Math.Round((note.Position - firstTick) * framesPerTick)),
            Math.Pow(2d, (Math.Clamp(note.Key, 0, 127) - rootMidiNote) / 12d),
            Math.Clamp(note.Velocity, 1, 127) / 127f)).ToArray();

        var finalNote = schedule[^1];
        TotalFrames = finalNote.OutputFrame + Math.Max(1, (long)Math.Ceiling(sourceFrames / finalNote.PitchRatio));
        sourceFrame = sourceFrames;
    }

    public WaveFormat WaveFormat { get; }
    public long TotalFrames { get; }
    public TimeSpan Duration => TimeSpan.FromSeconds(TotalFrames / (double)WaveFormat.SampleRate);

    public static MidiSamplerWaveProvider FromFile(string audioPath, IReadOnlyList<MidiNoteEvent> notes, int ppq, double tempoBpm, int rootMidiNote = DefaultRootMidiNote)
    {
        using var reader = new AudioFileReader(audioPath);
        var sourceChannels = reader.WaveFormat.Channels;
        if (sourceChannels < 1) throw new InvalidDataException("The matching sound has no audio channels.");
        var samples = new List<float>();
        var buffer = new float[Math.Max(4096, reader.WaveFormat.SampleRate * sourceChannels)];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            samples.AddRange(buffer.AsSpan(0, read).ToArray());

        if (sourceChannels <= 2)
            return new MidiSamplerWaveProvider(samples.ToArray(), sourceChannels, reader.WaveFormat.SampleRate, notes, ppq, tempoBpm, rootMidiNote);

        var raw = samples.ToArray();
        var frames = raw.Length / sourceChannels;
        var stereo = new float[frames * 2];
        for (var frame = 0; frame < frames; frame++)
        {
            stereo[frame * 2] = raw[frame * sourceChannels];
            stereo[frame * 2 + 1] = raw[frame * sourceChannels + 1];
        }
        return new MidiSamplerWaveProvider(stereo, 2, reader.WaveFormat.SampleRate, notes, ppq, tempoBpm, rootMidiNote);
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        if (buffer is null) throw new ArgumentNullException(nameof(buffer));
        if (offset < 0 || count < 0 || offset + count > buffer.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        var blockAlign = WaveFormat.BlockAlign;
        var requestedFrames = count / blockAlign;
        var remainingFrames = Math.Max(0, TotalFrames - outputFrame);
        var framesToWrite = (int)Math.Min(requestedFrames, remainingFrames);
        var byteIndex = offset;

        for (var frame = 0; frame < framesToWrite; frame++, outputFrame++)
        {
            while (nextNoteIndex < schedule.Length && schedule[nextNoteIndex].OutputFrame <= outputFrame)
            {
                var note = schedule[nextNoteIndex++];
                sourceFrame = 0;
                pitchRatio = note.PitchRatio;
                velocityGain = note.VelocityGain;
            }

            var sourceIndex = (int)sourceFrame;
            var fraction = sourceFrame - sourceIndex;
            for (var channel = 0; channel < channels; channel++)
            {
                var sample = 0f;
                if (sourceIndex < sourceFrames)
                {
                    var current = sourceSamples[sourceIndex * channels + channel];
                    var next = sourceIndex + 1 < sourceFrames ? sourceSamples[(sourceIndex + 1) * channels + channel] : current;
                    sample = (float)(current + (next - current) * fraction) * velocityGain;
                }
                var pcm = (short)Math.Round(Math.Clamp(sample, -1f, 1f) * short.MaxValue);
                buffer[byteIndex++] = (byte)pcm;
                buffer[byteIndex++] = (byte)(pcm >> 8);
            }
            sourceFrame += pitchRatio;
        }

        return framesToWrite * blockAlign;
    }

    private readonly record struct ScheduledNote(long OutputFrame, double PitchRatio, float VelocityGain);
}
