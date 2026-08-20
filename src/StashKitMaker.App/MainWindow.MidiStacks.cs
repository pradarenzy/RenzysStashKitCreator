using NAudio.Wave;
using NAudio;
using StashKitMaker.Core;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using DataFormats = System.Windows.DataFormats;
using DataObject = System.Windows.DataObject;
using DragDropEffects = System.Windows.DragDropEffects;
using MessageBox = System.Windows.MessageBox;

namespace StashKitMaker.App;

public partial class MainWindow
{
    private bool suppressStackFilters;

    private void FindMatchingMidi_Click(object sender, RoutedEventArgs e)
    {
        if (VaultGrid.SelectedItem is not VaultSoundItem sound)
        {
            MessageBox.Show("Select a Vault sound first.", "Nothing selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        LoadStack();
        StackCategoryCombo.SelectedItem = StackCategoryCombo.Items.Cast<string>().FirstOrDefault(category => category.Equals(sound.Category, StringComparison.OrdinalIgnoreCase)) ?? "All categories";
        var stacksButton = FindVisualChildren<System.Windows.Controls.Button>(this).FirstOrDefault(button => string.Equals(button.Tag as string, "Stacks", StringComparison.Ordinal));
        stacksButton?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        StatusText.Text = $"Showing extracted {sound.Category} MIDI matched to {sound.Category} sounds";
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

    private int ExtractMidiPatternsFromAnalysis()
    {
        var analyzedRows = projects.Where(row => row.Analysis is not null).ToArray();
        if (analyzedRows.Length == 0) return 0;
        Directory.CreateDirectory(AppStateStore.MidiExtractionFolder);
        var analyzedPaths = analyzedRows.Select(row => Path.GetFullPath(row.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var retained = appState.ExtractedMidiPatterns.Where(item =>
            string.IsNullOrWhiteSpace(item.ProjectPath) ||
            !analyzedPaths.Contains(Path.GetFullPath(item.ProjectPath))).ToList();
        var extracted = new List<ExtractedMidiState>();
        var classifier = new SampleClassifier();
        var resolver = new PathResolver(sampleLibraryRoots);

        foreach (var row in analyzedRows)
        {
            var analysis = row.Analysis!;
            foreach (var sequence in analysis.MidiPatterns.Where(sequence => sequence.Notes.Count > 0 && !string.IsNullOrWhiteSpace(sequence.SamplePath)))
            {
                var reference = new SampleReference(sequence.SamplePath!, sequence.ChannelName, null, sequence.ChannelId);
                var resolution = ResolveForBuild(resolver, row.Path, reference);
                var audioPath = resolution.Status == ResolutionStatus.Resolved && resolution.Path is not null ? resolution.Path : "";
                var classificationSource = !string.IsNullOrWhiteSpace(audioPath) ? audioPath : sequence.SamplePath!;
                if (SampleScreening.ShouldExclude(classificationSource, out _)) continue;
                var classification = classifier.Classify(classificationSource, sequence.ChannelName);
                if (classification.Category is SampleCategory.DrumLoop or SampleCategory.PercLoop or SampleCategory.MelodyLoop) continue;

                var midiBytes = StandardMidiFile.Create(sequence, analysis.Ppq, analysis.TempoBpm);
                var sourceIdentity = $"{Path.GetFullPath(row.Path)}|pattern:{sequence.PatternId}|channel:{sequence.ChannelId}|{sequence.SamplePath}";
                var stableId = StandardMidiFile.Fingerprint(midiBytes, sourceIdentity);
                var categoryPath = CategoryFolders.For(classification.Category);
                var categoryLabel = categoryPath.Replace(Path.DirectorySeparatorChar.ToString(), " › ").Replace(Path.AltDirectorySeparatorChar.ToString(), " › ");
                var projectFolder = WindowsNames.Sanitize(analysis.ProjectName);
                var midiFolder = Path.Combine(AppStateStore.MidiExtractionFolder, categoryPath, projectFolder);
                Directory.CreateDirectory(midiFolder);
                var baseName = WindowsNames.Sanitize($"{sequence.PatternName} - {sequence.ChannelName}");
                var midiPath = WriteMidiWithoutOverwrite(Path.Combine(midiFolder, $"{baseName} [{stableId[..10]}].mid"), midiBytes);
                extracted.Add(new ExtractedMidiState
                {
                    StableId = stableId,
                    MidiPath = midiPath,
                    ProjectPath = Path.GetFullPath(row.Path),
                    ProjectName = analysis.ProjectName,
                    PatternId = sequence.PatternId,
                    PatternName = sequence.PatternName,
                    ChannelId = sequence.ChannelId,
                    ChannelName = sequence.ChannelName,
                    StoredSamplePath = sequence.SamplePath!,
                    AudioPath = audioPath,
                    Category = categoryLabel,
                    Ppq = analysis.Ppq,
                    TempoBpm = analysis.TempoBpm,
                    Notes = sequence.Notes.ToList(),
                    ExtractedUtc = DateTime.UtcNow
                });
            }
        }

        appState.ExtractedMidiPatterns = retained.Concat(extracted)
            .GroupBy(item => item.StableId, StringComparer.OrdinalIgnoreCase).Select(group => group.First())
            .OrderBy(item => item.Category).ThenBy(item => item.ProjectName).ThenBy(item => item.PatternId).ThenBy(item => item.ChannelId).ToList();
        AppStateStore.Save(appState);
        return extracted.Count;
    }

    private static string WriteMidiWithoutOverwrite(string preferredPath, byte[] bytes)
    {
        if (!File.Exists(preferredPath))
        {
            File.WriteAllBytes(preferredPath, bytes);
            return preferredPath;
        }
        if (File.ReadAllBytes(preferredPath).AsSpan().SequenceEqual(bytes)) return preferredPath;
        var folder = Path.GetDirectoryName(preferredPath)!;
        var stem = Path.GetFileNameWithoutExtension(preferredPath);
        var extension = Path.GetExtension(preferredPath);
        for (var suffix = 2; ; suffix++)
        {
            var candidate = Path.Combine(folder, $"{stem} {suffix}{extension}");
            if (File.Exists(candidate))
            {
                if (File.ReadAllBytes(candidate).AsSpan().SequenceEqual(bytes)) return candidate;
                continue;
            }
            File.WriteAllBytes(candidate, bytes);
            return candidate;
        }
    }

    private void LoadStack()
    {
        stackItems.Clear();
        foreach (var item in appState.ExtractedMidiPatterns.OrderBy(item => item.Category).ThenBy(item => item.ProjectName).ThenBy(item => item.PatternId).ThenBy(item => item.ChannelId))
        {
            stackItems.Add(new MidiStackItem
            {
                StableId = item.StableId,
                MidiPath = item.MidiPath,
                ProjectPath = item.ProjectPath,
                ProjectName = item.ProjectName,
                PatternId = item.PatternId,
                PatternName = item.PatternName,
                ChannelId = item.ChannelId,
                ChannelName = item.ChannelName,
                StoredSamplePath = item.StoredSamplePath,
                AudioPath = item.AudioPath,
                Category = item.Category,
                Ppq = Math.Max(1, item.Ppq),
                TempoBpm = item.TempoBpm > 0 ? item.TempoBpm : 130,
                Notes = item.Notes ?? new List<MidiNoteEvent>()
            });
        }

        suppressStackFilters = true;
        var previous = StackCategoryCombo.SelectedItem?.ToString() ?? "All categories";
        StackCategoryCombo.Items.Clear();
        StackCategoryCombo.Items.Add("All categories");
        foreach (var category in stackItems.Select(item => item.Category).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(category => category)) StackCategoryCombo.Items.Add(category);
        StackCategoryCombo.SelectedItem = StackCategoryCombo.Items.Cast<string>().FirstOrDefault(category => category.Equals(previous, StringComparison.OrdinalIgnoreCase)) ?? "All categories";
        suppressStackFilters = false;
        stackView?.Refresh();
        StackNameText.Text = SafeDisplayName(appState.StackName, "My MIDI Stack");
        RefreshStackSummary();
    }

    private bool FilterStackItem(object value)
    {
        if (value is not MidiStackItem item) return false;
        var category = StackCategoryCombo?.SelectedItem?.ToString() ?? "All categories";
        return category == "All categories" || item.Category.Equals(category, StringComparison.OrdinalIgnoreCase);
    }

    private void StackFilter_Changed(object sender, EventArgs e)
    {
        if (suppressStackFilters) return;
        stackView?.Refresh();
        RefreshStackSummary();
    }

    private void RefreshExtractedMidi_Click(object sender, RoutedEventArgs e)
    {
        StopAllPlayback();
        var count = ExtractMidiPatternsFromAnalysis();
        LoadStack();
        StatusText.Text = projects.Any(row => row.Analysis is not null)
            ? $"Refreshed extracted MIDI · {count} sampler-aligned sequence{(count == 1 ? "" : "s")} from this analysis"
            : "Loaded saved extracted MIDI · analyze projects to discover more";
    }

    private void PersistStack()
    {
        appState.StackName = SafeDisplayName(StackNameText.Text, "My MIDI Stack");
        AppStateStore.Save(appState);
    }

    private void RefreshStackSummary()
    {
        var visible = stackView?.Cast<MidiStackItem>().ToArray() ?? stackItems.ToArray();
        var ready = visible.Count(item => File.Exists(item.MidiPath) && File.Exists(item.AudioPath));
        var midiOnly = visible.Count(item => File.Exists(item.MidiPath) && !File.Exists(item.AudioPath));
        var selectedCategory = StackCategoryCombo?.SelectedItem?.ToString() ?? "All categories";
        var categoryDescription = selectedCategory == "All categories" ? "category" : selectedCategory;
        StackSummaryText.Text = $"Extracted MIDI: {visible.Length} · Ready with matching sound: {ready} · Missing sound: {midiOnly}\nPreview plays every MIDI note through the matched {categoryDescription} sample: C5 keeps its original pitch, other keys transpose it, and each new hit cuts the previous one.";
    }

    private void PlayStack_Click(object sender, RoutedEventArgs e)
    {
        if (StackGrid.SelectedItem is not MidiStackItem item)
        {
            MessageBox.Show("Select an extracted MIDI row first.", "Nothing selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (currentMidiPlayingId == item.StableId)
        {
            StopAllPlayback();
            return;
        }
        PlayMidiStackItem(item);
    }

    private void MidiStackGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (StackGrid.SelectedItem is MidiStackItem item) PlayMidiStackItem(item);
    }

    private void PlayMidiStackItem(MidiStackItem item)
    {
        if (!File.Exists(item.MidiPath))
        {
            MessageBox.Show("The extracted MIDI file is missing. Re-analyze the source project to recreate it without overwriting anything.", "MIDI missing", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!File.Exists(item.AudioPath))
        {
            MessageBox.Show("The matching sampler sound is missing. Use Sample Rescue, then analyze again.", "Sound missing", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (item.Notes.Count == 0) return;

        try
        {
            StopAllPlayback();
            var sampler = MidiSamplerWaveProvider.FromFile(item.AudioPath, item.Notes, item.Ppq, item.TempoBpm);
            var output = new WaveOutEvent { DesiredLatency = 70, NumberOfBuffers = 3 };
            output.PlaybackStopped += StackAudioPlaybackStopped;
            output.Init(sampler);
            stackAudioOutput = output;
            currentMidiPlayingId = item.StableId;
            StackPlayButton.Content = "■ Stop MIDI";
            StackPlaybackText.Text = $"Playing {item.PatternName} through {item.MatchedSound} · pitched MIDI notes · FL-style self-cut · {item.TempoBpm:0.###} BPM";
            StatusText.Text = $"MIDI sampler preview · C5 is the sound's original pitch · each new note cuts the previous hit";
            output.Play();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or InvalidDataException or MmException or System.Runtime.InteropServices.COMException or NotSupportedException)
        {
            StopAllPlayback();
            MessageBox.Show(exception.Message, "MIDI preview failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void StackAudioPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!ReferenceEquals(sender, stackAudioOutput)) return;
            var failure = e.Exception;
            StopStackPlayers();
            ResetLibraryPlaybackUi();
            StatusText.Text = failure is null ? "MIDI sampler preview complete" : "MIDI sampler preview stopped: " + failure.Message;
        }));
    }

    private void StopStackPlayers()
    {
        var output = stackAudioOutput;
        stackAudioOutput = null;
        if (output is not null)
        {
            output.PlaybackStopped -= StackAudioPlaybackStopped;
            try { output.Stop(); }
            catch (MmException) { }
            output.Dispose();
        }
        currentMidiPlayingId = null;
    }

    private void ResetLibraryPlaybackUi()
    {
        currentVaultPlayingId = null;
        if (VaultPlayButton is not null) VaultPlayButton.Content = "▶ Play selected";
        if (VaultNowPlayingText is not null) VaultNowPlayingText.Text = "";
        if (StackPlayButton is not null) StackPlayButton.Content = "▶ Play selected MIDI";
        if (StackPlaybackText is not null) StackPlaybackText.Text = "";
    }

    private void OpenMidiLocation_Click(object sender, RoutedEventArgs e)
    {
        var path = StackGrid.SelectedItem is MidiStackItem selected && File.Exists(selected.MidiPath) ? selected.MidiPath : AppStateStore.MidiExtractionFolder;
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            MessageBox.Show("Analyze a project containing sampler-channel notes first.", "No extracted MIDI yet", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"") { UseShellExecute = true });
    }

    private void ExportStack_Click(object sender, RoutedEventArgs e)
    {
        var stackName = WindowsNames.Sanitize(StackNameText.Text.Trim());
        var selected = StackGrid.SelectedItems.Cast<MidiStackItem>().ToArray();
        var items = selected.Length > 0 ? selected : (stackView?.Cast<MidiStackItem>().ToArray() ?? stackItems.ToArray());
        if (string.IsNullOrWhiteSpace(stackName) || items.Length == 0)
        {
            MessageBox.Show("Give the MIDI collection a name and select or filter at least one extracted pattern.", "Nothing to export", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var root = Path.Combine(DefaultKitParent, "MIDI Stacks", stackName);
        try
        {
            Directory.CreateDirectory(root);
            var exports = new List<StackExportEntry>();
            foreach (var item in items.Where(item => File.Exists(item.MidiPath)))
            {
                var categoryFolder = Path.Combine(root, item.Category.Replace(" › ", Path.DirectorySeparatorChar.ToString()));
                Directory.CreateDirectory(categoryFolder);
                var midiDestination = CopyWithoutOverwrite(item.MidiPath, Path.Combine(categoryFolder, Path.GetFileName(item.MidiPath)));
                var soundDestination = File.Exists(item.AudioPath) ? CopyWithoutOverwrite(item.AudioPath, Path.Combine(categoryFolder, Path.GetFileName(item.AudioPath))) : "";
                exports.Add(new StackExportEntry
                {
                    StableId = item.StableId,
                    PatternName = item.PatternName,
                    ChannelName = item.ChannelName,
                    Category = item.Category,
                    MidiRelativePath = Path.GetRelativePath(root, midiDestination),
                    SoundRelativePath = string.IsNullOrWhiteSpace(soundDestination) ? "" : Path.GetRelativePath(root, soundDestination),
                    MidiSha256 = Hashing.Sha256(midiDestination),
                    SoundSha256 = string.IsNullOrWhiteSpace(soundDestination) ? "" : Hashing.Sha256(soundDestination)
                });
            }
            File.WriteAllText(Path.Combine(root, "midi-stack.json"), JsonSerializer.Serialize(exports, new JsonSerializerOptions { WriteIndented = true }));
            PersistStack();
            StackExportResultText.Text = $"Exported {exports.Count} MIDI pattern{(exports.Count == 1 ? "" : "s")} with matching sounds to:\n{root}";
            StatusText.Text = "MIDI Stack exported · source FLPs, MIDI, and sounds untouched";
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{root}\"") { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(exception.Message, "MIDI Stack export failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string CopyWithoutOverwrite(string sourcePath, string preferredDestination)
    {
        var sourceHash = Hashing.Sha256(sourcePath);
        if (File.Exists(preferredDestination) && string.Equals(Hashing.Sha256(preferredDestination), sourceHash, StringComparison.OrdinalIgnoreCase)) return preferredDestination;
        var folder = Path.GetDirectoryName(preferredDestination)!;
        var stem = Path.GetFileNameWithoutExtension(preferredDestination);
        var extension = Path.GetExtension(preferredDestination);
        var destination = preferredDestination;
        for (var suffix = 2; File.Exists(destination); suffix++)
        {
            destination = Path.Combine(folder, $"{stem} {suffix}{extension}");
            if (File.Exists(destination) && string.Equals(Hashing.Sha256(destination), sourceHash, StringComparison.OrdinalIgnoreCase)) return destination;
        }
        File.Copy(sourcePath, destination, false);
        return destination;
    }

    private void StackNameText_LostFocus(object sender, RoutedEventArgs e)
    {
        StackNameText.Text = SafeDisplayName(StackNameText.Text, "My MIDI Stack");
        PersistStack();
    }
}
