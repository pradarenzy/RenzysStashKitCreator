using System.Text.Json;
using System.IO;
using StashKitMaker.Core;

namespace StashKitMaker.App;

public sealed class AppState
{
    public string KitParent { get; set; } = @"D:\1renzys Trap pack\--  new kits";
    public string KitName { get; set; } = "RenzysStashKit";
    public string UiAccentColor { get; set; } = "#A66F83";
    public bool MergeGeneratedKit { get; set; }
    public List<BuildHistoryEntry> History { get; set; } = new();
    public List<string> SampleLibraryRoots { get; set; } = new();
    public Dictionary<string, string> SamplePathOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<StackLayerState> ActiveStack { get; set; } = new();
    public string StackName { get; set; } = "My Stack";
    public List<ExtractedMidiState> ExtractedMidiPatterns { get; set; } = new();
    public Dictionary<string, ProcessedSound> ProcessedSounds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, KitEntryState> KitEntries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, KitGroupState> KitGroups { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
public sealed class StackLayerState
{
    public string StableId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string AudioPath { get; set; } = "";
    public string Category { get; set; } = "Unknown";
    public string KitName { get; set; } = "";
}
public sealed class ExtractedMidiState
{
    public string StableId { get; set; } = "";
    public string MidiPath { get; set; } = "";
    public string ProjectPath { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public int PatternId { get; set; }
    public string PatternName { get; set; } = "";
    public int ChannelId { get; set; }
    public string ChannelName { get; set; } = "";
    public string StoredSamplePath { get; set; } = "";
    public string AudioPath { get; set; } = "";
    public string Category { get; set; } = "Unknown";
    public int Ppq { get; set; } = 96;
    public double TempoBpm { get; set; } = 130;
    public List<MidiNoteEvent> Notes { get; set; } = new();
    public DateTime ExtractedUtc { get; set; } = DateTime.UtcNow;
}
public sealed class BuildHistoryEntry { public DateTime CreatedUtc { get; set; } public string KitPath { get; set; } = ""; public int Added { get; set; } public int SkippedDuplicates { get; set; } public override string ToString() => $"{CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm} — {System.IO.Path.GetFileName(KitPath)} (+{Added}, skipped {SkippedDuplicates})"; }
public sealed class ProcessedSound { public string Hash { get; set; } = ""; public string OutputName { get; set; } = ""; public string Category { get; set; } = "Unknown"; public string KitPath { get; set; } = ""; }
public sealed class KitEntryState
{
    public string StableId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string UnicodeIcon { get; set; } = "";
    public string TextColor { get; set; } = "#454142";
    public string AudioSource { get; set; } = "";
    public string ParentId { get; set; } = "";
}
public sealed class KitGroupState
{
    public string StableId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string UnicodeIcon { get; set; } = "";
    public string TextColor { get; set; } = "#C04375";
}
public sealed class KitFolderState
{
    public string StableId { get; set; } = "";
    public string? ParentId { get; set; }
    public string DisplayName { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public List<string> Categories { get; set; } = new();
    public int FlIconIndex { get; set; } = 1014;
    public int FlHeightOffset { get; set; } = 5;
    public int FlSortGroup { get; set; } = 8;
    public string FlTip { get; set; } = "";
}
public sealed class KitStateFile
{
    public int Version { get; set; } = 3;
    public string SoundUnicodeIcon { get; set; } = "";
    public string SoundTextColor { get; set; } = "#454142";
    public string BaseColor { get; set; } = "#93977F";
    public string AppliedSignature { get; set; } = "";
    public List<KitFolderState> Folders { get; set; } = new();
    public List<KitEntryState> Entries { get; set; } = new();
    public List<KitGroupState> Groups { get; set; } = new();
}
public static class AppStateStore
{
    private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RenzysStashKitMaker");
    private static readonly string FilePath = Path.Combine(Folder, "state.json");
    private static readonly string LegacyFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OkashiStashKitMaker", "state.json");
    public static string MidiExtractionFolder => Path.Combine(Folder, "Extracted MIDI");

    public static AppState Load()
    {
        try
        {
            var source = File.Exists(FilePath) ? FilePath : LegacyFilePath;
            return File.Exists(source) ? Deserialize(File.ReadAllText(source)) : new AppState();
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return new AppState();
        }
    }

    public static AppState Deserialize(string json)
    {
        var state = JsonSerializer.Deserialize<AppState>(json) ?? new AppState();
        state.History ??= new List<BuildHistoryEntry>();
        state.SampleLibraryRoots = (state.SampleLibraryRoots ?? new List<string>()).Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        state.SamplePathOverrides = new Dictionary<string, string>(state.SamplePathOverrides ?? new(), StringComparer.OrdinalIgnoreCase);
        state.ActiveStack ??= new List<StackLayerState>();
        if (string.IsNullOrWhiteSpace(state.StackName)) state.StackName = "My Stack";
        state.ExtractedMidiPatterns = (state.ExtractedMidiPatterns ?? new List<ExtractedMidiState>())
            .Where(item => item is not null && !string.IsNullOrWhiteSpace(item.StableId) && !string.IsNullOrWhiteSpace(item.MidiPath))
            .GroupBy(item => item.StableId, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList();
        foreach (var item in state.ExtractedMidiPatterns) item.Notes ??= new List<MidiNoteEvent>();
        state.ProcessedSounds = new Dictionary<string, ProcessedSound>(state.ProcessedSounds ?? new(), StringComparer.OrdinalIgnoreCase);
        state.KitEntries = new Dictionary<string, KitEntryState>(state.KitEntries ?? new(), StringComparer.OrdinalIgnoreCase);
        state.KitGroups = new Dictionary<string, KitGroupState>(state.KitGroups ?? new(), StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(state.UiAccentColor)) state.UiAccentColor = "#A66F83";
        return state;
    }

    public static void Save(AppState state)
    {
        Directory.CreateDirectory(Folder);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, FilePath, true);
    }
}
