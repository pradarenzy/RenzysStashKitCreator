using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace StashKitMaker.Core;

public readonly record struct HslColor(double Hue, double Saturation, double Lightness);
public readonly record struct HierarchyColor(string WebHex, string FlStudioBgr, HslColor Hsl);
public sealed record HierarchyPalette(HierarchyColor Main, HierarchyColor Subfolder, HierarchyColor Deeper);
public sealed record FlStudioNfoSettings(string Color, int IconIndex = 1014, int HeightOffset = 5, int SortGroup = 8, string? Tip = null);

public static class FlStudioBrowserIcons
{
    public const int UnicodeBase = 0xF100;

    public static int CodePointForIndex(int iconIndex)
    {
        if (iconIndex < 0) throw new ArgumentOutOfRangeException(nameof(iconIndex), "IconIndex cannot be negative.");
        return checked(UnicodeBase + iconIndex);
    }
}

public static class FlStudioColors
{
    private static readonly Regex WebHexPattern = new("^#[0-9A-Fa-f]{6}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex FlBgrPattern = new("^\\$[0-9A-Fa-f]{6}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsWebHex(string? value) => value is not null && WebHexPattern.IsMatch(value);

    public static string NormalizeWebHex(string value)
    {
        if (!IsWebHex(value)) throw new FormatException("Enter exactly six hexadecimal digits in #RRGGBB format.");
        return value.ToUpperInvariant();
    }

    public static HslColor HexToHsl(string webHex)
    {
        var hex = NormalizeWebHex(webHex);
        var r = int.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d;
        var g = int.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d;
        var b = int.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        var lightness = (max + min) / 2d;
        if (delta == 0) return new(0, 0, lightness);
        var saturation = delta / (1d - Math.Abs(2d * lightness - 1d));
        var hue = max == r ? 60d * (((g - b) / delta) % 6d) : max == g ? 60d * (((b - r) / delta) + 2d) : 60d * (((r - g) / delta) + 4d);
        if (hue < 0) hue += 360d;
        return new(hue, Clamp01(saturation), Clamp01(lightness));
    }

    public static string HslToHex(HslColor color)
    {
        var hue = ((color.Hue % 360d) + 360d) % 360d;
        var saturation = Clamp01(color.Saturation);
        var lightness = Clamp01(color.Lightness);
        var chroma = (1d - Math.Abs(2d * lightness - 1d)) * saturation;
        var x = chroma * (1d - Math.Abs((hue / 60d) % 2d - 1d));
        var m = lightness - chroma / 2d;
        var (r1, g1, b1) = hue switch
        {
            < 60d => (chroma, x, 0d),
            < 120d => (x, chroma, 0d),
            < 180d => (0d, chroma, x),
            < 240d => (0d, x, chroma),
            < 300d => (x, 0d, chroma),
            _ => (chroma, 0d, x)
        };
        return $"#{ToByte(r1 + m):X2}{ToByte(g1 + m):X2}{ToByte(b1 + m):X2}";
    }

    public static HslColor DeriveHsl(HslColor baseColor, int depth)
    {
        var saturationFactor = depth <= 0 ? 1d : depth == 1 ? .6d : .3d;
        var lightnessFactor = depth <= 0 ? 0d : depth == 1 ? .15d : .3d;
        return new(
            ((baseColor.Hue % 360d) + 360d) % 360d,
            Clamp01(baseColor.Saturation * saturationFactor),
            Clamp01(baseColor.Lightness + (1d - baseColor.Lightness) * lightnessFactor));
    }

    public static HierarchyColor Derive(string baseWebHex, int depth)
    {
        var hsl = DeriveHsl(HexToHsl(baseWebHex), depth);
        var web = HslToHex(hsl);
        return new(web, WebToFlBgr(web), hsl);
    }

    public static HierarchyPalette CreatePalette(string baseWebHex) => new(Derive(baseWebHex, 0), Derive(baseWebHex, 1), Derive(baseWebHex, 2));

    public static string WebToFlBgr(string webHex)
    {
        var hex = NormalizeWebHex(webHex);
        return $"${hex[5..7]}{hex[3..5]}{hex[1..3]}";
    }

    public static string FlBgrToWeb(string flBgr)
    {
        if (!FlBgrPattern.IsMatch(flBgr)) throw new FormatException("FL Studio colours must use $BBGGRR format.");
        var value = flBgr.ToUpperInvariant();
        return $"#{value[5..7]}{value[3..5]}{value[1..3]}";
    }

    private static int ToByte(double channel) => (int)Math.Round(Clamp01(channel) * 255d, MidpointRounding.AwayFromZero);
    private static double Clamp01(double value) => Math.Max(0d, Math.Min(1d, value));
}

public static class FlStudioNfo
{
    private static readonly Regex IconIndexLine = new("^[ \\t]*IconIndex[ \\t]*=[ \\t]*(?<value>-?[0-9]+)[ \\t]*(?=\\r?$)", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    public static string SidecarPath(string folderPath) => folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".nfo";

    public static string Serialize(FlStudioNfoSettings settings)
    {
        var color = settings.Color.StartsWith('$') ? settings.Color.ToUpperInvariant() : FlStudioColors.WebToFlBgr(settings.Color);
        _ = FlStudioColors.FlBgrToWeb(color);
        var lines = new List<string>();
        var tip = SanitizeTip(settings.Tip);
        if (!string.IsNullOrWhiteSpace(tip)) lines.Add("Tip=" + tip);
        lines.Add("Color=" + color);
        lines.Add("IconIndex=" + settings.IconIndex.ToString(CultureInfo.InvariantCulture));
        lines.Add("HeightOfs=" + settings.HeightOffset.ToString(CultureInfo.InvariantCulture));
        lines.Add("SortGroup=" + settings.SortGroup.ToString(CultureInfo.InvariantCulture));
        return string.Join("\r\n", lines) + "\r\n";
    }

    public static string WriteBesideFolder(string folderPath, FlStudioNfoSettings settings)
    {
        if (!Directory.Exists(folderPath)) throw new DirectoryNotFoundException("Metadata target folder does not exist: " + folderPath);
        var path = SidecarPath(folderPath);
        var temp = path + ".tmp";
        File.WriteAllText(temp, Serialize(settings), new UTF8Encoding(false));
        File.Move(temp, path, true);
        return path;
    }

    public static bool TryReadIconIndex(string folderPath, out int iconIndex)
    {
        iconIndex = 0;
        var path = SidecarPath(folderPath);
        if (!File.Exists(path)) return false;
        var match = IconIndexLine.Match(File.ReadAllText(path));
        return match.Success && int.TryParse(match.Groups["value"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out iconIndex) && iconIndex >= 0;
    }

    public static string UpsertIconIndex(string existing, int iconIndex)
    {
        if (iconIndex < 0) throw new ArgumentOutOfRangeException(nameof(iconIndex), "IconIndex cannot be negative.");
        var replacement = "IconIndex=" + iconIndex.ToString(CultureInfo.InvariantCulture);
        if (IconIndexLine.IsMatch(existing)) return IconIndexLine.Replace(existing, replacement, 1);
        var normalized = existing.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd('\n');
        return (string.IsNullOrEmpty(normalized) ? replacement : normalized + "\n" + replacement).Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n";
    }

    public static string SanitizeTip(string? value) => Regex.Replace(value ?? "", "[\\r\\n]+", " ").Trim();
}

public static class KitFolderNames
{
    private static readonly Regex Invalid = new("[<>:\"/\\\\|?*\\x00-\\x1F]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> Reserved = new(new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }, StringComparer.OrdinalIgnoreCase);

    public static bool TryValidate(string? name, out string error)
    {
        if (string.IsNullOrWhiteSpace(name)) { error = "Folder names cannot be blank."; return false; }
        if (name is "." or "..") { error = "Folder names cannot be . or ..."; return false; }
        if (!string.Equals(name, name.Trim(), StringComparison.Ordinal) || name.EndsWith('.') || name.EndsWith(' ')) { error = "Folder names cannot start or end with whitespace or end with a period."; return false; }
        if (Invalid.IsMatch(name)) { error = "Folder names cannot contain Windows-invalid characters."; return false; }
        if (Reserved.Contains(name)) { error = "That name is reserved by Windows."; return false; }
        if (name.Contains("..", StringComparison.Ordinal)) { error = "Folder names cannot contain path-traversal sequences."; return false; }
        error = "";
        return true;
    }
}

public sealed record StagedKitFolder(string StableId, string? ParentId, string Name, string OldRelativePath, int IconIndex = 1014, int HeightOffset = 5, int SortGroup = 8, string? Tip = null);
public sealed record CalculatedKitFolder(string StableId, string? ParentId, string Name, string OldRelativePath, string NewRelativePath, int Depth, int IconIndex, int HeightOffset, int SortGroup, string Tip);
public sealed record FlStudioMetadataApplyResult(string RootPath, IReadOnlyList<CalculatedKitFolder> Folders, IReadOnlyList<string> Sidecars);
public sealed record FlStudioIconApplyResult(int FolderCount, IReadOnlyList<string> Sidecars);
public sealed record FlStudioMetadataRecoveryResult(int FoldersRestored, int SidecarsRestored, IReadOnlyList<string> RemainingStages);

public static class KitFolderLayout
{
    public static IReadOnlyList<CalculatedKitFolder> Calculate(IReadOnlyList<StagedKitFolder> folders)
    {
        if (folders.Count == 0) throw new InvalidDataException("The kit has no folder metadata entries.");
        var byId = new Dictionary<string, StagedKitFolder>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder.StableId) || !byId.TryAdd(folder.StableId, folder)) throw new InvalidDataException("Folder stable IDs must be unique and non-blank.");
            if (!KitFolderNames.TryValidate(folder.Name, out var error)) throw new InvalidDataException($"{folder.Name}: {error}");
            if (folder.IconIndex < 0) throw new InvalidDataException($"{folder.Name}: IconIndex cannot be negative.");
        }
        var roots = folders.Where(x => x.ParentId is null).ToArray();
        if (roots.Length != 1) throw new InvalidDataException("Exactly one kit-root folder is required.");
        foreach (var folder in folders.Where(x => x.ParentId is not null)) if (!byId.ContainsKey(folder.ParentId!)) throw new InvalidDataException($"{folder.Name}: parent ID does not exist.");
        var duplicate = folders.GroupBy(x => x.ParentId ?? "<root>", StringComparer.OrdinalIgnoreCase).SelectMany(group => group.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Where(names => names.Count() > 1).Select(names => names.Key)).FirstOrDefault();
        if (duplicate is not null) throw new InvalidDataException("Duplicate sibling folder name: " + duplicate);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cache = new Dictionary<string, (int Depth, string Path)>(StringComparer.OrdinalIgnoreCase);
        (int Depth, string Path) Resolve(StagedKitFolder folder)
        {
            if (cache.TryGetValue(folder.StableId, out var resolved)) return resolved;
            if (!visiting.Add(folder.StableId)) throw new InvalidDataException("Folder hierarchy contains a cycle.");
            if (folder.ParentId is null) resolved = (0, "");
            else
            {
                var parent = byId[folder.ParentId];
                var parentLayout = Resolve(parent);
                resolved = (parentLayout.Depth + 1, string.IsNullOrEmpty(parentLayout.Path) ? folder.Name : Path.Combine(parentLayout.Path, folder.Name));
            }
            visiting.Remove(folder.StableId);
            cache[folder.StableId] = resolved;
            return resolved;
        }
        return folders.Select(folder => { var layout = Resolve(folder); return new CalculatedKitFolder(folder.StableId, folder.ParentId, folder.Name, folder.OldRelativePath, layout.Path, layout.Depth, folder.IconIndex, folder.HeightOffset, folder.SortGroup, FlStudioNfo.SanitizeTip(folder.Tip)); }).OrderBy(x => x.Depth).ThenBy(x => x.NewRelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}

public static class FlStudioKitMetadata
{
    private const string JournalFileName = "metadata-transaction.json";

    private sealed class MetadataTransactionJournal
    {
        public List<string> FolderOldRelativePaths { get; set; } = new();
        public List<string> SidecarPaths { get; set; } = new();
    }

    public static FlStudioMetadataRecoveryResult RecoverInterruptedApply(string rootPath, IReadOnlyList<StagedKitFolder> stagedFolders)
    {
        if (!GeneratedKitSafety.IsGeneratedKit(rootPath)) return new(0, 0, Array.Empty<string>());
        var layout = KitFolderLayout.Calculate(stagedFolders);
        var root = layout.Single(x => x.ParentId is null);
        var oldRoot = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(oldRoot) ?? throw new InvalidDataException("The kit root has no parent directory.");
        var nonRoot = layout.Where(x => x.ParentId is not null).ToArray();
        var legacyStageOrder = nonRoot.OrderByDescending(x => OldDepth(x.OldRelativePath)).ToArray();
        var allowedRelativePaths = nonRoot.Select(x => NormalizeRelative(x.OldRelativePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allowedSidecars = nonRoot.Select(x => FlStudioNfo.SidecarPath(SafeChildPath(oldRoot, x.OldRelativePath))).Append(FlStudioNfo.SidecarPath(oldRoot)).Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stages = Directory.EnumerateDirectories(parent, $".{Path.GetFileName(oldRoot)}.stashkit-stage-*", SearchOption.TopDirectoryOnly).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        var restoredFolders = 0;
        var restoredSidecars = 0;
        var remaining = new List<string>();

        foreach (var stage in stages)
        {
            if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(stage) < TimeSpan.FromSeconds(10)) { remaining.Add(stage); continue; }
            var journal = ReadJournal(stage);
            var folderRoot = Path.Combine(stage, "folders");
            if (Directory.Exists(folderRoot))
            {
                foreach (var temporary in Directory.EnumerateDirectories(folderRoot).OrderBy(NumericLeaf))
                {
                    var index = NumericLeaf(temporary);
                    string relative;
                    if (journal is not null && index >= 0 && index < journal.FolderOldRelativePaths.Count) relative = journal.FolderOldRelativePaths[index];
                    else if (index >= 0 && index < legacyStageOrder.Length) relative = legacyStageOrder[index].OldRelativePath;
                    else throw new InvalidDataException($"Interrupted metadata stage has an unknown folder slot: {temporary}");
                    relative = NormalizeRelative(relative);
                    if (!allowedRelativePaths.Contains(relative)) throw new InvalidDataException($"Interrupted metadata stage references an unknown kit folder: {relative}");
                    var original = SafeChildPath(oldRoot, relative);
                    if (Directory.Exists(original)) throw new IOException($"Recovery found both the original and staged copy of '{relative}'. The staged copy was preserved at {temporary}.");
                    Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                    Directory.Move(temporary, original);
                    restoredFolders++;
                }
                if (!Directory.EnumerateFileSystemEntries(folderRoot).Any()) Directory.Delete(folderRoot);
            }

            var sidecarRoot = Path.Combine(stage, "sidecars");
            if (Directory.Exists(sidecarRoot))
            {
                var backups = Directory.EnumerateFiles(sidecarRoot, "*.nfo", SearchOption.TopDirectoryOnly).OrderBy(NumericLeaf).ToArray();
                IReadOnlyList<string>? destinations = null;
                if (journal is not null && backups.All(path => NumericLeaf(path) >= 0 && NumericLeaf(path) < journal.SidecarPaths.Count)) destinations = journal.SidecarPaths;
                else if (backups.Length == allowedSidecars.Count) destinations = nonRoot.Select(x => FlStudioNfo.SidecarPath(SafeChildPath(oldRoot, x.OldRelativePath))).Append(FlStudioNfo.SidecarPath(oldRoot)).ToArray();
                if (destinations is not null)
                {
                    foreach (var backup in backups)
                    {
                        var destination = Path.GetFullPath(destinations[NumericLeaf(backup)]);
                        if (!allowedSidecars.Contains(destination)) throw new InvalidDataException("Interrupted metadata stage references an unexpected sidecar path.");
                        if (File.Exists(destination)) throw new IOException($"Recovery found both an active and staged metadata sidecar: {destination}");
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        File.Move(backup, destination);
                        restoredSidecars++;
                    }
                }
                if (!Directory.EnumerateFileSystemEntries(sidecarRoot).Any()) Directory.Delete(sidecarRoot);
            }

            var journalPath = Path.Combine(stage, JournalFileName);
            if (File.Exists(journalPath) && !Directory.EnumerateFileSystemEntries(stage).Where(path => !path.Equals(journalPath, StringComparison.OrdinalIgnoreCase)).Any()) File.Delete(journalPath);
            if (!Directory.EnumerateFileSystemEntries(stage).Any()) Directory.Delete(stage); else remaining.Add(stage);
        }

        return new(restoredFolders, restoredSidecars, remaining);
    }

    public static FlStudioMetadataApplyResult Apply(string rootPath, string baseColor, IReadOnlyList<StagedKitFolder> stagedFolders, bool preserveExistingIconIndexes = false)
    {
        if (!GeneratedKitSafety.IsGeneratedKit(rootPath)) throw new InvalidDataException("Only a marker-valid generated kit can receive FL Studio metadata.");
        var normalizedBase = FlStudioColors.NormalizeWebHex(baseColor);
        var layout = KitFolderLayout.Calculate(stagedFolders);
        var root = layout.Single(x => x.ParentId is null);
        var oldRoot = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(oldRoot) ?? throw new InvalidDataException("The kit root has no parent directory.");
        var newRoot = Path.Combine(parent, root.Name);
        if (!string.Equals(oldRoot, newRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(newRoot)) throw new IOException("Another folder already uses the requested kit name.");
        var nonRoot = layout.Where(x => x.ParentId is not null).ToArray();
        var recovery = RecoverInterruptedApply(oldRoot, stagedFolders);
        if (recovery.RemainingStages.Count > 0) throw new IOException("An interrupted metadata transaction still needs attention: " + recovery.RemainingStages[0]);
        var oldLocations = nonRoot.ToDictionary(x => x.StableId, x => SafeChildPath(oldRoot, x.OldRelativePath), StringComparer.OrdinalIgnoreCase);
        foreach (var folder in nonRoot) if (!Directory.Exists(oldLocations[folder.StableId])) throw new DirectoryNotFoundException($"Folder metadata source is missing: {oldLocations[folder.StableId]}");
        var currentIconIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (preserveExistingIconIndexes)
        {
            foreach (var folder in layout)
            {
                var currentFolder = folder.ParentId is null ? oldRoot : oldLocations[folder.StableId];
                currentIconIndexes[folder.StableId] = FlStudioNfo.TryReadIconIndex(currentFolder, out var currentIcon) ? currentIcon : folder.IconIndex;
            }
        }
        var movingFolders = nonRoot.Where(folder => !PathEquals(folder.OldRelativePath, folder.NewRelativePath)).ToArray();
        var oldManaged = oldLocations.Values.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in movingFolders)
        {
            var target = SafeChildPath(oldRoot, folder.NewRelativePath);
            if (Directory.Exists(target) && !oldManaged.Contains(Path.GetFullPath(target))) throw new IOException("A non-managed folder already occupies: " + target);
        }
        var stage = Path.Combine(parent, $".{Path.GetFileName(oldRoot)}.stashkit-stage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stage);
        var stagedLocations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sidecarBackups = new List<(string Original, string Backup)>();
        var writtenSidecars = new List<string>();
        byte[]? manifestBackup = null;
        var manifestPath = Path.Combine(oldRoot, "_metadata", "manifest.json");
        var rootMoved = false;
        var stageOrder = movingFolders.OrderByDescending(x => OldDepth(x.OldRelativePath)).ToArray();
        try
        {
            if (File.Exists(manifestPath)) manifestBackup = File.ReadAllBytes(manifestPath);
            var oldSidecars = nonRoot.Select(x => FlStudioNfo.SidecarPath(oldLocations[x.StableId])).Append(FlStudioNfo.SidecarPath(oldRoot)).Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists).ToArray();
            var journal = new MetadataTransactionJournal { FolderOldRelativePaths = stageOrder.Select(x => x.OldRelativePath).ToList(), SidecarPaths = oldSidecars.Select(Path.GetFullPath).ToList() };
            File.WriteAllText(Path.Combine(stage, JournalFileName), JsonSerializer.Serialize(journal, new JsonSerializerOptions { WriteIndented = true }));
            var sidecarStage = Directory.CreateDirectory(Path.Combine(stage, "sidecars")).FullName;
            for (var i = 0; i < oldSidecars.Length; i++) { var backup = Path.Combine(sidecarStage, i.ToString(CultureInfo.InvariantCulture) + ".nfo"); File.Move(oldSidecars[i], backup); sidecarBackups.Add((oldSidecars[i], backup)); }
            var folderStage = Directory.CreateDirectory(Path.Combine(stage, "folders")).FullName;
            for (var i = 0; i < stageOrder.Length; i++) { var temporary = Path.Combine(folderStage, i.ToString(CultureInfo.InvariantCulture)); Directory.Move(oldLocations[stageOrder[i].StableId], temporary); stagedLocations[stageOrder[i].StableId] = temporary; }
            foreach (var folder in movingFolders.OrderBy(x => x.Depth)) { var destination = SafeChildPath(oldRoot, folder.NewRelativePath); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); Directory.Move(stagedLocations[folder.StableId], destination); }
            if (movingFolders.Length > 0) UpdateManifest(manifestPath, movingFolders);
            if (!string.Equals(oldRoot, newRoot, StringComparison.OrdinalIgnoreCase)) { Directory.Move(oldRoot, newRoot); rootMoved = true; }
            foreach (var folder in layout)
            {
                var targetFolder = folder.ParentId is null ? newRoot : SafeChildPath(newRoot, folder.NewRelativePath);
                var color = FlStudioColors.Derive(normalizedBase, folder.Depth);
                var iconIndex = preserveExistingIconIndexes ? currentIconIndexes[folder.StableId] : folder.IconIndex;
                writtenSidecars.Add(FlStudioNfo.WriteBesideFolder(targetFolder, new(color.FlStudioBgr, iconIndex, folder.HeightOffset, folder.SortGroup, folder.Tip)));
            }
            Directory.Delete(stage, true);
            return new(newRoot, layout, writtenSidecars);
        }
        catch
        {
            try
            {
                foreach (var sidecar in writtenSidecars.Where(File.Exists)) File.Delete(sidecar);
                if (rootMoved && Directory.Exists(newRoot) && !Directory.Exists(oldRoot)) Directory.Move(newRoot, oldRoot);
                foreach (var folder in nonRoot.OrderByDescending(x => x.Depth))
                {
                    var placed = SafeChildPath(oldRoot, folder.NewRelativePath);
                    if (!Directory.Exists(placed)) continue;
                    if (!stagedLocations.TryGetValue(folder.StableId, out var temporary)) continue;
                    if (Directory.Exists(temporary)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
                    Directory.Move(placed, temporary);
                }
                foreach (var folder in nonRoot.OrderBy(x => OldDepth(x.OldRelativePath)))
                {
                    var temporary = stagedLocations.GetValueOrDefault(folder.StableId);
                    if (temporary is null || !Directory.Exists(temporary)) continue;
                    var original = oldLocations[folder.StableId];
                    Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                    Directory.Move(temporary, original);
                }
                foreach (var (original, backup) in sidecarBackups) if (File.Exists(backup)) { Directory.CreateDirectory(Path.GetDirectoryName(original)!); File.Move(backup, original, true); }
                if (manifestBackup is not null) File.WriteAllBytes(manifestPath, manifestBackup);
                if (Directory.Exists(stage)) Directory.Delete(stage, true);
            }
            catch { }
            throw;
        }
    }

    public static FlStudioIconApplyResult ApplyIconIndexes(string rootPath, string baseColor, IReadOnlyList<StagedKitFolder> stagedFolders)
    {
        if (!GeneratedKitSafety.IsGeneratedKit(rootPath)) throw new InvalidDataException("Only a marker-valid generated kit can receive FL Studio metadata.");
        var normalizedBase = FlStudioColors.NormalizeWebHex(baseColor);
        var layout = KitFolderLayout.Calculate(stagedFolders);
        var oldRoot = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var recovery = RecoverInterruptedApply(oldRoot, stagedFolders);
        if (recovery.RemainingStages.Count > 0) throw new IOException("An interrupted metadata transaction still needs attention: " + recovery.RemainingStages[0]);

        var targets = layout.Select(folder => new
        {
            Folder = folder,
            Path = folder.ParentId is null ? oldRoot : SafeChildPath(oldRoot, folder.OldRelativePath)
        }).ToArray();
        foreach (var target in targets)
            if (!Directory.Exists(target.Path)) throw new DirectoryNotFoundException("Folder metadata target is missing: " + target.Path);

        var backups = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
        var written = new List<string>();
        try
        {
            foreach (var target in targets)
            {
                var sidecar = FlStudioNfo.SidecarPath(target.Path);
                backups[sidecar] = File.Exists(sidecar) ? File.ReadAllBytes(sidecar) : null;
                string contents;
                if (File.Exists(sidecar)) contents = FlStudioNfo.UpsertIconIndex(File.ReadAllText(sidecar), target.Folder.IconIndex);
                else
                {
                    var color = FlStudioColors.Derive(normalizedBase, target.Folder.Depth);
                    contents = FlStudioNfo.Serialize(new(color.FlStudioBgr, target.Folder.IconIndex, target.Folder.HeightOffset, target.Folder.SortGroup, target.Folder.Tip));
                }
                var temp = sidecar + ".tmp";
                File.WriteAllText(temp, contents, new UTF8Encoding(false));
                File.Move(temp, sidecar, true);
                written.Add(sidecar);
            }

            foreach (var target in targets)
                if (!FlStudioNfo.TryReadIconIndex(target.Path, out var actual) || actual != target.Folder.IconIndex)
                    throw new IOException("IconIndex verification failed for: " + target.Path);

            return new(targets.Length, written);
        }
        catch
        {
            foreach (var (path, backup) in backups)
            {
                try
                {
                    if (backup is null) { if (File.Exists(path)) File.Delete(path); }
                    else File.WriteAllBytes(path, backup);
                    var temp = path + ".tmp";
                    if (File.Exists(temp)) File.Delete(temp);
                }
                catch { }
            }
            throw;
        }
    }

    private static MetadataTransactionJournal? ReadJournal(string stage)
    {
        var path = Path.Combine(stage, JournalFileName);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<MetadataTransactionJournal>(File.ReadAllText(path)); }
        catch (JsonException ex) { throw new InvalidDataException("Interrupted metadata transaction journal is unreadable.", ex); }
    }

    private static int NumericLeaf(string path) => int.TryParse(Path.GetFileNameWithoutExtension(path), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : -1;
    private static string NormalizeRelative(string path) => path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar).Trim(Path.DirectorySeparatorChar);
    private static bool PathEquals(string first, string second) => string.Equals(NormalizeRelative(first), NormalizeRelative(second), StringComparison.OrdinalIgnoreCase);

    private static void UpdateManifest(string manifestPath, IReadOnlyList<CalculatedKitFolder> folders)
    {
        if (!File.Exists(manifestPath)) return;
        var array = JsonNode.Parse(File.ReadAllText(manifestPath))?.AsArray() ?? throw new InvalidDataException("Generated-kit manifest is unreadable.");
        var mappings = folders.Where(x => x.ParentId is not null).OrderByDescending(x => x.OldRelativePath.Length).ToArray();
        foreach (var node in array)
        {
            if (node is not JsonObject entry || entry["RelativePath"]?.GetValue<string>() is not { } relative) continue;
            var mapping = mappings.FirstOrDefault(folder => IsWithin(relative, folder.OldRelativePath));
            if (mapping is null) continue;
            var remainder = Path.GetRelativePath(mapping.OldRelativePath, relative);
            entry["RelativePath"] = Path.Combine(mapping.NewRelativePath, remainder);
        }
        var temp = manifestPath + ".tmp";
        File.WriteAllText(temp, array.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, manifestPath, true);
    }

    private static bool IsWithin(string path, string folder) => path.Equals(folder, StringComparison.OrdinalIgnoreCase) || path.StartsWith(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static int OldDepth(string relativePath) => string.IsNullOrWhiteSpace(relativePath) ? 0 : relativePath.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries).Length;
    private static string SafeChildPath(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Folder path escaped the generated kit root.");
        return full;
    }
}
