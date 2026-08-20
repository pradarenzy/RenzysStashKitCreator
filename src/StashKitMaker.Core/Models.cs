namespace StashKitMaker.Core;

public enum ParseStatus { Success, Partial, Unsupported, ParseError }
public enum ResolutionStatus { Resolved, Missing, Ambiguous }
public enum SampleCategory { Unknown, EightOhEight, Kick, Snare, Clap, Rim, Hat, OpenHat, Crash, Cymbal, Ride, Perc, Tom, Bass, FX, Impact, Riser, Downlifter, Texture, Vox, VocalChop, DrumLoop, PercLoop, MelodyLoop, OneShot }

public sealed record SampleReference(string StoredPath, string? ChannelName = null, string? PluginContext = null, int? ChannelId = null);
public sealed record MidiNoteEvent(int Position, int Length, int Key, int Velocity, int MidiChannel = 0);
public sealed record PatternMidiSequence(int PatternId, string PatternName, int ChannelId, string ChannelName, string? SamplePath, IReadOnlyList<MidiNoteEvent> Notes)
{
    public int DurationTicks => Notes.Count == 0 ? 0 : Notes.Max(note => (int)Math.Min(int.MaxValue, (long)note.Position + note.Length));
}
public sealed record ProjectAnalysis(string ProjectName, string ProjectPath, string? FlStudioVersion, ParseStatus Status, IReadOnlyList<SampleReference> SampleReferences, IReadOnlyList<string> Warnings)
{
    public int Ppq { get; init; } = 96;
    public double TempoBpm { get; init; } = 130;
    public IReadOnlyList<PatternMidiSequence> MidiPatterns { get; init; } = Array.Empty<PatternMidiSequence>();
}
public sealed record Resolution(ResolutionStatus Status, string? Path, IReadOnlyList<string> Candidates);
public sealed record Classification(SampleCategory Category, SampleCategory? Secondary, double Confidence, IReadOnlyList<string> Evidence, string? Variant = null, int? Bpm = null, string? Key = null);
public sealed class UniqueSample
{
    public string Hash { get; init; } = "";
    public string CanonicalPath { get; init; } = "";
    public List<string> SourcePaths { get; } = new();
    public List<string> Projects { get; } = new();
    public Classification Classification { get; set; } = new(SampleCategory.Unknown, null, 0, Array.Empty<string>());
    public string OutputName { get; set; } = "";
    public bool Included { get; set; } = true;
    public bool NameLocked { get; set; }
}

public sealed record PlannedFile(string Hash, string SourcePath, string RelativePath, IReadOnlyList<string> SourcePaths, IReadOnlyList<string> Projects, Classification Classification);
public sealed record BuildPlan(string KitName, string DestinationParent, IReadOnlyList<PlannedFile> Files, bool IncludePrivateProvenance, bool MergeIntoGeneratedKit = false)
{
    public string RootPath => Path.Combine(DestinationParent, KitName);
}
public sealed record BuildResult(string RootPath, int Copied, IReadOnlyList<string> Failures);
