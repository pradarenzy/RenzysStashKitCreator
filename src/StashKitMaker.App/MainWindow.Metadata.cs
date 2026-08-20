using StashKitMaker.Core;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace StashKitMaker.App;

public partial class MainWindow
{
    private sealed class FinalSoundSource
    {
        public string StableId { get; init; } = "";
        public string GroupId { get; init; } = "";
        public string ManifestRelativePath { get; set; } = "";
        public string AudioPath { get; set; } = "";
    }

    private sealed record ManifestFolderSeed(string StableId, string RelativePath, string DefaultName, IReadOnlyList<string> Categories);

    private readonly ObservableCollection<FolderEditorViewModel> stagedFolders = new();
    private readonly List<KitFolderState> committedFolders = new();
    private readonly List<FinalSoundSource> finalSoundSources = new();
    private string stagedBaseColor = "#93977F";
    private string appliedBaseColor = "#93977F";
    private string appliedMetadataSignature = "";
    private bool suppressMetadataChanges;
    private bool applyingMetadata;
    private bool applyingIconIndexes;
    private bool propagatingIconIndex;
    private bool suppressIconIndexTextChange;
    private bool iconIndexTextPending;
    private bool flIconGlyphTypefaceSearched;
    private GlyphTypeface? flIconGlyphTypeface;

    private void InitializeFolderMetadata(string root, KitStateFile? savedState, IReadOnlyList<ManifestFolderSeed> seeds)
    {
        suppressMetadataChanges = true;
        foreach (var folder in stagedFolders) folder.PropertyChanged -= StagedFolder_PropertyChanged;
        stagedFolders.Clear();
        committedFolders.Clear();
        var saved = (savedState?.Folders ?? new()).Where(x => x is not null).ToDictionary(x => x.StableId, CloneFolder, StringComparer.OrdinalIgnoreCase);
        var rootState = saved.GetValueOrDefault("kit-root") ?? new KitFolderState { StableId = "kit-root", ParentId = null, DisplayName = Path.GetFileName(root), RelativePath = "" };
        rootState.ParentId = null;
        rootState.RelativePath = "";
        if (string.IsNullOrWhiteSpace(rootState.DisplayName)) rootState.DisplayName = Path.GetFileName(root);
        var folders = new List<KitFolderState> { rootState };
        var byRelative = new Dictionary<string, KitFolderState>(StringComparer.OrdinalIgnoreCase);
        foreach (var seed in seeds.DistinctBy(x => x.StableId, StringComparer.OrdinalIgnoreCase))
        {
            var state = saved.GetValueOrDefault(seed.StableId) ?? new KitFolderState { StableId = seed.StableId, ParentId = "", DisplayName = seed.DefaultName, RelativePath = seed.RelativePath };
            if (string.IsNullOrWhiteSpace(state.RelativePath)) state.RelativePath = seed.RelativePath;
            if (string.IsNullOrWhiteSpace(state.DisplayName)) state.DisplayName = seed.DefaultName;
            state.Categories = state.Categories.Concat(seed.Categories).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            folders.Add(state);
            byRelative[state.RelativePath] = state;
        }
        if (Directory.Exists(root))
        {
            foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Where(path => !IsMetadataPath(root, path)).OrderBy(path => path.Length))
            {
                var relative = Path.GetRelativePath(root, directory);
                if (byRelative.ContainsKey(relative)) continue;
                var state = saved.Values.FirstOrDefault(x => string.Equals(x.RelativePath, relative, StringComparison.OrdinalIgnoreCase)) ?? new KitFolderState { StableId = StableFolderId(relative), DisplayName = Path.GetFileName(directory), RelativePath = relative };
                folders.Add(state);
                byRelative[relative] = state;
            }
        }
        foreach (var folder in folders.Where(x => !string.Equals(x.StableId, "kit-root", StringComparison.OrdinalIgnoreCase)))
        {
            if (!string.IsNullOrWhiteSpace(folder.ParentId) && folders.Any(x => string.Equals(x.StableId, folder.ParentId, StringComparison.OrdinalIgnoreCase))) continue;
            var parentRelative = Path.GetDirectoryName(folder.RelativePath) ?? "";
            folder.ParentId = string.IsNullOrWhiteSpace(parentRelative) ? "kit-root" : byRelative.GetValueOrDefault(parentRelative)?.StableId ?? "kit-root";
        }
        foreach (var state in folders)
        {
            committedFolders.Add(CloneFolder(state));
            var editor = new FolderEditorViewModel { StableId = state.StableId, ParentId = state.ParentId, DisplayName = state.DisplayName, OriginalRelativePath = state.RelativePath, Categories = (state.Categories ?? new()).ToList(), IsRoot = state.ParentId is null, FlIconIndex = state.FlIconIndex, FlHeightOffset = state.FlHeightOffset, FlSortGroup = state.FlSortGroup, FlTip = state.FlTip };
            editor.PropertyChanged += StagedFolder_PropertyChanged;
            stagedFolders.Add(editor);
        }
        try
        {
            var recovery = FlStudioKitMetadata.RecoverInterruptedApply(root, ToStagedFolderRecords());
            if (recovery.FoldersRestored > 0 || recovery.SidecarsRestored > 0)
                StatusText.Text = $"Recovered an interrupted metadata update ({recovery.FoldersRestored} folders, {recovery.SidecarsRestored} sidecars).";
            if (recovery.RemainingStages.Count > 0)
                StatusText.Text = "An interrupted metadata backup was preserved because it could not be safely resolved.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            StatusText.Text = "Interrupted metadata recovery needs attention: " + ex.Message;
        }
        appliedBaseColor = FlStudioColors.IsWebHex(savedState?.BaseColor) ? FlStudioColors.NormalizeWebHex(savedState!.BaseColor) : "#93977F";
        stagedBaseColor = appliedBaseColor;
        var savedSignature = savedState?.AppliedSignature ?? "";
        var legacySignature = CalculateLegacyMetadataSignature(stagedBaseColor, stagedFolders);
        appliedMetadataSignature = savedSignature.Equals(legacySignature, StringComparison.Ordinal)
            ? CalculateColorMetadataSignature(stagedBaseColor, stagedFolders)
            : savedSignature;
        BaseColorHexText.Text = stagedBaseColor;
        FolderEditorCombo.ItemsSource = stagedFolders;
        FolderEditorCombo.SelectedItem = stagedFolders.FirstOrDefault();
        suppressMetadataChanges = false;
        RefreshMetadataPreview();
    }

    private void StagedFolder_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (suppressMetadataChanges || propagatingIconIndex) return;
        RefreshMetadataPreview();
        FolderEditorCombo.Items.Refresh();
    }

    private void RefreshMetadataPreview()
    {
        if (suppressMetadataChanges) return;
        if (!FlStudioColors.IsWebHex(stagedBaseColor))
        {
            MetadataApplyStateText.Text = "Not yet applied · enter #RRGGBB";
            MetadataApplyStateText.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            ApplyMetadataButton.IsEnabled = false;
            return;
        }
        var palette = FlStudioColors.CreatePalette(stagedBaseColor);
        SetPaletteRow(BaseSelectedSwatch, BaseSelectedHexText, BaseSelectedFlText, palette.Main);
        SetPaletteRow(MainFolderSwatch, MainFolderHexText, MainFolderFlText, palette.Main);
        SetPaletteRow(SubfolderSwatch, SubfolderHexText, SubfolderFlText, palette.Subfolder);
        SetPaletteRow(DeeperSwatch, DeeperHexText, DeeperFlText, palette.Deeper);
        try
        {
            var layout = KitFolderLayout.Calculate(ToStagedFolderRecords());
            foreach (var calculated in layout)
            {
                var editor = stagedFolders.First(x => x.StableId.Equals(calculated.StableId, StringComparison.OrdinalIgnoreCase));
                var color = FlStudioColors.Derive(stagedBaseColor, calculated.Depth);
                editor.Depth = calculated.Depth;
                editor.WebColor = color.WebHex;
                editor.FlColor = color.FlStudioBgr;
            }
            RenderFinalKitTree();
            var signature = CalculateColorMetadataSignature(stagedBaseColor, stagedFolders);
            var pending = string.IsNullOrWhiteSpace(appliedMetadataSignature) || !signature.Equals(appliedMetadataSignature, StringComparison.Ordinal);
            MetadataApplyStateText.Text = pending ? "Not yet applied" : "Applied";
            MetadataApplyStateText.SetResourceReference(TextBlock.ForegroundProperty, pending ? "Accent" : "SecondaryText");
            ApplyMetadataButton.IsEnabled = pending && !applyingMetadata;
        }
        catch (InvalidDataException ex)
        {
            MetadataApplyStateText.Text = "Not yet applied · " + ex.Message;
            MetadataApplyStateText.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            ApplyMetadataButton.IsEnabled = false;
        }
    }

    private void RenderFinalKitTree()
    {
        var layout = KitFolderLayout.Calculate(ToStagedFolderRecords());
        var root = layout.Single(x => x.ParentId is null);
        var groups = new Dictionary<string, FinalKitItemViewModel>(StringComparer.OrdinalIgnoreCase);
        finalKitGroups.Clear();
        foreach (var folder in layout.Where(x => x.ParentId is not null))
        {
            var color = FlStudioColors.Derive(stagedBaseColor, folder.Depth).WebHex;
            groups[folder.StableId] = new() { StableId = folder.StableId, ParentId = folder.ParentId ?? "", IsGroup = true, DisplayName = folder.Name, TextColor = color };
        }
        foreach (var folder in layout.Where(x => x.ParentId is not null).OrderBy(x => x.Depth).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            var group = groups[folder.StableId];
            if (string.Equals(folder.ParentId, root.StableId, StringComparison.OrdinalIgnoreCase) || !groups.TryGetValue(folder.ParentId!, out var parent)) finalKitGroups.Add(group); else parent.Children.Add(group);
        }
        foreach (var sound in finalSoundSources)
        {
            if (!groups.TryGetValue(sound.GroupId, out var group))
            {
                var directory = Path.GetDirectoryName(sound.ManifestRelativePath) ?? "";
                var match = layout.Where(x => x.ParentId is not null && IsRelativeWithin(directory, x.OldRelativePath)).OrderByDescending(x => x.OldRelativePath.Length).FirstOrDefault();
                if (match is null || !groups.TryGetValue(match.StableId, out group)) continue;
            }
            appState.KitEntries.TryGetValue(sound.StableId, out var entry);
            var depth = stagedFolders.First(x => x.StableId.Equals(group.StableId, StringComparison.OrdinalIgnoreCase)).Depth + 1;
            group.Children.Add(new() { StableId = sound.StableId, ParentId = group.StableId, DisplayName = SafeDisplayName(entry?.DisplayName, Path.GetFileNameWithoutExtension(sound.AudioPath)), TextColor = FlStudioColors.Derive(stagedBaseColor, depth).WebHex, AudioPath = sound.AudioPath });
        }
        FinalKitSummaryText.Text = $"{root.Name} · {groups.Count} folders · {finalSoundSources.Count} sounds";
    }

    private void BaseColorHexText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (suppressMetadataChanges) return;
        var value = BaseColorHexText.Text.Trim();
        stagedBaseColor = FlStudioColors.IsWebHex(value) ? FlStudioColors.NormalizeWebHex(value) : value;
        RefreshMetadataPreview();
    }

    private void PickBaseColor_Click(object sender, RoutedEventArgs e)
    {
        var initial = FlStudioColors.IsWebHex(stagedBaseColor) ? stagedBaseColor : appliedBaseColor;
        using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true, Color = System.Drawing.ColorTranslator.FromHtml(initial) };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        BaseColorHexText.Text = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
    }

    private void FolderEditorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FolderEditorCombo.SelectedItem is not FolderEditorViewModel selected) { FolderEditorPanel.DataContext = null; return; }
        FolderEditorPanel.DataContext = selected;
        suppressMetadataChanges = true;
        var options = stagedFolders.Where(folder => !folder.StableId.Equals(selected.StableId, StringComparison.OrdinalIgnoreCase) && !IsDescendant(folder.StableId, selected.StableId)).Select(folder => new FolderParentOption(folder.StableId, folder.Label)).ToList();
        FolderParentCombo.ItemsSource = options;
        FolderParentCombo.SelectedValue = selected.ParentId;
        FolderParentCombo.IsEnabled = !selected.IsRoot;
        suppressMetadataChanges = false;
        suppressIconIndexTextChange = true;
        FlIconIndexText.Text = selected.FlIconIndex.ToString(CultureInfo.InvariantCulture);
        suppressIconIndexTextChange = false;
        iconIndexTextPending = false;
        UpdateFlIconPreview(selected.FlIconIndex);
        RefreshIconApplyState();
        RefreshMetadataPreview();
    }

    private void FolderParentCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressMetadataChanges || FolderEditorCombo.SelectedItem is not FolderEditorViewModel selected || selected.IsRoot) return;
        if (FolderParentCombo.SelectedValue is string parentId) selected.ParentId = parentId;
    }

    private void FlIconIndexText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (suppressIconIndexTextChange) return;
        iconIndexTextPending = true;
        IconIndexStateText.Text = "Press Confirm to stage this index for every folder.";
        
        if (int.TryParse(FlIconIndexText.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= 0)
        {
            UpdateFlIconPreview(value);
        }
        else
        {
            ShowFlIconFallback("?", "Enter a whole number from 0 upward.");
        }

        ApplyIconIndexButton.IsEnabled = false;
    }

    private void ConfirmIconIndex_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(FlIconIndexText.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < 0)
        {
            IconIndexStateText.Text = "Enter a whole number from 0 upward.";
            return;
        }
        var installedTypeface = GetFlIconGlyphTypeface();
        if (installedTypeface is not null && !TryGetFlBrowserGlyph(installedTypeface, value, out _, out var codePoint))
        {
            IconIndexStateText.Text = $"IconIndex {value} is not provided by the installed FL Browser font (expected U+{codePoint:X4}).";
            ApplyIconIndexButton.IsEnabled = false;
            return;
        }

        propagatingIconIndex = true;
        foreach (var folder in stagedFolders) folder.FlIconIndex = value;
        propagatingIconIndex = false;

        iconIndexTextPending = false;
        StatusText.Text = $"IconIndex {value} confirmed for every folder.";
        UpdateFlIconPreview(value);
        RefreshIconApplyState();
        RefreshMetadataPreview();
    }

    private void RefreshIconApplyState()
    {
        if (iconIndexTextPending)
        {
            ApplyIconIndexButton.IsEnabled = false;
            return;
        }
        if (string.IsNullOrWhiteSpace(loadedFinalKitRoot) || stagedFolders.Count == 0)
        {
            ApplyIconIndexButton.IsEnabled = false;
            IconIndexStateText.Text = "Choose a generated kit first.";
            return;
        }
        var confirmed = stagedFolders[0].FlIconIndex;
        var installedTypeface = GetFlIconGlyphTypeface();
        if (installedTypeface is not null && !TryGetFlBrowserGlyph(installedTypeface, confirmed, out _, out var expectedCodePoint))
        {
            ApplyIconIndexButton.IsEnabled = false;
            IconIndexStateText.Text = $"IconIndex {confirmed} maps to missing glyph U+{expectedCodePoint:X4}; FL Studio substitutes its fallback icon. Choose a number with a visible preview.";
            return;
        }
        try
        {
            var layout = KitFolderLayout.Calculate(ToStagedFolderRecords());
            var appliedEverywhere = layout.All(folder =>
            {
                var target = folder.ParentId is null ? loadedFinalKitRoot : Path.Combine(loadedFinalKitRoot, folder.OldRelativePath);
                return FlStudioNfo.TryReadIconIndex(target, out var actual) && actual == folder.IconIndex;
            });
            IconIndexStateText.Text = appliedEverywhere
                ? $"Applied to all {layout.Count} folders: {confirmed}"
                : $"Confirmed for all {layout.Count} folders: {confirmed} · not yet written everywhere";
            ApplyIconIndexButton.IsEnabled = !appliedEverywhere && !applyingIconIndexes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ApplyIconIndexButton.IsEnabled = false;
            IconIndexStateText.Text = "Icon status unavailable: " + ex.Message;
        }
    }

    private async void ApplyIconIndex_Click(object sender, RoutedEventArgs e)
    {
        if (applyingIconIndexes || iconIndexTextPending || string.IsNullOrWhiteSpace(loadedFinalKitRoot)) return;
        IReadOnlyList<StagedKitFolder> folders;
        try { folders = ToStagedFolderRecords(); _ = KitFolderLayout.Calculate(folders); }
        catch (InvalidDataException ex) { IconIndexStateText.Text = ex.Message; return; }

        applyingIconIndexes = true;
        ApplyIconIndexButton.IsEnabled = false;
        ApplyIconIndexButton.Content = "Applying icons…";
        IconIndexStateText.Text = "Writing IconIndex to every folder…";
        try
        {
            var result = await Task.Run(() => FlStudioKitMetadata.ApplyIconIndexes(loadedFinalKitRoot, appliedBaseColor, folders));
            var stagedById = stagedFolders.ToDictionary(x => x.StableId, StringComparer.OrdinalIgnoreCase);
            foreach (var committed in committedFolders)
                if (stagedById.TryGetValue(committed.StableId, out var staged)) committed.FlIconIndex = staged.FlIconIndex;
            SaveFinalKitState();
            StatusText.Text = $"IconIndex applied and verified for all {result.FolderCount} folders.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            IconIndexStateText.Text = "Icons were not applied: " + ex.Message;
            StatusText.Text = "IconIndex was not applied. " + ex.Message;
        }
        finally
        {
            applyingIconIndexes = false;
            ApplyIconIndexButton.Content = "Apply icons to kit";
            RefreshIconApplyState();
        }
    }

    private void UpdateFlIconPreview(int iconIndex)
    {
        var glyphTypeface = GetFlIconGlyphTypeface();
        if (glyphTypeface is null)
        {
            ShowFlIconFallback(iconIndex.ToString(CultureInfo.InvariantCulture), "The installed FL Browser font was not found, so this IconIndex cannot be previewed locally.");
            return;
        }
        if (!TryGetFlBrowserGlyph(glyphTypeface, iconIndex, out var glyphIndex, out var codePoint))
        {
            ShowFlIconFallback("?", $"FL Browser IconIndex {iconIndex} maps to U+{codePoint:X4}, which is not present in the installed ILGlyphsEx font. Choose an index with a visible preview.");
            return;
        }
        try
        {
            var geometry = glyphTypeface.GetGlyphOutline(glyphIndex, 26, 26).Clone();
            var bounds = geometry.Bounds;
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) { ShowFlIconFallback(iconIndex.ToString(CultureInfo.InvariantCulture), "No drawable glyph was found for this index."); return; }
            var scale = Math.Min(24d / bounds.Width, 24d / bounds.Height);
            var transform = new TransformGroup();
            transform.Children.Add(new TranslateTransform(-bounds.X, -bounds.Y));
            transform.Children.Add(new ScaleTransform(scale, scale));
            transform.Children.Add(new TranslateTransform((28d - bounds.Width * scale) / 2d, (28d - bounds.Height * scale) / 2d));
            geometry.Transform = transform;
            var image = new DrawingImage(new GeometryDrawing(new SolidColorBrush(System.Windows.Media.Color.FromRgb(69, 65, 66)), null, geometry));
            image.Freeze();
            FlIconPreviewImage.Source = image;
            FlIconPreviewImage.Visibility = Visibility.Visible;
            FlIconPreviewFallback.Visibility = Visibility.Collapsed;
            FlIconPreviewBorder.ToolTip = $"FL Browser IconIndex {iconIndex} · ILGlyphsEx U+{codePoint:X4}";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            ShowFlIconFallback(iconIndex.ToString(CultureInfo.InvariantCulture), "Preview unavailable; FL Studio will render the native icon.");
        }
    }

    private static bool TryGetFlBrowserGlyph(GlyphTypeface glyphTypeface, int iconIndex, out ushort glyphIndex, out int codePoint)
    {
        glyphIndex = 0;
        try { codePoint = FlStudioBrowserIcons.CodePointForIndex(iconIndex); }
        catch (OverflowException) { codePoint = int.MaxValue; return false; }
        return glyphTypeface.CharacterToGlyphMap.TryGetValue(codePoint, out glyphIndex) && glyphIndex != 0;
    }

    private void ShowFlIconFallback(string text, string toolTip)
    {
        FlIconPreviewImage.Source = null;
        FlIconPreviewImage.Visibility = Visibility.Collapsed;
        FlIconPreviewFallback.Text = text;
        FlIconPreviewFallback.Visibility = Visibility.Visible;
        FlIconPreviewBorder.ToolTip = toolTip;
    }

    private GlyphTypeface? GetFlIconGlyphTypeface()
    {
        if (flIconGlyphTypefaceSearched) return flIconGlyphTypeface;
        flIconGlyphTypefaceSearched = true;
        var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var imageLineRoot in roots.Select(path => Path.Combine(path, "Image-Line")).Where(Directory.Exists))
        {
            try
            {
                foreach (var flStudioFolder in Directory.EnumerateDirectories(imageLineRoot, "FL Studio*", SearchOption.TopDirectoryOnly).OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    var fontPath = Path.Combine(flStudioFolder, "Help", "fonts", "ILGlyphsEx.ttf");
                    if (!File.Exists(fontPath)) continue;
                    flIconGlyphTypeface = new GlyphTypeface(new Uri(fontPath, UriKind.Absolute));
                    return flIconGlyphTypeface;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
        return null;
    }

    private async void ApplyMetadata_Click(object sender, RoutedEventArgs e)
    {
        if (applyingMetadata || string.IsNullOrWhiteSpace(loadedFinalKitRoot)) return;
        System.Windows.Input.Keyboard.ClearFocus();
        if (FindVisualChildren<DependencyObject>(FolderEditorPanel).Any(Validation.GetHasError)) { MetadataApplyStateText.Text = "Not yet applied · correct the numeric folder fields."; return; }
        if (!FlStudioColors.IsWebHex(stagedBaseColor)) { MetadataApplyStateText.Text = "Not yet applied · enter exactly #RRGGBB."; return; }
        
        IReadOnlyList<StagedKitFolder> folders;
        try { folders = ToColorFolderRecords(); _ = KitFolderLayout.Calculate(folders); }
        catch (InvalidDataException ex) { MetadataApplyStateText.Text = "Not yet applied · " + ex.Message; return; }
        
        applyingMetadata = true;
        ApplyMetadataButton.IsEnabled = false;
        ApplyMetadataButton.Content = "Applying…";
        MetadataApplyStateText.Text = "Applying FL Studio metadata…";
        var oldRoot = loadedFinalKitRoot;
        string? applyFailure = null;
        
        try
        {
            var result = await Task.Run(() => FlStudioKitMetadata.Apply(oldRoot, stagedBaseColor, folders, preserveExistingIconIndexes: true));
            foreach (var calculated in result.Folders)
            {
                var editor = stagedFolders.First(x => x.StableId.Equals(calculated.StableId, StringComparison.OrdinalIgnoreCase));
                editor.OriginalRelativePath = calculated.NewRelativePath;
            }
            UpdateRootReferencesAfterMetadataApply(oldRoot, result.RootPath);
            appliedBaseColor = FlStudioColors.NormalizeWebHex(stagedBaseColor);
            appliedMetadataSignature = CalculateColorMetadataSignature(appliedBaseColor, stagedFolders);
            committedFolders.Clear();
            committedFolders.AddRange(stagedFolders.Select(ToFolderState));
            foreach (var committed in committedFolders)
            {
                var calculated = result.Folders.First(x => x.StableId.Equals(committed.StableId, StringComparison.OrdinalIgnoreCase));
                var target = calculated.ParentId is null ? result.RootPath : Path.Combine(result.RootPath, calculated.NewRelativePath);
                if (FlStudioNfo.TryReadIconIndex(target, out var appliedIcon)) committed.FlIconIndex = appliedIcon;
            }
            loadedFinalKitRoot = result.RootPath;
            selectedFinalKitRoot = result.RootPath;
            SaveFinalKitState();
            finalKitDirty = true;
            finalKitChoicesDirty = true;
            reviewDirty = true;
            LoadFinalizedKit(result.RootPath);
            MetadataApplyStateText.Text = "FL Studio metadata added. Refresh or re-index the FL Studio Browser.";
            StatusText.Text = "FL Studio metadata added. Open the exported kit in the FL Studio Browser.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            applyFailure = $"Folder in use by Windows or FL Studio. Close it and try again. ({ex.Message})";
        }
        finally
        {
            applyingMetadata = false;
            ApplyMetadataButton.Content = "Apply colour & folders";
            RefreshMetadataPreview();
            if (applyFailure is not null)
            {
                MetadataApplyStateText.Text = "Not yet applied · " + applyFailure;
                MetadataApplyStateText.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
                StatusText.Text = "FL Studio metadata was not applied: " + applyFailure;
            }
        }
    }

    private void UpdateRootReferencesAfterMetadataApply(string oldRoot, string newRoot)
    {
        foreach (var history in appState.History.Where(x => string.Equals(x.KitPath, oldRoot, StringComparison.OrdinalIgnoreCase))) history.KitPath = newRoot;
        foreach (var processed in appState.ProcessedSounds.Values.Where(x => string.Equals(x.KitPath, oldRoot, StringComparison.OrdinalIgnoreCase))) processed.KitPath = newRoot;
        if (string.Equals(Path.Combine(DefaultKitParent, DefaultKitName), oldRoot, StringComparison.OrdinalIgnoreCase)) { appState.KitParent = Path.GetDirectoryName(newRoot) ?? appState.KitParent; appState.KitName = Path.GetFileName(newRoot); KitParentText.Text = appState.KitParent; KitNameText.Text = appState.KitName; }
        AppStateStore.Save(appState);
        RefreshHistory();
        IndexManifest(newRoot);
    }

    private IReadOnlyList<StagedKitFolder> ToStagedFolderRecords() => stagedFolders.Select(x => new StagedKitFolder(x.StableId, x.ParentId, x.DisplayName, x.OriginalRelativePath, x.FlIconIndex, x.FlHeightOffset, x.FlSortGroup, x.FlTip)).ToArray();
    private IReadOnlyList<StagedKitFolder> ToColorFolderRecords()
    {
        var appliedIcons = committedFolders.ToDictionary(x => x.StableId, x => x.FlIconIndex, StringComparer.OrdinalIgnoreCase);
        return stagedFolders.Select(x => new StagedKitFolder(x.StableId, x.ParentId, x.DisplayName, x.OriginalRelativePath, appliedIcons.GetValueOrDefault(x.StableId, x.FlIconIndex), x.FlHeightOffset, x.FlSortGroup, x.FlTip)).ToArray();
    }
    private static KitFolderState ToFolderState(FolderEditorViewModel x) => new() { StableId = x.StableId, ParentId = x.ParentId, DisplayName = x.DisplayName, RelativePath = x.OriginalRelativePath, Categories = x.Categories.ToList(), FlIconIndex = x.FlIconIndex, FlHeightOffset = x.FlHeightOffset, FlSortGroup = x.FlSortGroup, FlTip = FlStudioNfo.SanitizeTip(x.FlTip) };
    private static KitFolderState CloneFolder(KitFolderState x) => new() { StableId = x.StableId, ParentId = x.ParentId, DisplayName = x.DisplayName, RelativePath = x.RelativePath, Categories = (x.Categories ?? new()).ToList(), FlIconIndex = x.FlIconIndex, FlHeightOffset = x.FlHeightOffset, FlSortGroup = x.FlSortGroup, FlTip = x.FlTip };
    private static string CalculateColorMetadataSignature(string baseColor, IEnumerable<FolderEditorViewModel> folders) => CalculateColorMetadataSignature(baseColor, folders.Select(ToFolderState));
    private static string CalculateColorMetadataSignature(string baseColor, IEnumerable<KitFolderState> folders)
    {
        var value = FlStudioColors.NormalizeWebHex(baseColor) + "\n" + string.Join("\n", folders.OrderBy(x => x.StableId, StringComparer.OrdinalIgnoreCase).Select(x => $"{x.StableId}|{x.ParentId}|{x.DisplayName}|{x.FlHeightOffset}|{x.FlSortGroup}|{FlStudioNfo.SanitizeTip(x.FlTip)}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }
    private static string CalculateLegacyMetadataSignature(string baseColor, IEnumerable<FolderEditorViewModel> folders)
    {
        var value = FlStudioColors.NormalizeWebHex(baseColor) + "\n" + string.Join("\n", folders.Select(ToFolderState).OrderBy(x => x.StableId, StringComparer.OrdinalIgnoreCase).Select(x => $"{x.StableId}|{x.ParentId}|{x.DisplayName}|{x.FlIconIndex}|{x.FlHeightOffset}|{x.FlSortGroup}|{FlStudioNfo.SanitizeTip(x.FlTip)}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }
    private static void SetPaletteRow(Border swatch, TextBlock web, TextBlock fl, HierarchyColor color) { swatch.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color.WebHex)); web.Text = color.WebHex; fl.Text = color.FlStudioBgr; }
    private bool IsDescendant(string candidateId, string ancestorId) { var current = stagedFolders.FirstOrDefault(x => x.StableId.Equals(candidateId, StringComparison.OrdinalIgnoreCase)); var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase); while (current?.ParentId is { } parent && visited.Add(parent)) { if (parent.Equals(ancestorId, StringComparison.OrdinalIgnoreCase)) return true; current = stagedFolders.FirstOrDefault(x => x.StableId.Equals(parent, StringComparison.OrdinalIgnoreCase)); } return false; }
    private static bool IsMetadataPath(string root, string path) => Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).FirstOrDefault()?.Equals("_metadata", StringComparison.OrdinalIgnoreCase) == true;
    private static string StableFolderId(string relative) => "folder:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(relative.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar).ToLowerInvariant())))[..16].ToLowerInvariant();
    private static bool IsRelativeWithin(string path, string folder) => path.Equals(folder, StringComparison.OrdinalIgnoreCase) || path.StartsWith(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private IReadOnlyList<KitFolderState> AppliedFoldersFor(string root)
    {
        if (string.Equals(root, loadedFinalKitRoot, StringComparison.OrdinalIgnoreCase) && committedFolders.Count > 0) return committedFolders;
        return ReadKitStateFileRaw(root)?.Folders ?? new List<KitFolderState>();
    }
    private string CategoryRelativePath(string root, SampleCategory category)
    {
        var key = category.ToString();
        var folder = AppliedFoldersFor(root).FirstOrDefault(item => (item.Categories ?? new()).Contains(key, StringComparer.OrdinalIgnoreCase))
            ?? AppliedFoldersFor(root).FirstOrDefault(item => item.StableId.Equals("group:" + key, StringComparison.OrdinalIgnoreCase));
        return folder is not null && !string.IsNullOrWhiteSpace(folder.RelativePath) ? folder.RelativePath : CategoryFolders.For(category);
    }
    private string CategoryFolderStableId(string root, SampleCategory category)
    {
        var key = category.ToString();
        return AppliedFoldersFor(root).FirstOrDefault(item => (item.Categories ?? new()).Contains(key, StringComparer.OrdinalIgnoreCase))?.StableId ?? "group:" + key;
    }
}
