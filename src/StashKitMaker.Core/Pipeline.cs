using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace StashKitMaker.Core;

public sealed class PathResolver
{
    private readonly string[] roots;
    public PathResolver(IEnumerable<string> roots) => this.roots = roots.Where(Directory.Exists).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    public Resolution Resolve(string stored, string projectPath)
    {
        var expanded = Environment.ExpandEnvironmentVariables(stored.Trim().Trim('"')).Replace('/', Path.DirectorySeparatorChar);
        var direct = new List<string>();
        if (Path.IsPathRooted(expanded)) direct.Add(expanded);
        direct.Add(Path.Combine(Path.GetDirectoryName(projectPath)!, expanded));
        foreach (var candidate in direct.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)) if (File.Exists(candidate)) return new(ResolutionStatus.Resolved, candidate, new[] { candidate });
        var name = Path.GetFileName(expanded); var matches = new List<string>();
        foreach (var root in roots) { try { matches.AddRange(Directory.EnumerateFiles(root, name, SearchOption.AllDirectories).Take(101)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
        matches = matches.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return matches.Count switch { 0 => new(ResolutionStatus.Missing, null, matches), 1 => new(ResolutionStatus.Resolved, matches[0], matches), _ => new(ResolutionStatus.Ambiguous, null, matches) };
    }
}

public static class Hashing
{
    public static string Sha256(string path) { using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); using var sha = SHA256.Create(); return Convert.ToHexString(sha.ComputeHash(s)).ToLowerInvariant(); }
}

public sealed class Deduplicator
{
    public IReadOnlyList<UniqueSample> Consolidate(IEnumerable<(string Path, string Project)> items)
    {
        var map = new Dictionary<string, UniqueSample>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var hash = Hashing.Sha256(item.Path);
            if (!map.TryGetValue(hash, out var sample)) map[hash] = sample = new() { Hash = hash, CanonicalPath = item.Path };
            if (!sample.SourcePaths.Contains(item.Path, StringComparer.OrdinalIgnoreCase)) sample.SourcePaths.Add(item.Path);
            if (!sample.Projects.Contains(item.Project, StringComparer.OrdinalIgnoreCase)) sample.Projects.Add(item.Project);
        }
        return map.Values.ToList();
    }
}

public sealed class SampleClassifier
{
    private static readonly (SampleCategory category, string[] tokens)[] Rules = {
        (SampleCategory.OpenHat, new[]{"open hat","openhat","ophat"}), (SampleCategory.EightOhEight,new[]{"808","sub bass"}),
        (SampleCategory.Kick,new[]{"kick","kck","bass drum","bd"}), (SampleCategory.Snare,new[]{"snare","snr","sd"}),
        (SampleCategory.Clap,new[]{"clap","clp"}), (SampleCategory.Rim,new[]{"rim","rimshot"}), (SampleCategory.Hat,new[]{"hihat","hi hat","closed hat","closedhat","hh"}),
        (SampleCategory.Crash,new[]{"crash"}), (SampleCategory.Ride,new[]{"ride"}), (SampleCategory.Cymbal,new[]{"cymbal"}), (SampleCategory.Perc,new[]{"perc","percussion"}),
        (SampleCategory.Tom,new[]{"tom"}), (SampleCategory.Vox,new[]{"vox","vocal"}), (SampleCategory.Riser,new[]{"riser"}), (SampleCategory.Downlifter,new[]{"downlifter"}),
        (SampleCategory.Impact,new[]{"impact"}), (SampleCategory.FX,new[]{"fx","effect"}), (SampleCategory.Bass,new[]{"bass"}), (SampleCategory.DrumLoop,new[]{"drum loop"}),
        (SampleCategory.PercLoop,new[]{"perc loop"}), (SampleCategory.MelodyLoop,new[]{"melody loop","melodic loop"}), (SampleCategory.OneShot,new[]{"one shot","oneshot"}) };
    public Classification Classify(string path, string? channel = null)
    {
        var filename = Normalize(Path.GetFileNameWithoutExtension(path)); var context = Normalize(channel ?? "");
        var scores = new Dictionary<SampleCategory, double>(); var evidence = new Dictionary<SampleCategory, List<string>>();
        foreach (var rule in Rules) foreach (var token in rule.tokens)
            {
                if (ContainsToken(filename, token)) Add(rule.category, .55, $"filename contains '{token}'");
                if (ContainsToken(context, token)) Add(rule.category, .75, $"FL channel contains '{token}'");
            }
        if (scores.Count == 0) return new(SampleCategory.Unknown, null, .15, new[] { "no reliable filename or channel evidence" });
        var ranked = scores.OrderByDescending(x => x.Value).ToList(); var best = ranked[0]; var confidence = Math.Min(.98, best.Value / (best.Value + ranked.Skip(1).Sum(x => x.Value) + .2));
        return new(best.Key, ranked.Count > 1 ? ranked[1].Key : null, confidence, evidence[best.Key], ExtractVariant(filename), ExtractBpm(filename), ExtractKey(filename));
        void Add(SampleCategory c, double s, string why) { scores[c] = scores.GetValueOrDefault(c) + s; if (!evidence.TryGetValue(c, out var e)) evidence[c] = e = new(); e.Add(why); }
    }
    private static string Normalize(string s) => Regex.Replace(s.ToLowerInvariant().Replace('_', ' ').Replace('-', ' '), @"\s+", " ").Trim();
    private static bool ContainsToken(string s, string token) => Regex.IsMatch(s, $@"(?<![a-z0-9]){Regex.Escape(token)}(?![a-z0-9])", RegexOptions.IgnoreCase);
    private static string? ExtractVariant(string s) => new[] { "hard", "soft", "short", "long", "clean", "dirty", "distorted", "reverse", "wide", "mono", "stereo", "layered", "dry", "wet" }.FirstOrDefault(x => ContainsToken(s, x)) is { } v ? char.ToUpperInvariant(v[0]) + v[1..] : null;
    private static int? ExtractBpm(string s) { var m = Regex.Match(s, @"(?<!\d)(?<bpm>\d{2,3})\s*(?:bpm)?(?!\d)"); return m.Success && int.TryParse(m.Groups["bpm"].Value, out var b) && b is >= 40 and <= 300 ? b : null; }
    private static string? ExtractKey(string s) { var m = Regex.Match(s, @"(?<![a-z])(?<n>[a-g])\s*(?<a>#|sharp|b|flat)?\s*(?<mode>minor|major|min|maj)?(?![a-z])", RegexOptions.IgnoreCase); if (!m.Success) return null; var a = m.Groups["a"].Value.ToLowerInvariant(); var mode = m.Groups["mode"].Value.ToLowerInvariant(); return m.Groups["n"].Value.ToUpperInvariant() + (a is "#" or "sharp" ? " Sharp" : a is "b" or "flat" ? " Flat" : "") + (mode.StartsWith("min") ? " Minor" : mode.StartsWith("maj") ? " Major" : ""); }
}

public static class SampleScreening
{
    private static readonly string[] LongFormTokens = { "loop", "melody", "melodic", "song", "instrumental", "full beat", "type beat", "demo", "master", "rough mix", "final mix" };
    private static readonly string[] TagTokens = { "producer tag", "prod tag", "voice tag", "beat tag", "watermark", "purchase your tracks" };
    public static bool ShouldExclude(string path, out string reason)
    {
        var name = Regex.Replace(Path.GetFileNameWithoutExtension(path).ToLowerInvariant().Replace('_', ' ').Replace('-', ' '), @"\s+", " ");
        var tag = TagTokens.FirstOrDefault(x => name.Contains(x, StringComparison.Ordinal)); if (tag is not null) { reason = $"producer/voice tag marker '{tag}'"; return true; }
        var marker = LongFormTokens.FirstOrDefault(x => name.Contains(x, StringComparison.Ordinal)); if (marker is not null) { reason = $"loop or long-form marker '{marker}'"; return true; }
        var duration = TryReadWavDuration(path); if (duration is > 12) { reason = $"long-form WAV ({duration.Value:0.0} seconds)"; return true; }
        reason = ""; return false;
    }
    public static double? TryReadWavDuration(string path)
    {
        if (!Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase)) return null;
        try { using var s = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); using var r = new BinaryReader(s); if (new string(r.ReadChars(4)) != "RIFF") return null; r.ReadUInt32(); if (new string(r.ReadChars(4)) != "WAVE") return null; uint byteRate = 0, dataSize = 0; while (s.Position + 8 <= s.Length) { var id = new string(r.ReadChars(4)); var size = r.ReadUInt32(); if (id == "fmt " && size >= 16) { r.ReadUInt16(); r.ReadUInt16(); r.ReadUInt32(); byteRate = r.ReadUInt32(); s.Position += size - 12; } else if (id == "data") { dataSize = size; break; } else s.Position += size + (size % 2); } return byteRate > 0 ? dataSize / (double)byteRate : null; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException) { return null; }
    }
}
