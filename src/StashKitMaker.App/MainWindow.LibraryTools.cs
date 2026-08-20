using NAudio.Wave;
using StashKitMaker.Core;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using DataFormats = System.Windows.DataFormats;
using DataObject = System.Windows.DataObject;
using DragDropEffects = System.Windows.DragDropEffects;
using MessageBox = System.Windows.MessageBox;

namespace StashKitMaker.App;

internal sealed class VaultSoundItem
{
    public string StableId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string AudioPath { get; init; } = "";
    public string Category { get; init; } = "Unknown";
    public string KitName { get; init; } = "";
    public string SizeLabel { get; init; } = "";
    public string FileType => Path.GetExtension(AudioPath).TrimStart('.').ToUpperInvariant();
}

internal sealed class VaultCategoryCount
{
    public string Category { get; init; } = "";
    public int Count { get; init; }
    public string Label => $"{Category}   {Count}";
}

internal sealed class MidiStackItem
{
    public string StableId { get; init; } = "";
    public string MidiPath { get; init; } = "";
    public string ProjectPath { get; init; } = "";
    public string ProjectName { get; init; } = "";
    public int PatternId { get; init; }
    public string PatternName { get; init; } = "";
    public int ChannelId { get; init; }
    public string ChannelName { get; init; } = "";
    public string StoredSamplePath { get; init; } = "";
    public string AudioPath { get; init; } = "";
    public string Category { get; init; } = "Unknown";
    public int Ppq { get; init; } = 96;
    public double TempoBpm { get; init; } = 130;
    public IReadOnlyList<MidiNoteEvent> Notes { get; init; } = Array.Empty<MidiNoteEvent>();
    public string MidiName => Path.GetFileNameWithoutExtension(MidiPath);
    public string MatchedSound => !string.IsNullOrWhiteSpace(AudioPath) ? Path.GetFileName(AudioPath) : Path.GetFileName(StoredSamplePath);
    public string NoteCountLabel => Notes.Count.ToString("N0");
    public string LengthLabel
    {
        get
        {
            if (Notes.Count == 0) return "0.0 s";
            var ticks = Notes.Max(note => (long)note.Position + note.Length);
            return $"{ticks * 60d / Math.Max(1, Ppq) / Math.Max(1, TempoBpm):0.0} s";
        }
    }
    public string Availability => !File.Exists(MidiPath) ? "MIDI missing" : File.Exists(AudioPath) ? "Ready" : "Sound missing";
}

internal sealed class RecoveryReferenceItem
{
    public string Key { get; init; } = "";
    public string ProjectName { get; init; } = "";
    public string ProjectPath { get; init; } = "";
    public string StoredPath { get; init; } = "";
    public string Status { get; init; } = "Missing";
    public string CandidatePath { get; init; } = "";
    public int CandidateCount { get; init; }
    public bool HasOverride { get; init; }
    public string ReferenceName => Path.GetFileName(StoredPath);
    public string MatchLabel => !string.IsNullOrWhiteSpace(CandidatePath) ? CandidatePath : CandidateCount > 1 ? $"{CandidateCount} possible matches" : "No match found";
}

internal sealed class StackExportEntry
{
    public string StableId { get; init; } = "";
    public string PatternName { get; init; } = "";
    public string ChannelName { get; init; } = "";
    public string Category { get; init; } = "";
    public string MidiRelativePath { get; init; } = "";
    public string SoundRelativePath { get; init; } = "";
    public string MidiSha256 { get; init; } = "";
    public string SoundSha256 { get; init; } = "";
}

public partial class MainWindow
{
    private static readonly HashSet<string> VaultAudioExtensions = new(new[] { ".wav", ".mp3", ".flac", ".ogg", ".aif", ".aiff" }, StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<VaultSoundItem> vaultItems = new();
    private readonly ObservableCollection<MidiStackItem> stackItems = new();
    private readonly ObservableCollection<RecoveryReferenceItem> recoveryItems = new();
    private ICollectionView? vaultView;
    private ICollectionView? stackView;
    private string? currentVaultPlayingId;
    private string? currentMidiPlayingId;
    private WaveOutEvent? stackAudioOutput;
    private bool suppressVaultFilters;
    private System.Windows.Point vaultDragStart;

    private void ConfigureLibraryTools()
    {
        VaultGrid.ItemsSource = vaultItems;
        StackGrid.ItemsSource = stackItems;
        RecoveryGrid.ItemsSource = recoveryItems;
        vaultView = CollectionViewSource.GetDefaultView(vaultItems);
        vaultView.Filter = FilterVaultItem;
        stackView = CollectionViewSource.GetDefaultView(stackItems);
        stackView.Filter = FilterStackItem;
        foreach (var root in appState.SampleLibraryRoots.Where(Directory.Exists))
            if (!sampleLibraryRoots.Contains(root, StringComparer.OrdinalIgnoreCase)) sampleLibraryRoots.Add(root);
        StackNameText.Text = appState.StackName;
    }

    private void LoadVault()
    {
        vaultItems.Clear();
        var configuredRoot = Path.Combine(DefaultKitParent, DefaultKitName);
        var roots = appState.History.OrderByDescending(x => x.CreatedUtc).Select(x => x.KitPath)
            .Concat(DiscoverGeneratedKits()).Prepend(configuredRoot)
            .Where(path => Directory.Exists(path) && GeneratedKitSafety.IsGeneratedKit(path))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unreadable = 0;
        foreach (var root in roots)
        {
            var manifest = Path.Combine(root, "_metadata", "manifest.json");
            if (!File.Exists(manifest)) continue;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(manifest));
                foreach (var item in document.RootElement.EnumerateArray())
                {
                    var stableId = item.TryGetProperty("Hash", out var hashElement) ? hashElement.GetString() : null;
                    var relative = item.TryGetProperty("RelativePath", out var relativeElement) ? relativeElement.GetString() : null;
                    if (string.IsNullOrWhiteSpace(stableId) || string.IsNullOrWhiteSpace(relative)) continue;
                    var audioPath = Path.Combine(root, relative);
                    if (!File.Exists(audioPath) || !VaultAudioExtensions.Contains(Path.GetExtension(audioPath))) continue;
                    if (!seen.Add(stableId)) continue;
                    var rawCategory = item.TryGetProperty("Category", out var categoryElement) ? categoryElement.GetString() ?? "Unknown" : "Unknown";
                    var category = Enum.TryParse<SampleCategory>(rawCategory, out var parsed) ? CategoryFolders.For(parsed).Replace(Path.DirectorySeparatorChar.ToString(), " › ") : rawCategory;
                    var displayName = appState.KitEntries.TryGetValue(stableId, out var state) ? SafeDisplayName(state.DisplayName, Path.GetFileNameWithoutExtension(audioPath)) : Path.GetFileNameWithoutExtension(audioPath);
                    vaultItems.Add(new VaultSoundItem
                    {
                        StableId = stableId,
                        DisplayName = displayName,
                        AudioPath = audioPath,
                        Category = category,
                        KitName = Path.GetFileName(root),
                        SizeLabel = $"{new FileInfo(audioPath).Length / 1024d:0.#} KB"
                    });
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { unreadable++; }
        }

        suppressVaultFilters = true;
        var categories = vaultItems.Select(x => x.Category).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).Prepend("All categories").ToArray();
        VaultCategoryCombo.ItemsSource = categories;
        VaultCategoryCombo.SelectedIndex = 0;
        VaultCategoryList.ItemsSource = vaultItems.GroupBy(x => x.Category, StringComparer.OrdinalIgnoreCase).OrderByDescending(x => x.Count()).ThenBy(x => x.Key).Select(x => new VaultCategoryCount { Category = x.Key, Count = x.Count() }).ToArray();
        suppressVaultFilters = false;
        vaultView?.Refresh();
        VaultStatusText.Text = $"{vaultItems.Count:N0} unique sounds across {roots.Length} generated kit{(roots.Length == 1 ? "" : "s")}." + (unreadable > 0 ? $" {unreadable} manifest{(unreadable == 1 ? " was" : "s were")} unreadable." : " Drag any selected sound straight into your DAW.");
    }

    private bool FilterVaultItem(object value)
    {
        if (value is not VaultSoundItem item) return false;
        var search = VaultSearchText?.Text?.Trim() ?? "";
        var category = VaultCategoryCombo?.SelectedItem?.ToString() ?? "All categories";
        return (string.IsNullOrWhiteSpace(search) || item.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase) || item.KitName.Contains(search, StringComparison.OrdinalIgnoreCase) || item.Category.Contains(search, StringComparison.OrdinalIgnoreCase))
            && (category == "All categories" || item.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
    }

    private void VaultFilter_Changed(object sender, EventArgs e) { if (!suppressVaultFilters) vaultView?.Refresh(); }
    private void RefreshVault_Click(object sender, RoutedEventArgs e) { StopAllPlayback(); LoadVault(); StatusText.Text = "Sound Vault refreshed · Offline"; }

    private void PlayVaultSelected_Click(object sender, RoutedEventArgs e)
    {
        if (VaultGrid.SelectedItem is not VaultSoundItem item) { MessageBox.Show("Select a sound from the Vault first.", "Nothing selected", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (currentVaultPlayingId == item.StableId) { StopAllPlayback(); return; }
        PlayVaultItem(item);
    }

    private void VaultGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) { if (VaultGrid.SelectedItem is VaultSoundItem item) PlayVaultItem(item); }

    private void PlayVaultItem(VaultSoundItem item)
    {
        if (!File.Exists(item.AudioPath)) { StatusText.Text = "Vault sound is missing: " + item.DisplayName; return; }
        try
        {
            StopAllPlayback();
            reviewPlayer.Open(new Uri(item.AudioPath, UriKind.Absolute));
            reviewPlayer.Play();
            currentVaultPlayingId = item.StableId;
            VaultPlayButton.Content = "■ Stop";
            VaultNowPlayingText.Text = $"Playing: {item.DisplayName} · {item.Category}";
            StatusText.Text = "Playing from Sound Vault";
        }
        catch (Exception exception) when (exception is UriFormatException or InvalidOperationException) { StopAllPlayback(); VaultNowPlayingText.Text = "Could not play: " + exception.Message; }
    }

    private void OpenVaultSoundFolder_Click(object sender, RoutedEventArgs e)
    {
        if (VaultGrid.SelectedItem is not VaultSoundItem item || !File.Exists(item.AudioPath)) { MessageBox.Show("Select an available Vault sound first.", "Nothing selected", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.AudioPath}\"") { UseShellExecute = true });
    }

#if false // Retained only as migration reference; the live Stacks page is MIDI-pattern based.
    private void AddVaultSelectionToStack_Click(object sender, RoutedEventArgs e)
    {
        var selected = VaultGrid.SelectedItems.Cast<VaultSoundItem>().ToArray();
        if (selected.Length == 0 && VaultGrid.SelectedItem is VaultSoundItem single) selected = new[] { single };
        if (selected.Length == 0) { MessageBox.Show("Select one or more Vault sounds first.", "Nothing selected", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var added = 0;
        foreach (var item in selected)
        {
            if (stackItems.Any(x => x.StableId.Equals(item.StableId, StringComparison.OrdinalIgnoreCase))) continue;
            stackItems.Add(new StackLayerItem { StableId = item.StableId, DisplayName = item.DisplayName, AudioPath = item.AudioPath, Category = item.Category, KitName = item.KitName });
            added++;
        }
        PersistStack();
        RefreshStackSummary();
        StatusText.Text = added == 0 ? "Those sounds are already in the Stack." : $"Added {added} sound{(added == 1 ? "" : "s")} to the Stack.";
    }

    private void VaultGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => vaultDragStart = e.GetPosition(null);

    private void VaultGrid_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || VaultGrid.SelectedItem is not VaultSoundItem item || !File.Exists(item.AudioPath)) return;
        var current = e.GetPosition(null);
        if (Math.Abs(current.X - vaultDragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(current.Y - vaultDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var data = new DataObject(DataFormats.FileDrop, new[] { item.AudioPath });
        DragDrop.DoDragDrop(VaultGrid, data, DragDropEffects.Copy);
    }

    private void LoadStack()
    {
        stackItems.Clear();
        foreach (var item in appState.ActiveStack.Where(x => x is not null))
            stackItems.Add(new StackLayerItem { StableId = item.StableId, DisplayName = item.DisplayName, AudioPath = item.AudioPath, Category = item.Category, KitName = item.KitName });
        StackNameText.Text = SafeDisplayName(appState.StackName, "My Stack");
        RefreshStackSummary();
    }

    private void PersistStack()
    {
        appState.StackName = SafeDisplayName(StackNameText.Text, "My Stack");
        appState.ActiveStack = stackItems.Select(x => new StackLayerState { StableId = x.StableId, DisplayName = x.DisplayName, AudioPath = x.AudioPath, Category = x.Category, KitName = x.KitName }).ToList();
        AppStateStore.Save(appState);
    }

    private void RefreshStackSummary()
    {
        var ready = stackItems.Count(x => File.Exists(x.AudioPath));
        StackSummaryText.Text = $"Layers: {stackItems.Count} · Ready: {ready} · Missing: {stackItems.Count - ready}\nStack playback starts every layer together; no hidden time-stretching or pitch changes are applied.";
    }

    private void RemoveStackLayer_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in StackGrid.SelectedItems.Cast<StackLayerItem>().ToArray()) stackItems.Remove(item);
        StopAllPlayback(); PersistStack(); RefreshStackSummary();
    }

    private void ClearStack_Click(object sender, RoutedEventArgs e) { StopAllPlayback(); stackItems.Clear(); PersistStack(); RefreshStackSummary(); }

    private void PlayStack_Click(object sender, RoutedEventArgs e)
    {
        if (stackPlayers.Count > 0) { StopAllPlayback(); return; }
        var ready = stackItems.Where(x => File.Exists(x.AudioPath)).ToArray();
        if (ready.Length == 0) { MessageBox.Show("Add available sounds from the Vault first.", "Stack is empty", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        StopAllPlayback();
        stackPlayersRemaining = ready.Length;
        var volume = Math.Clamp(0.9 / Math.Sqrt(ready.Length), 0.18, 0.75);
        foreach (var layer in ready)
        {
            var player = new MediaPlayer { Volume = volume };
            player.MediaEnded += (_, _) => Dispatcher.Invoke(StackPlayerCompleted);
            player.MediaFailed += (_, _) => Dispatcher.Invoke(StackPlayerCompleted);
            player.Open(new Uri(layer.AudioPath, UriKind.Absolute));
            stackPlayers.Add(player);
        }
        foreach (var player in stackPlayers) player.Play();
        StackPlayButton.Content = "■ Stop stack";
        StackPlaybackText.Text = $"Playing {ready.Length} layer{(ready.Length == 1 ? "" : "s")} together";
        StatusText.Text = "Stack preview playing";
    }

    private void StackPlayerCompleted()
    {
        stackPlayersRemaining--;
        if (stackPlayersRemaining <= 0) StopAllPlayback();
    }

    private void StopStackPlayers()
    {
        foreach (var player in stackPlayers) { try { player.Stop(); player.Close(); } catch (InvalidOperationException) { } }
        stackPlayers.Clear();
        stackPlayersRemaining = 0;
    }

    private void ResetLibraryPlaybackUi()
    {
        currentVaultPlayingId = null;
        if (VaultPlayButton is not null) VaultPlayButton.Content = "▶ Play selected";
        if (VaultNowPlayingText is not null) VaultNowPlayingText.Text = "";
        if (StackPlayButton is not null) StackPlayButton.Content = "▶ Play stack";
        if (StackPlaybackText is not null) StackPlaybackText.Text = "";
    }

    private void ExportStack_Click(object sender, RoutedEventArgs e)
    {
        var stackName = WindowsNames.Sanitize(StackNameText.Text.Trim());
        var ready = stackItems.Where(x => File.Exists(x.AudioPath)).ToArray();
        if (string.IsNullOrWhiteSpace(stackName) || ready.Length == 0) { MessageBox.Show("Give the Stack a name and add at least one available sound.", "Stack not ready", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var root = Path.Combine(DefaultKitParent, "Stack Exports", stackName);
        try
        {
            Directory.CreateDirectory(root);
            var exports = new List<StackExportEntry>();
            var usedHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var layerNumber = 1;
            foreach (var layer in ready)
            {
                var hash = Hashing.Sha256(layer.AudioPath);
                if (!usedHashes.Add(hash)) continue;
                var cleanName = WindowsNames.Sanitize(layer.DisplayName);
                var filename = $"{layerNumber:00} {cleanName}{Path.GetExtension(layer.AudioPath)}";
                var destination = Path.Combine(root, filename);
                var collision = 2;
                while (File.Exists(destination) && !string.Equals(Hashing.Sha256(destination), hash, StringComparison.OrdinalIgnoreCase))
                    destination = Path.Combine(root, $"{layerNumber:00} {cleanName} {collision++}{Path.GetExtension(layer.AudioPath)}");
                if (!File.Exists(destination)) File.Copy(layer.AudioPath, destination, false);
                exports.Add(new StackExportEntry { Layer = layerNumber++, StableId = layer.StableId, DisplayName = layer.DisplayName, Category = layer.Category, RelativePath = Path.GetFileName(destination), Sha256 = hash });
            }
            var manifest = Path.Combine(root, "stack.json");
            File.WriteAllText(manifest, JsonSerializer.Serialize(exports, new JsonSerializerOptions { WriteIndented = true }));
            PersistStack();
            StackExportResultText.Text = $"Exported {exports.Count} unique layer{(exports.Count == 1 ? "" : "s")} to:\n{root}";
            StatusText.Text = "Stack folder exported · source sounds untouched";
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{root}\"") { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { MessageBox.Show(exception.Message, "Stack export failed", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void StackNameText_LostFocus(object sender, RoutedEventArgs e) { StackNameText.Text = SafeDisplayName(StackNameText.Text, "My Stack"); PersistStack(); }
#endif

    private void RefreshRecovery()
    {
        recoveryItems.Clear();
        var resolver = new PathResolver(sampleLibraryRoots);
        var analyzed = projects.Where(x => x.Analysis is not null).ToArray();
        var total = 0;
        var resolved = 0;
        var found = 0;
        var missing = 0;
        var ambiguous = 0;
        foreach (var row in analyzed)
        {
            foreach (var reference in row.Analysis!.SampleReferences)
            {
                total++;
                var key = RecoveryKey(row.Path, reference.StoredPath);
                var hasOverride = appState.SamplePathOverrides.TryGetValue(key, out var manual) && File.Exists(manual);
                var result = hasOverride ? new Resolution(ResolutionStatus.Resolved, manual, new[] { manual! }) : resolver.Resolve(reference.StoredPath, row.Path);
                var directExists = StoredReferenceExists(reference.StoredPath, row.Path);
                if (result.Status == ResolutionStatus.Resolved && directExists && !hasOverride) { resolved++; continue; }
                if (result.Status == ResolutionStatus.Resolved)
                {
                    found++;
                    recoveryItems.Add(new RecoveryReferenceItem { Key = key, ProjectName = row.Name, ProjectPath = row.Path, StoredPath = reference.StoredPath, Status = hasOverride ? "Manual replacement" : "Found in search folder", CandidatePath = result.Path ?? "", CandidateCount = 1, HasOverride = hasOverride });
                }
                else if (result.Status == ResolutionStatus.Ambiguous)
                {
                    ambiguous++;
                    recoveryItems.Add(new RecoveryReferenceItem { Key = key, ProjectName = row.Name, ProjectPath = row.Path, StoredPath = reference.StoredPath, Status = "Choose between matches", CandidateCount = result.Candidates.Count });
                }
                else
                {
                    missing++;
                    recoveryItems.Add(new RecoveryReferenceItem { Key = key, ProjectName = row.Name, ProjectPath = row.Path, StoredPath = reference.StoredPath, Status = "Missing", CandidateCount = 0 });
                }
            }
        }
        RecoveryRootsText.Text = sampleLibraryRoots.Count == 0 ? "No saved search folders yet." : string.Join("\n", sampleLibraryRoots.Select(path => "• " + path));
        RecoverySummaryText.Text = analyzed.Length == 0
            ? "Analyze one or more projects first. Sample Rescue will then find moved files and let you set exact replacements used by the next kit build."
            : $"References: {total} · Already available: {resolved} · Recovered: {found} · Missing: {missing} · Ambiguous: {ambiguous}\nSaved replacements are used by the normal Build Kit pipeline; FLP files are never rewritten.";
    }

    private static bool StoredReferenceExists(string storedPath, string projectPath)
    {
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(storedPath.Trim().Trim('"')).Replace('/', Path.DirectorySeparatorChar);
            return (Path.IsPathRooted(expanded) && File.Exists(expanded)) || File.Exists(Path.Combine(Path.GetDirectoryName(projectPath) ?? "", expanded));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static string RecoveryKey(string projectPath, string storedPath) => Path.GetFullPath(projectPath).Trim().ToLowerInvariant() + "|" + storedPath.Trim().Replace('/', '\\').ToLowerInvariant();

    private Resolution ResolveForBuild(PathResolver resolver, string projectPath, SampleReference reference)
    {
        var key = RecoveryKey(projectPath, reference.StoredPath);
        if (appState.SamplePathOverrides.TryGetValue(key, out var manual) && File.Exists(manual)) return new Resolution(ResolutionStatus.Resolved, manual, new[] { manual });
        return resolver.Resolve(reference.StoredPath, projectPath);
    }

    private void AddRecoveryRoot_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "Choose a folder that may contain moved or missing samples", UseDescriptionForTitle = true };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        if (!sampleLibraryRoots.Contains(dialog.SelectedPath, StringComparer.OrdinalIgnoreCase)) sampleLibraryRoots.Add(dialog.SelectedPath);
        appState.SampleLibraryRoots = sampleLibraryRoots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        AppStateStore.Save(appState);
        RefreshRecovery();
        StatusText.Text = "Sample search folder saved · Rescan complete";
    }

    private void RescanRecovery_Click(object sender, RoutedEventArgs e) { RefreshRecovery(); StatusText.Text = "Sample Rescue scan complete"; }

    private void ChooseRecoveryReplacement_Click(object sender, RoutedEventArgs e)
    {
        if (RecoveryGrid.SelectedItem is not RecoveryReferenceItem item) { MessageBox.Show("Select a missing or ambiguous reference first.", "Nothing selected", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = $"Choose replacement for {item.ReferenceName}", Filter = "Audio files|*.wav;*.mp3;*.flac;*.ogg;*.aif;*.aiff|All files|*.*", Multiselect = false };
        if (dialog.ShowDialog() != true) return;
        appState.SamplePathOverrides[item.Key] = dialog.FileName;
        AppStateStore.Save(appState);
        RefreshRecovery();
        pendingBuild = null;
        BuildButton.IsEnabled = false;
        StatusText.Text = "Replacement saved · Analyze again to refresh the build plan";
    }

    private void ClearRecoveryReplacement_Click(object sender, RoutedEventArgs e)
    {
        if (RecoveryGrid.SelectedItem is not RecoveryReferenceItem item || !appState.SamplePathOverrides.Remove(item.Key)) { StatusText.Text = "The selected reference has no manual replacement."; return; }
        AppStateStore.Save(appState);
        RefreshRecovery();
        pendingBuild = null;
        BuildButton.IsEnabled = false;
        StatusText.Text = "Manual replacement cleared";
    }

    private void OpenRecoveryCandidate_Click(object sender, RoutedEventArgs e)
    {
        if (RecoveryGrid.SelectedItem is not RecoveryReferenceItem item || !File.Exists(item.CandidatePath)) { MessageBox.Show("That reference does not have one confirmed candidate yet.", "No candidate", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.CandidatePath}\"") { UseShellExecute = true });
    }
}
