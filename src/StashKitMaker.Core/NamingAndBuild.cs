using System.Text.Json;
using System.Text.RegularExpressions;

namespace StashKitMaker.Core;

public sealed class SweetNameGenerator
{
    public const string DictionaryVersion = "kawaii-v3";
    private static readonly string[] AnimalModifiers = { "", "Chibi ", "Mini ", "Poko ", "Fuwa ", "Mofu ", "Kira ", "Pika " };
    private static readonly string[] Animals = { "Neko", "Inu", "Usagi", "Kuma", "Panda", "Tori", "Sakana", "Kaeru", "Hitsuji", "Kitsune" };
    private static readonly string[] NatureNames = { "Hana", "Yuki", "Ame", "Tsuki", "Hoshi", "Kumo", "Niji", "Mizu", "Hi", "Kaze", "Sakura", "Ume", "Sakura Kaze", "Yuki Hoshi", "Tsuki Kumo", "Ame Niji", "Mizu Hana", "Hoshi Kaze", "Kumo Ame", "Sakura Tsuki" };
    private static readonly string[] FoodNames = { "Momo", "Ichigo", "Ringo", "Mikan", "Budō", "Anzu", "Nashi", "Kaki", "Ramune", "Purin", "Dorayaki", "Kasutera", "Konpeito", "Dango", "Choko", "Yuzu", "Anmitsu", "Taiyaki", "Azuki", "Mizuame", "Kohii", "Ichigo Daifuku", "Kinako Mochi", "Yuzu Ramune", "Macha Purin", "Mikan Soda", "Melon Soda", "Choko Minto", "Azuki Mochi", "Sakura Mochi" };
    private static readonly string[] ColorNames = { "Shiro", "Kuro", "Aka", "Ao", "Ki", "Midori", "Pinku", "Murasaki", "Cha", "Gin", "Shiro Neko", "Kuro Neko", "Pinku Usagi", "Shiro Kuma", "Aka Kitsune", "Ao Tori" };
    private static readonly string[] SoundNames = { "Doki Doki", "Waku Waku", "Kira Kira", "Pika Pika", "Fuwa Fuwa", "Mofu Mofu" };
    public string Generate(string hash, Classification c, int salt = 0)
    {
        ulong seed = 14695981039346656037UL; foreach (var character in hash) { seed ^= character; seed *= 1099511628211UL; }
        seed += (ulong)salt * 7919; var family = (int)(seed % 5); string themed = family switch { 0 => AnimalModifiers[(seed / 5) % (ulong)AnimalModifiers.Length] + Animals[(seed / ((ulong)AnimalModifiers.Length * 5)) % (ulong)Animals.Length], 1 => NatureNames[(seed / 5) % (ulong)NatureNames.Length], 2 => FoodNames[(seed / 5) % (ulong)FoodNames.Length], 3 => ColorNames[(seed / 5) % (ulong)ColorNames.Length], _ => SoundNames[(seed / 5) % (ulong)SoundNames.Length] };
        var parts = new List<string> { themed }; if (c.Variant is not null) parts.Add(c.Variant); parts.Add(Label(c.Category)); if (c.Bpm is not null && IsLoop(c.Category)) parts.Add($"{c.Bpm} BPM"); if (c.Key is not null && IsTonal(c.Category)) parts.Add(c.Key);
        return WindowsNames.Sanitize(string.Join(" ", parts)) + ".wav";
    }
    public static string Label(SampleCategory c) => c switch { SampleCategory.EightOhEight => "808", SampleCategory.Hat => "HH", SampleCategory.OpenHat => "Open Hat", SampleCategory.DrumLoop => "Drum Loop", SampleCategory.PercLoop => "Perc Loop", SampleCategory.MelodyLoop => "Melody Loop", SampleCategory.OneShot => "One Shot", SampleCategory.VocalChop => "Vocal Chop", _ => c.ToString() };
    private static bool IsLoop(SampleCategory c) => c is SampleCategory.DrumLoop or SampleCategory.PercLoop or SampleCategory.MelodyLoop;
    private static bool IsTonal(SampleCategory c) => c is SampleCategory.EightOhEight or SampleCategory.Bass or SampleCategory.MelodyLoop or SampleCategory.OneShot or SampleCategory.VocalChop;
}

public static class WindowsNames
{
    private static readonly Regex Invalid = new("[<>:\"/\\|?*\\x00-\\x1F]", RegexOptions.Compiled);
    private static readonly HashSet<string> Reserved = new(new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }, StringComparer.OrdinalIgnoreCase);
    public static string Sanitize(string name) { var s = Invalid.Replace(name, " "); s = Regex.Replace(s, @"\s+", " ").Trim().TrimEnd('.'); if (Reserved.Contains(s)) s += " Sample"; return string.IsNullOrWhiteSpace(s) ? "Unnamed Sample" : s; }
}

public static class CategoryFolders
{
    public static string For(SampleCategory c) => c switch { SampleCategory.EightOhEight => "808s", SampleCategory.Kick => "Kicks", SampleCategory.Snare => "Snares", SampleCategory.Clap => "Claps", SampleCategory.Rim => "Rims", SampleCategory.Hat => "Hats", SampleCategory.OpenHat => "Open Hats", SampleCategory.Perc => "Percs", SampleCategory.Bass => "Bass", SampleCategory.FX or SampleCategory.Impact or SampleCategory.Riser or SampleCategory.Downlifter or SampleCategory.Texture => "FX", SampleCategory.Vox or SampleCategory.VocalChop => "Vox", SampleCategory.DrumLoop => Path.Combine("Loops", "Drum Loops"), SampleCategory.PercLoop => Path.Combine("Loops", "Perc Loops"), SampleCategory.MelodyLoop => Path.Combine("Loops", "Melody Loops"), SampleCategory.OneShot => "One Shots", SampleCategory.Unknown => "Unknown", _ => SweetNameGenerator.Label(c) + "s" };
}

public sealed class KitBuilder
{
    public async Task<BuildResult> BuildAsync(BuildPlan plan, bool explicitApproval, CancellationToken token = default)
    {
        if (!explicitApproval) throw new InvalidOperationException("Explicit build approval is required.");
        if (Directory.Exists(plan.RootPath) && Directory.EnumerateFileSystemEntries(plan.RootPath).Any())
        {
            var marker = Path.Combine(plan.RootPath, "_metadata", ".stashkitmaker");
            if (!plan.MergeIntoGeneratedKit || !File.Exists(marker)) throw new IOException("Destination is not an approved generated kit; silent merge is forbidden.");
        }
        Validate(plan); Directory.CreateDirectory(plan.RootPath); var failures = new List<string>(); var copied = 0; var successfulFiles = new List<PlannedFile>();
        foreach (var file in plan.Files)
        {
            token.ThrowIfCancellationRequested(); var destination = Path.Combine(plan.RootPath, file.RelativePath);
            try { Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file.SourcePath, destination, false); if (Hashing.Sha256(destination) != file.Hash) { File.Delete(destination); throw new IOException("Post-copy SHA-256 verification failed."); } copied++; successfulFiles.Add(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failures.Add($"{file.SourcePath}: {ex.Message}"); }
        }
        var meta = Path.Combine(plan.RootPath, "_metadata"); Directory.CreateDirectory(meta);
        var manifestPath = Path.Combine(meta, "manifest.json");
        var manifest = successfulFiles.Select(f => new Dictionary<string, object?> { ["RelativePath"] = f.RelativePath, ["Hash"] = f.Hash, ["Category"] = f.Classification.Category.ToString(), ["Confidence"] = f.Classification.Confidence, ["Sources"] = plan.IncludePrivateProvenance ? f.SourcePaths : null, ["Projects"] = plan.IncludePrivateProvenance ? f.Projects : null }).ToList();
        if (plan.MergeIntoGeneratedKit && File.Exists(manifestPath)) { try { using var old = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, token)); var prior = old.RootElement.EnumerateArray().Select(x => JsonSerializer.Deserialize<Dictionary<string, object?>>(x.GetRawText())!).ToList(); prior.AddRange(manifest); manifest = prior; } catch (JsonException) { throw new InvalidDataException("Existing generated-kit manifest is unreadable; merge stopped safely."); } }
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), token);
        await File.WriteAllTextAsync(Path.Combine(meta, ".stashkitmaker"), "generated-by=StashKitMaker\nmanifest-version=1\n", token);
        return new(plan.RootPath, copied, failures);
    }
    public static void Validate(BuildPlan plan)
    {
        if (string.IsNullOrWhiteSpace(plan.KitName) || plan.KitName != WindowsNames.Sanitize(plan.KitName)) throw new InvalidDataException("Kit name is not Windows-safe.");
        var collisions = plan.Files.GroupBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
        if (collisions.Length > 0) throw new InvalidDataException("Case-insensitive output collisions: " + string.Join(", ", collisions));
    }
}

public sealed class DrumkitStructureAnalyzer
{
    public IReadOnlyDictionary<string, int> ScanReadOnly(string root) => Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Select(x => Path.GetFileName(x) ?? "Unknown").GroupBy(x => x, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Take(30).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
}
