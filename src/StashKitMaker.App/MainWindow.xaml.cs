using Microsoft.Win32;
using StashKitMaker.Core;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;
using Microsoft.VisualBasic.FileIO;
using System.Globalization;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;

namespace StashKitMaker.App;
public partial class MainWindow : Window
{
    private sealed class ProjectRow { public string Path { get; init; } = ""; public string Name => System.IO.Path.GetFileNameWithoutExtension(Path); public string Status { get; set; } = "Pending"; public int Samples { get; set; } public int Warnings => WarningMessages.Count; public List<string> WarningMessages { get; set; } = new(); public ProjectAnalysis? Analysis { get; set; } }
    private sealed class ReviewItem { public string Path { get; init; } = ""; public string StableId { get; init; } = ""; public string Name => System.IO.Path.GetFileName(Path); public string DisplayName { get; init; } = ""; public string DisplayLabel => DisplayName+System.IO.Path.GetExtension(Path); public string UnicodeIcon { get; init; } = ""; public string TextColor { get; init; } = "#454142"; public string SizeLabel => $"{new System.IO.FileInfo(Path).Length / 1024d:0.#} KB"; }
    private sealed class KitSelectionItem { public string Path { get; init; } = ""; public string Label { get; init; } = ""; }
    private readonly ObservableCollection<ProjectRow> projects = new();
    private readonly ObservableCollection<ReviewItem> unknownItems = new();
    private readonly ObservableCollection<FinalKitItemViewModel> finalKitGroups = new();
    private readonly AppState appState;
    private readonly System.Windows.Media.MediaPlayer reviewPlayer = new();
    private bool playbackPaused;
    private string? currentFinalPlayingId;
    private string? loadedFinalKitRoot;
    private string? selectedFinalKitRoot;
    private string reviewSoundIcon = "";
    private string reviewSoundTextColor = "#454142";
    private bool suppressFinalKitSelection;
    private bool finalKitDirty = true;
    private bool finalKitChoicesDirty = true;
    private bool reviewDirty = true;
    private bool startupPagesPreloaded;
    private string stagedUiAccentColor = UiPalette.DefaultAccent;
    private bool suppressUiAccentPreview;
    private readonly List<string> sampleLibraryRoots = new();
    private BuildPlan? pendingBuild;
    private int pendingSkippedDuplicates;
    private string DefaultKitParent => appState.KitParent;
    private string DefaultKitName => appState.KitName;
    private string currentSection = "Projects";
    public MainWindow()
    {
        appState = AppStateStore.Load();
        stagedUiAccentColor = UiPalette.NormalizeOrDefault(appState.UiAccentColor);
        UiPalette.Apply(stagedUiAccentColor);
        InitializeComponent();
        SourceInitialized += (_, _) => WindowsShellIdentity.ApplyWindowAppearance(this);
        StateChanged += (_, _) => MaximizeGlyph.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        ContentRendered += (_, _) => FinishStartupVisualWarmup();
        ProjectsGrid.ItemsSource = projects;
        UnknownGrid.ItemsSource = unknownItems;
        FinalKitTree.ItemsSource = finalKitGroups;
        ConfigureLibraryTools();
        reviewPlayer.MediaEnded += (s, e) => Dispatcher.Invoke(ResetPlaybackUi);
        reviewPlayer.MediaFailed += (s, e) => Dispatcher.Invoke(() => { ResetPlaybackUi(); StatusText.Text = "Audio could not be played."; });
        KitParentText.Text = appState.KitParent;
        KitNameText.Text = appState.KitName;
        MergeKitCheck.IsChecked = appState.MergeGeneratedKit;
        suppressUiAccentPreview = true;
        UiAccentText.Text = stagedUiAccentColor;
        suppressUiAccentPreview = false;
        IndexManifest(System.IO.Path.Combine(appState.KitParent, appState.KitName));
        PreloadStartupPages();
    }

    private void PreloadStartupPages()
    {
        if (startupPagesPreloaded) return;
        LoadReview();
        ShowFinalizedKit();
        LoadVault();
        LoadStack();
        RefreshRecovery();
        RefreshHistory();
        startupPagesPreloaded = true;
        currentSection = "Projects";
        PageTitle.Text = "Projects";
        PageSubtitle.Text = "Choose FL Studio projects, then start a read-only analysis.";
        ProjectsPage.Visibility = Visibility.Visible;
        // Hidden pages still take part in the first measure pass. This generates the
        // large kit/review visual trees during startup instead of on first navigation.
        ReviewPage.Visibility = Visibility.Hidden;
        BuildKitPage.Visibility = Visibility.Hidden;
        VaultPage.Visibility = Visibility.Hidden;
        StacksPage.Visibility = Visibility.Hidden;
        RecoveryPage.Visibility = Visibility.Hidden;
        SettingsPage.Visibility = Visibility.Collapsed;
        HistoryPage.Visibility = Visibility.Collapsed;
        SectionPage.Visibility = Visibility.Collapsed;
        StatusText.Text = "Ready · Offline · No telemetry";
    }

    private void FinishStartupVisualWarmup()
    {
        if (currentSection != "Projects") return;
        ReviewPage.Visibility = Visibility.Collapsed;
        BuildKitPage.Visibility = Visibility.Collapsed;
        VaultPage.Visibility = Visibility.Collapsed;
        StacksPage.Visibility = Visibility.Collapsed;
        RecoveryPage.Visibility = Visibility.Collapsed;
    }
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if(e.ClickCount==2){ToggleMaximize();return;}if(e.LeftButton==MouseButtonState.Pressed)try{DragMove();}catch(InvalidOperationException){} }
    private void MinimizeWindow_Click(object sender,RoutedEventArgs e)=>WindowState=WindowState.Minimized;
    private void MaximizeWindow_Click(object sender,RoutedEventArgs e)=>ToggleMaximize();
    private void CloseWindow_Click(object sender,RoutedEventArgs e)=>Close();
    private void ToggleMaximize()=>WindowState=WindowState==WindowState.Maximized?WindowState.Normal:WindowState.Maximized;
    private void Navigate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button || button.Tag is not string section) return;
        StopAllPlayback();
        currentSection = section;
        foreach (var nav in FindVisualChildren<System.Windows.Controls.Button>(this).Where(x => x.Tag is string))
            nav.Style = (Style)System.Windows.Application.Current.FindResource(typeof(System.Windows.Controls.Button));
        button.Style = (Style)FindResource("AccentButton");
        ProjectsPage.Visibility = section == "Projects" ? Visibility.Visible : Visibility.Collapsed;
        VaultPage.Visibility = section == "Vault" ? Visibility.Visible : Visibility.Collapsed;
        StacksPage.Visibility = section == "Stacks" ? Visibility.Visible : Visibility.Collapsed;
        RecoveryPage.Visibility = section == "Recovery" ? Visibility.Visible : Visibility.Collapsed;
        ReviewPage.Visibility = section == "Review" ? Visibility.Visible : Visibility.Collapsed;
        BuildKitPage.Visibility=section=="Build Kit"?Visibility.Visible:Visibility.Collapsed;
        SettingsPage.Visibility = section == "Settings" ? Visibility.Visible : Visibility.Collapsed; HistoryPage.Visibility = section == "History" ? Visibility.Visible : Visibility.Collapsed;
        SectionPage.Visibility = Visibility.Collapsed;
        if (section == "Vault") { PageTitle.Text = "Sound Vault"; PageSubtitle.Text = "Search, preview, filter, and drag sounds from every generated kit."; LoadVault(); }
        else if (section == "Stacks") { PageTitle.Text = "Stacks"; PageSubtitle.Text = "Extract sampler-channel MIDI, keep it aligned by sound category, and preview it through the matched sample."; LoadStack(); }
        else if (section == "Recovery") { PageTitle.Text = "Sample Rescue"; PageSubtitle.Text = "Find moved samples or save exact replacements for the next verified build."; RefreshRecovery(); }
        else if (section == "Review") ShowReview(); else if(section=="Build Kit")ShowFinalizedKit(); else if (section == "Settings") { PageTitle.Text = "Settings"; PageSubtitle.Text = "Choose where kits are created and whether to merge into an existing generated kit."; } else if (section == "History") { PageTitle.Text = "History"; PageSubtitle.Text = "Open previous kits and inspect duplicate-import activity."; RefreshHistory(); } else ConfigureSection(section);
        StatusText.Text = $"{section} · Offline · No telemetry";
    }
    private void ConfigureSection(string section)
    {
        PageTitle.Text = section; SectionResult.Text = ""; SectionAction.Visibility = Visibility.Visible;
        (PageSubtitle.Text, SectionHeading.Text, SectionBody.Text, SectionAction.Content, SectionState.Text) = section switch
        {
            "Sample Library" => ("Index locations that may contain samples referenced by your projects.", "Sample library locations", "Choose a sample folder to inspect. This action only counts supported audio files; it does not rename, move, or edit anything.", "Inspect Sample Folder", "No sample-library folder has been inspected in this session."),
            "Drumkits" => ("Inspect your existing kit organization without modifying it.", "Read-only Drumkits scan", "Choose your Drumkits root to measure recurring folder names and organization patterns. Every operation on this screen is read-only.", "Scan Drumkits Folder", "No Drumkits folder has been scanned in this session."),
            "Analysis" => ("View the current project-analysis totals.", "Analysis overview", "Analysis runs from the Projects screen. This page reflects the projects currently loaded in this session.", "Go to Projects", $"Projects loaded: {projects.Count}\nAnalyzed: {projects.Count(x => x.Status != "Pending")}\nPending: {projects.Count(x => x.Status == "Pending")}"),
            _ => ("", "", "", "", "")
        };
    }
    private void ShowReview()
    {
        PageTitle.Text = "Review";
        PageSubtitle.Text = "Sort Unknown files in the generated kit into their correct folders.";
        if (reviewDirty) LoadReview();
    }
    private void LoadReview()
    {
        PageTitle.Text = "Review"; PageSubtitle.Text = "Sort Unknown files in the generated kit into their correct folders."; unknownItems.Clear(); var root = System.IO.Path.Combine(DefaultKitParent, DefaultKitName);var presentation=LoadKitStateFile(root);reviewSoundIcon=ResolveUniformIcon(presentation);reviewSoundTextColor=ResolveUniformColor(presentation);var unknown = System.IO.Path.Combine(root, CategoryRelativePath(root,SampleCategory.Unknown));var ids=ReadManifestIdsByPath(root);
        if (System.IO.Directory.Exists(unknown)) foreach (var file in System.IO.Directory.EnumerateFiles(unknown).OrderBy(System.IO.Path.GetFileName)){var relative=System.IO.Path.GetRelativePath(root,file);var id=ids.TryGetValue(relative,out var manifestId)?manifestId:Hashing.Sha256(file);appState.KitEntries.TryGetValue(id,out var state);unknownItems.Add(new() { Path = file,StableId=id,DisplayName=SafeDisplayName(state?.DisplayName,System.IO.Path.GetFileNameWithoutExtension(file)),UnicodeIcon=reviewSoundIcon,TextColor=reviewSoundTextColor });}
        var folders = System.IO.Directory.Exists(root) ? System.IO.Directory.EnumerateDirectories(root).Where(x => System.IO.Path.GetFileName(x) != "_metadata").Select(x => $"{System.IO.Path.GetFileName(x)}: {System.IO.Directory.EnumerateFiles(x).Count()}").ToArray() : Array.Empty<string>();
        ReviewSummaryText.Text = $"Kit: {root}\nUnknown files remaining: {unknownItems.Count}\n\n" + (folders.Length > 0 ? string.Join("  •  ", folders) : "No built kit was found.");RefreshReviewCategoryLabels();reviewDirty=false;
    }
    private Dictionary<string,string> ReadManifestIdsByPath(string root)
    {
        var result=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);var path=System.IO.Path.Combine(root,"_metadata","manifest.json");if(!System.IO.File.Exists(path))return result;try{using var document=JsonDocument.Parse(System.IO.File.ReadAllText(path));foreach(var item in document.RootElement.EnumerateArray()){if(!item.TryGetProperty("Hash",out var hashElement)||!item.TryGetProperty("RelativePath",out var relativeElement))continue;var hash=hashElement.GetString();var relative=relativeElement.GetString();if(!string.IsNullOrWhiteSpace(hash)&&!string.IsNullOrWhiteSpace(relative))result[relative]=hash;}}catch(Exception ex)when(ex is JsonException or System.IO.IOException or UnauthorizedAccessException){StatusText.Text="Review is using safe display defaults because the manifest could not be read.";}return result;
    }
    private void RefreshReviewCategoryLabels()
    {
        foreach(var button in FindVisualChildren<System.Windows.Controls.Button>(ReviewPage).Where(x=>x.Tag is string))
        {
            if(button.Tag is not string value||!Enum.TryParse<SampleCategory>(value,out var category))continue;button.Content=CategoryFolders.For(category);button.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty,"PrimaryText");
        }
    }
    private void ShowFinalizedKit()
    {
        PageTitle.Text="Build Kit";PageSubtitle.Text="Preview the final kit, then apply real colours, folder names, hierarchy, and native icons to the FL Studio Browser.";var configuredRoot=System.IO.Path.Combine(DefaultKitParent,DefaultKitName);var preferred=selectedFinalKitRoot??configuredRoot;if(finalKitChoicesDirty)RefreshFinalKitChoices(preferred);var root=selectedFinalKitRoot??configuredRoot;
        if(finalKitDirty||!string.Equals(root,loadedFinalKitRoot,StringComparison.OrdinalIgnoreCase))LoadFinalizedKit(root);
    }
    private void RefreshFinalKitChoices(string preferredRoot)
    {
        var configuredRoot=System.IO.Path.Combine(DefaultKitParent,DefaultKitName);var paths=appState.History.OrderByDescending(x=>x.CreatedUtc).Select(x=>x.KitPath).Concat(DiscoverGeneratedKits()).Prepend(configuredRoot).Append(preferredRoot).Where(path=>!string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).Where(path=>System.IO.Directory.Exists(path)&&GeneratedKitSafety.IsGeneratedKit(path)).ToList();if(paths.Count==0)paths.Add(configuredRoot);var choices=paths.Select(path=>new KitSelectionItem{Path=path,Label=$"{System.IO.Path.GetFileName(path)}  —  {(System.IO.Directory.Exists(path)?System.IO.Path.GetDirectoryName(path):"not built yet")}"}).ToList();var selected=choices.FirstOrDefault(x=>string.Equals(x.Path,preferredRoot,StringComparison.OrdinalIgnoreCase))??choices[0];suppressFinalKitSelection=true;FinalKitCombo.ItemsSource=choices;FinalKitCombo.SelectedItem=selected;suppressFinalKitSelection=false;selectedFinalKitRoot=selected.Path;finalKitChoicesDirty=false;
    }
    private string[] DiscoverGeneratedKits(){try{return System.IO.Directory.Exists(DefaultKitParent)?System.IO.Directory.EnumerateDirectories(DefaultKitParent).Where(GeneratedKitSafety.IsGeneratedKit).ToArray():Array.Empty<string>();}catch(Exception ex)when(ex is System.IO.IOException or UnauthorizedAccessException){StatusText.Text="Some kit folders could not be listed.";return Array.Empty<string>();}}
    private void FinalKitCombo_SelectionChanged(object sender,System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if(suppressFinalKitSelection||FinalKitCombo.SelectedItem is not KitSelectionItem selection)return;selectedFinalKitRoot=selection.Path;finalKitDirty=true;LoadFinalizedKit(selection.Path);
    }
    private void LoadFinalizedKit(string root)
    {
        StopAllPlayback();
        finalKitGroups.Clear();
        finalSoundSources.Clear();
        loadedFinalKitRoot=root;
        finalKitDirty=false;
        var savedState=LoadKitStateFile(root);
        var manifestPath=System.IO.Path.Combine(root,"_metadata","manifest.json");
        if(!System.IO.File.Exists(manifestPath))
        {
            FinalKitSummaryText.Text=$"No finalised kit found at {root}. Build a kit from Projects first.";
            ApplyMetadataButton.IsEnabled=false;
            return;
        }
        try
        {
            using var document=JsonDocument.Parse(System.IO.File.ReadAllText(manifestPath));
            var seedBuilders=new Dictionary<string,(string StableId,string DefaultName,List<string> Categories)>(StringComparer.OrdinalIgnoreCase);
            foreach(var item in document.RootElement.EnumerateArray())
            {
                if(!item.TryGetProperty("Hash",out var hashElement)||!item.TryGetProperty("RelativePath",out var relativeElement))continue;
                var hash=hashElement.GetString();
                var relative=relativeElement.GetString();
                if(string.IsNullOrWhiteSpace(hash)||string.IsNullOrWhiteSpace(relative))continue;
                var category=item.TryGetProperty("Category",out var categoryElement)?categoryElement.GetString()??"Unknown":"Unknown";
                var folderRelative=System.IO.Path.GetDirectoryName(relative)??"";
                if(string.IsNullOrWhiteSpace(folderRelative)&&Enum.TryParse<SampleCategory>(category,out var parsedFallback))folderRelative=CategoryFolders.For(parsedFallback);
                if(!seedBuilders.TryGetValue(folderRelative,out var seed))
                {
                    var savedFolder=(savedState?.Folders??new()).FirstOrDefault(folder=>string.Equals(folder.RelativePath,folderRelative,StringComparison.OrdinalIgnoreCase)||(folder.Categories??new()).Contains(category,StringComparer.OrdinalIgnoreCase));
                    var id=savedFolder?.StableId??"group:"+category;
                    if(seedBuilders.Values.Any(existing=>existing.StableId.Equals(id,StringComparison.OrdinalIgnoreCase)))id=StableFolderId(folderRelative);
                    seed=(id,System.IO.Path.GetFileName(folderRelative),new List<string>());
                }
                if(!seed.Categories.Contains(category,StringComparer.OrdinalIgnoreCase))seed.Categories.Add(category);
                seedBuilders[folderRelative]=seed;
                var groupId=seed.StableId;
                var audioPath=System.IO.Path.Combine(root,relative);
                if(!appState.KitEntries.TryGetValue(hash,out var entryState)){entryState=new(){StableId=hash};appState.KitEntries[hash]=entryState;}
                entryState.StableId=hash;
                entryState.DisplayName=SafeDisplayName(entryState.DisplayName,System.IO.Path.GetFileNameWithoutExtension(relative));
                entryState.AudioSource=audioPath;
                entryState.ParentId=groupId;
                finalSoundSources.Add(new(){StableId=hash,GroupId=groupId,ManifestRelativePath=relative,AudioPath=audioPath});
            }
            var seeds=seedBuilders.Select(pair=>new ManifestFolderSeed(pair.Value.StableId,pair.Key,string.IsNullOrWhiteSpace(pair.Value.DefaultName)?"Sounds":pair.Value.DefaultName,pair.Value.Categories)).ToArray();
            InitializeFolderMetadata(root,savedState,seeds);
        }
        catch(Exception ex)when(ex is JsonException or System.IO.IOException or UnauthorizedAccessException){FinalKitSummaryText.Text="The finalised kit could not be loaded: "+ex.Message;}
    }
    private KitStateFile? ReadKitStateFileRaw(string root)
    {
        var path=System.IO.Path.Combine(root,"_metadata","kit-state.json");
        if(!System.IO.File.Exists(path))return null;
        try{return JsonSerializer.Deserialize<KitStateFile>(System.IO.File.ReadAllText(path));}
        catch(Exception ex)when(ex is JsonException or System.IO.IOException or UnauthorizedAccessException){StatusText.Text="Older kit presentation state was unreadable; safe defaults were used.";return null;}
    }
    private KitStateFile? LoadKitStateFile(string root)
    {
        var state=ReadKitStateFileRaw(root);if(state is null)return null;foreach(var group in state.Groups??new())if(group is not null&&!string.IsNullOrWhiteSpace(group.StableId))appState.KitGroups[group.StableId]=group;foreach(var entry in state.Entries??new())if(entry is not null&&!string.IsNullOrWhiteSpace(entry.StableId))appState.KitEntries[entry.StableId]=entry;return state;
    }
    private static string ResolveUniformIcon(KitStateFile? state){if(state is null)return "";if(state.Version>=2)return FirstTextElement(state.SoundUnicodeIcon??"");return FirstTextElement((state.Entries??new()).Where(x=>x is not null).Select(x=>x.UnicodeIcon??"").GroupBy(x=>x).OrderByDescending(x=>x.Count()).Select(x=>x.Key).FirstOrDefault()??"");}
    private static string ResolveUniformColor(KitStateFile? state){if(state is null)return "#454142";var candidate=state.Version>=2?state.SoundTextColor:(state.Entries??new()).Where(x=>x is not null).Select(x=>x.TextColor).GroupBy(x=>x,StringComparer.OrdinalIgnoreCase).OrderByDescending(x=>x.Count()).Select(x=>x.Key).FirstOrDefault();return SafeTextColor(candidate,"#454142");}
    private void SaveFinalKitState()
    {
        try{AppStateStore.Save(appState);if(!string.IsNullOrWhiteSpace(loadedFinalKitRoot))SaveKitStateFile(loadedFinalKitRoot);}catch(Exception ex)when(ex is JsonException or System.IO.IOException or UnauthorizedAccessException){StatusText.Text="The edit is visible, but its presentation settings could not be saved: "+ex.Message;}
    }
    private void SaveKitStateFile(string root,string? soundIcon=null,string? soundTextColor=null)
    {
        if(!System.IO.Directory.Exists(root))return;var manifestPath=System.IO.Path.Combine(root,"_metadata","manifest.json");if(!System.IO.File.Exists(manifestPath))return;var existing=ReadKitStateFileRaw(root);using var document=JsonDocument.Parse(System.IO.File.ReadAllText(manifestPath));var entries=new List<KitEntryState>();var groupIds=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var item in document.RootElement.EnumerateArray())
        {
            if(!item.TryGetProperty("Hash",out var hashElement))continue;var hash=hashElement.GetString();if(string.IsNullOrWhiteSpace(hash))continue;if(appState.KitEntries.TryGetValue(hash,out var entry)){entries.Add(entry);if(!string.IsNullOrWhiteSpace(entry.ParentId))groupIds.Add(entry.ParentId);}
        }
        var isLoaded=string.Equals(root,loadedFinalKitRoot,StringComparison.OrdinalIgnoreCase);
        var state=new KitStateFile{Version=3,SoundUnicodeIcon=soundIcon??existing?.SoundUnicodeIcon??"",SoundTextColor=SafeTextColor(soundTextColor??existing?.SoundTextColor,"#454142"),BaseColor=isLoaded?appliedBaseColor:existing?.BaseColor??"#93977F",AppliedSignature=isLoaded?appliedMetadataSignature:existing?.AppliedSignature??"",Folders=isLoaded&&committedFolders.Count>0?committedFolders.Select(CloneFolder).ToList():(existing?.Folders??new()).Select(CloneFolder).ToList(),Groups=groupIds.Where(appState.KitGroups.ContainsKey).Select(id=>appState.KitGroups[id]).ToList(),Entries=entries};var metadata=System.IO.Path.Combine(root,"_metadata");System.IO.Directory.CreateDirectory(metadata);var path=System.IO.Path.Combine(metadata,"kit-state.json");var temp=path+".tmp";System.IO.File.WriteAllText(temp,JsonSerializer.Serialize(state,new JsonSerializerOptions{WriteIndented=true}));System.IO.File.Move(temp,path,true);
    }
    private IEnumerable<FinalKitItemViewModel> EnumerateFinalItems(){foreach(var root in finalKitGroups)foreach(var item in EnumerateFinalItems(root))yield return item;}
    private static IEnumerable<FinalKitItemViewModel> EnumerateFinalItems(FinalKitItemViewModel item){yield return item;foreach(var child in item.Children)foreach(var nested in EnumerateFinalItems(child))yield return nested;}
    private static string SafeDisplayName(string? value,string fallback)=>string.IsNullOrWhiteSpace(value)?fallback:value.Trim();
    private static string SafeTextColor(string? value,string fallback){try{_ = System.Windows.Media.ColorConverter.ConvertFromString(value);return value??fallback;}catch{return fallback;}}
    private void RefreshFinalKit_Click(object sender,RoutedEventArgs e){var preferred=selectedFinalKitRoot??System.IO.Path.Combine(DefaultKitParent,DefaultKitName);RefreshFinalKitChoices(preferred);finalKitDirty=true;LoadFinalizedKit(selectedFinalKitRoot??preferred);}
    private void OpenFinalKitFolder_Click(object sender,RoutedEventArgs e){var root=loadedFinalKitRoot??selectedFinalKitRoot??System.IO.Path.Combine(DefaultKitParent,DefaultKitName);if(!System.IO.Directory.Exists(root)){System.Windows.MessageBox.Show("The selected kit folder does not exist.","Kit unavailable",MessageBoxButton.OK,MessageBoxImage.Information);return;}Process.Start(new ProcessStartInfo("explorer.exe",$"\"{root}\""){UseShellExecute=true});}
    private void FinalPlayStop_Click(object sender,RoutedEventArgs e)
    {
        if(sender is not System.Windows.Controls.Button{DataContext:FinalKitItemViewModel item})return;if(currentFinalPlayingId==item.StableId){StopAllPlayback();return;}StopAllPlayback();if(!System.IO.File.Exists(item.AudioPath)){StatusText.Text=$"Missing audio: {item.DisplayName}";System.Windows.MessageBox.Show("This audio file is missing or has moved.","Cannot play sound",MessageBoxButton.OK,MessageBoxImage.Warning);return;}
        try{reviewPlayer.Open(new Uri(item.AudioPath,UriKind.Absolute));reviewPlayer.Play();currentFinalPlayingId=item.StableId;item.PlayGlyph="■";StatusText.Text="Playing: "+item.DisplayName;}catch(Exception ex)when(ex is UriFormatException or InvalidOperationException){StopAllPlayback();StatusText.Text="Could not play: "+ex.Message;}
    }
    private void FinalName_GotFocus(object sender,RoutedEventArgs e){if(sender is System.Windows.Controls.TextBox box)box.Tag=box.Text;}
    private void FinalName_KeyDown(object sender,System.Windows.Input.KeyEventArgs e)
    {
        if(sender is not System.Windows.Controls.TextBox box)return;if(e.Key==System.Windows.Input.Key.Escape){box.Text=box.Tag?.ToString()??(box.DataContext as FinalKitItemViewModel)?.DisplayName??"";System.Windows.Input.Keyboard.ClearFocus();e.Handled=true;}else if(e.Key==System.Windows.Input.Key.Enter){CommitFinalName(box);box.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.Next));e.Handled=true;}
    }
    private void FinalName_LostFocus(object sender,RoutedEventArgs e){if(sender is System.Windows.Controls.TextBox box)CommitFinalName(box);}
    private void CommitFinalName(System.Windows.Controls.TextBox box)
    {
        if(box.DataContext is not FinalKitItemViewModel item)return;var previous=SafeDisplayName(box.Tag?.ToString(),item.IsGroup?"Untitled Group":"Untitled Sound");var requested=SafeDisplayName(box.Text,previous);if(item.IsGroup){item.DisplayName=requested;box.Text=requested;var state=appState.KitGroups[item.StableId];state.DisplayName=requested;SynchronizeFinalItem(item.StableId,x=>x.DisplayName=requested);SaveFinalKitState();return;}
        requested=WindowsNames.Sanitize(requested);if(string.IsNullOrWhiteSpace(requested))requested=previous;if(string.Equals(requested,item.DisplayName,StringComparison.Ordinal)){box.Text=item.DisplayName;return;}var source=item.AudioPath;if(!System.IO.File.Exists(source)){box.Text=item.DisplayName;StatusText.Text="Rename stopped because the audio file is missing.";return;}var destination=System.IO.Path.Combine(System.IO.Path.GetDirectoryName(source)!,requested+System.IO.Path.GetExtension(source));if(System.IO.File.Exists(destination)&&!string.Equals(source,destination,StringComparison.OrdinalIgnoreCase)){box.Text=item.DisplayName;System.Windows.MessageBox.Show("Another sound already uses that filename.","Name collision",MessageBoxButton.OK,MessageBoxImage.Warning);return;}
        try{StopAllPlayback();if(!string.Equals(source,destination,StringComparison.OrdinalIgnoreCase))System.IO.File.Move(source,destination);UpdateManifestEntry(item.StableId,destination);var state=appState.KitEntries[item.StableId];state.DisplayName=requested;state.AudioSource=destination;if(appState.ProcessedSounds.TryGetValue(item.StableId,out var processed)){processed.OutputName=System.IO.Path.GetFileName(destination);processed.KitPath=loadedFinalKitRoot??processed.KitPath;}var sourceState=finalSoundSources.FirstOrDefault(x=>x.StableId.Equals(item.StableId,StringComparison.OrdinalIgnoreCase));if(sourceState is not null){sourceState.AudioPath=destination;if(!string.IsNullOrWhiteSpace(loadedFinalKitRoot))sourceState.ManifestRelativePath=System.IO.Path.GetRelativePath(loadedFinalKitRoot,destination);}SynchronizeFinalItem(item.StableId,x=>{x.DisplayName=requested;x.AudioPath=destination;});box.Text=requested;SaveFinalKitState();finalKitDirty=false;reviewDirty=true;}
        catch(Exception ex)when(ex is System.IO.IOException or UnauthorizedAccessException){box.Text=item.DisplayName;System.Windows.MessageBox.Show(ex.Message,"Sound could not be renamed",MessageBoxButton.OK,MessageBoxImage.Warning);}
    }
    private static string FirstTextElement(string value){if(string.IsNullOrEmpty(value))return "";var enumerator=StringInfo.GetTextElementEnumerator(value);return enumerator.MoveNext()?enumerator.GetTextElement():"";}
    private void SynchronizeFinalItem(string id,Action<FinalKitItemViewModel> update){foreach(var match in EnumerateFinalItems().Where(x=>x.StableId.Equals(id,StringComparison.OrdinalIgnoreCase)))update(match);}
    private void UpdateManifestEntry(string stableId,string newAudioPath)
    {
        if(string.IsNullOrWhiteSpace(loadedFinalKitRoot))return;var manifestPath=System.IO.Path.Combine(loadedFinalKitRoot,"_metadata","manifest.json");if(!System.IO.File.Exists(manifestPath))return;var array=JsonNode.Parse(System.IO.File.ReadAllText(manifestPath))?.AsArray();if(array is null)return;foreach(var node in array){if(node is JsonObject obj&&string.Equals(obj["Hash"]?.GetValue<string>(),stableId,StringComparison.OrdinalIgnoreCase)){obj["RelativePath"]=System.IO.Path.GetRelativePath(loadedFinalKitRoot,newAudioPath);obj["StableId"]=stableId;break;}}System.IO.File.WriteAllText(manifestPath,array.ToJsonString(new JsonSerializerOptions{WriteIndented=true}));
    }
    private void PlaySelected_Click(object sender, RoutedEventArgs e) { if (UnknownGrid.SelectedItem is not ReviewItem item) { System.Windows.MessageBox.Show("Select an Unknown file to play.", "Nothing selected", MessageBoxButton.OK, MessageBoxImage.Information); return; } PlayReviewItem(item); }
    private void UnknownGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) { if (UnknownGrid.SelectedItem is ReviewItem item) PlayReviewItem(item); }
    private void PlayReviewItem(ReviewItem item) { try { StopAllPlayback(); reviewPlayer.Open(new Uri(item.Path, UriKind.Absolute)); reviewPlayer.Play(); playbackPaused = false; PauseResumeButton.Content = "⏸ Pause"; PauseResumeButton.IsEnabled = true; NowPlayingText.Text = "Playing: " + item.Name; } catch (Exception ex) when (ex is UriFormatException or InvalidOperationException) { ResetPlaybackUi(); NowPlayingText.Text = "Could not play: " + ex.Message; } }
    private void PauseResume_Click(object sender, RoutedEventArgs e) { if (!PauseResumeButton.IsEnabled) return; if (playbackPaused) { reviewPlayer.Play(); playbackPaused = false; PauseResumeButton.Content = "⏸ Pause"; NowPlayingText.Text = NowPlayingText.Text.Replace("Paused:", "Playing:"); } else { reviewPlayer.Pause(); playbackPaused = true; PauseResumeButton.Content = "▶ Resume"; NowPlayingText.Text = NowPlayingText.Text.Replace("Playing:", "Paused:"); } }
    private void StopPlayback_Click(object sender, RoutedEventArgs e) => StopAllPlayback();
    private void StopAllPlayback(){reviewPlayer.Stop();StopStackPlayers();ResetPlaybackUi();}
    private void ResetPlaybackUi() { playbackPaused = false; PauseResumeButton.IsEnabled = false; PauseResumeButton.Content = "⏸ Pause"; NowPlayingText.Text = "";currentFinalPlayingId=null;foreach(var item in EnumerateFinalItems().Where(x=>!x.IsGroup))item.PlayGlyph="▶";ResetLibraryPlaybackUi(); }
    protected override void OnClosed(EventArgs e) { StopStackPlayers(); reviewPlayer.Close(); base.OnClosed(e); }
    private void BrowseKitParent_Click(object sender, RoutedEventArgs e) { using var d = new FolderBrowserDialog { Description = "Choose the parent folder for generated kits", UseDescriptionForTitle = true }; if (d.ShowDialog() == System.Windows.Forms.DialogResult.OK) KitParentText.Text = d.SelectedPath; }
    private void ChooseExistingKit_Click(object sender, RoutedEventArgs e) { using var d = new FolderBrowserDialog { Description = "Choose an existing app-generated kit", UseDescriptionForTitle = true }; if (d.ShowDialog() != System.Windows.Forms.DialogResult.OK) return; var marker = System.IO.Path.Combine(d.SelectedPath, "_metadata", ".stashkitmaker"); if (!System.IO.File.Exists(marker)) { SettingsResult.Text = "That folder is not recognized as an app-generated kit, so it will not be merged."; return; } KitParentText.Text = System.IO.Path.GetDirectoryName(d.SelectedPath) ?? ""; KitNameText.Text = System.IO.Path.GetFileName(d.SelectedPath); MergeKitCheck.IsChecked = true; SettingsResult.Text = "Generated kit recognized. Save settings to enable hash-safe merging."; IndexManifest(d.SelectedPath); }
    private void UiAccentText_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (suppressUiAccentPreview) return;
        if (!UiPalette.TryNormalize(UiAccentText.Text, out var normalized)) { UiAccentStatus.Text = "Enter a six-digit colour such as #A66F83."; return; }
        stagedUiAccentColor = normalized;
        UiPalette.Apply(normalized);
        UiAccentStatus.Text = "Previewing this muted colour scheme. Save settings to keep it.";
    }
    private void PickUiAccent_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new ColorDialog { FullOpen = true, Color = UiPalette.ToDrawingColor(stagedUiAccentColor) };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        UiAccentText.Text = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
    }
    private void ResetUiAccent_Click(object sender, RoutedEventArgs e) => UiAccentText.Text = UiPalette.DefaultAccent;
    private void SaveSettings_Click(object sender, RoutedEventArgs e) { var parent = KitParentText.Text.Trim(); var name = WindowsNames.Sanitize(KitNameText.Text.Trim()); if (!System.IO.Directory.Exists(parent)) { SettingsResult.Text = "Choose an existing parent folder."; return; } if (string.IsNullOrWhiteSpace(name)) { SettingsResult.Text = "Enter a valid kit name."; return; }if(!UiPalette.TryNormalize(UiAccentText.Text,out var uiAccent)){SettingsResult.Text="Enter a valid six-digit interface colour.";return;}StopAllPlayback(); appState.KitParent = parent; appState.KitName = name; appState.MergeGeneratedKit = MergeKitCheck.IsChecked == true;appState.UiAccentColor=uiAccent;stagedUiAccentColor=uiAccent;UiPalette.Apply(uiAccent); AppStateStore.Save(appState); pendingBuild = null; BuildButton.IsEnabled = false;finalKitDirty=true;finalKitChoicesDirty=true;reviewDirty=true;UiAccentStatus.Text="Saved. This colour scheme will be restored at startup."; SettingsResult.Text = $"Saved. New destination:\n{System.IO.Path.Combine(parent, name)}"; }
    private void RefreshHistory_Click(object sender, RoutedEventArgs e) => RefreshHistory();
    private void RefreshHistory() { HistoryCombo.ItemsSource = null; HistoryCombo.ItemsSource = appState.History.OrderByDescending(x => x.CreatedUtc).ToList(); if (HistoryCombo.Items.Count > 0) HistoryCombo.SelectedIndex = 0; HistoryDetails.Text = $"Recorded builds: {appState.History.Count}\nProcessed unique hashes: {appState.ProcessedSounds.Count}"; }
    private void HistoryCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) { if (HistoryCombo.SelectedItem is BuildHistoryEntry entry) HistoryDetails.Text = $"Kit: {entry.KitPath}\nCreated: {entry.CreatedUtc.ToLocalTime():f}\nAdded: {entry.Added}\nDuplicates skipped: {entry.SkippedDuplicates}\nAvailable: {System.IO.Directory.Exists(entry.KitPath)}"; }
    private void OpenHistoryKit_Click(object sender, RoutedEventArgs e) { if (HistoryCombo.SelectedItem is not BuildHistoryEntry entry || !System.IO.Directory.Exists(entry.KitPath)) { System.Windows.MessageBox.Show("That kit folder is no longer available.", "Kit unavailable", MessageBoxButton.OK, MessageBoxImage.Information); return; } Process.Start(new ProcessStartInfo("explorer.exe", $"\"{entry.KitPath}\"") { UseShellExecute = true }); }
    private void DeleteHistoryKit_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryCombo.SelectedItem is not BuildHistoryEntry entry) { System.Windows.MessageBox.Show("Select a kit from History first.", "Nothing selected", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (!GeneratedKitSafety.IsGeneratedKit(entry.KitPath)) { System.Windows.MessageBox.Show("Deletion refused: this folder is missing the app's generated-kit marker. Nothing was changed.", "Safety check failed", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var answer = System.Windows.MessageBox.Show($"Move this entire generated kit to the Windows Recycle Bin?\n\n{entry.KitPath}\n\nYou may restore it from the Recycle Bin afterward.", "Delete generated kit", MessageBoxButton.YesNo, MessageBoxImage.Warning); if (answer != MessageBoxResult.Yes) return;
        try { StopAllPlayback(); FileSystem.DeleteDirectory(entry.KitPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException); appState.History.RemoveAll(x => string.Equals(x.KitPath, entry.KitPath, StringComparison.OrdinalIgnoreCase)); RebuildProcessedIndex(); AppStateStore.Save(appState); RefreshHistory(); if (string.Equals(System.IO.Path.Combine(DefaultKitParent, DefaultKitName), entry.KitPath, StringComparison.OrdinalIgnoreCase)) { unknownItems.Clear(); pendingBuild = null; BuildButton.IsEnabled = false; reviewDirty=true; }if(string.Equals(selectedFinalKitRoot,entry.KitPath,StringComparison.OrdinalIgnoreCase)){selectedFinalKitRoot=null;loadedFinalKitRoot=null;finalKitGroups.Clear();}finalKitDirty=true;finalKitChoicesDirty=true; StatusText.Text = "Kit moved to Recycle Bin"; System.Windows.MessageBox.Show("The kit was moved to the Recycle Bin and removed from History.", "Kit deleted", MessageBoxButton.OK, MessageBoxImage.Information); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or OperationCanceledException) { System.Windows.MessageBox.Show(ex.Message, "Kit could not be recycled", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void RebuildProcessedIndex() { appState.ProcessedSounds.Clear(); var paths = appState.History.Select(x => x.KitPath).Append(System.IO.Path.Combine(DefaultKitParent, DefaultKitName)).Where(System.IO.Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(); foreach (var path in paths) IndexManifest(path); }
    private void IndexManifest(string kitPath) { var manifest = System.IO.Path.Combine(kitPath, "_metadata", "manifest.json"); if (!System.IO.File.Exists(manifest)) return; try { using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(manifest)); foreach (var item in doc.RootElement.EnumerateArray()) { if (!item.TryGetProperty("Hash", out var h) || !item.TryGetProperty("RelativePath", out var p)) continue; var hash = h.GetString(); var relative = p.GetString(); if (string.IsNullOrWhiteSpace(hash) || string.IsNullOrWhiteSpace(relative)) continue; var category = item.TryGetProperty("Category", out var c) ? c.GetString() ?? "Unknown" : "Unknown"; appState.ProcessedSounds[hash] = new() { Hash = hash, OutputName = System.IO.Path.GetFileName(relative), Category = category, KitPath = kitPath }; } AppStateStore.Save(appState); } catch (JsonException) { SettingsResult.Text = "The kit was recognized, but its manifest could not be indexed."; } }
    private void ReviewCategory_Click(object sender, RoutedEventArgs e)
    {
        if (UnknownGrid.SelectedItem is not ReviewItem item) { System.Windows.MessageBox.Show("Select an Unknown file first.", "Nothing selected", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (sender is not System.Windows.Controls.Button button || button.Tag is not string value || !Enum.TryParse<SampleCategory>(value, out var category)) return;
        try
        {
            reviewPlayer.Stop(); ResetPlaybackUi(); var root=System.IO.Path.Combine(DefaultKitParent,DefaultKitName);var hash = Hashing.Sha256(item.Path); var classification = new Classification(category, null, 1, new[] { "manual review override" }); var generator = new SweetNameGenerator(); var relativeFolder=CategoryRelativePath(root,category);var folder = System.IO.Path.Combine(root,relativeFolder); System.IO.Directory.CreateDirectory(folder); var salt = 0; string destination; do { var name = System.IO.Path.ChangeExtension(generator.Generate(hash, classification, salt++), System.IO.Path.GetExtension(item.Path)); destination = System.IO.Path.Combine(folder, name); } while (System.IO.File.Exists(destination));
            System.IO.File.Move(item.Path, destination); UpdateManifestAfterReview(item.Path, destination, category); appState.ProcessedSounds[hash] = new() { Hash = hash, OutputName = System.IO.Path.GetFileName(destination), Category = category.ToString(), KitPath = root };if(!appState.KitEntries.TryGetValue(hash,out var entry)){entry=new(){StableId=hash};appState.KitEntries[hash]=entry;}entry.DisplayName=System.IO.Path.GetFileNameWithoutExtension(destination);entry.AudioSource=destination;entry.ParentId=CategoryFolderStableId(root,category);entry.UnicodeIcon=reviewSoundIcon;entry.TextColor=reviewSoundTextColor;var groupId=entry.ParentId;if(!appState.KitGroups.ContainsKey(groupId))appState.KitGroups[groupId]=new(){StableId=groupId,DisplayName=System.IO.Path.GetFileName(relativeFolder),TextColor="#C04375"};AppStateStore.Save(appState);SaveKitStateFile(root,reviewSoundIcon,reviewSoundTextColor);finalKitDirty=true; LoadReview(); StatusText.Text = $"Moved to {System.IO.Path.GetFileName(relativeFolder)} · manifest updated";
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or JsonException) { System.Windows.MessageBox.Show(ex.Message, "Could not sort file", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void UpdateManifestAfterReview(string oldPath, string newPath, SampleCategory category)
    {
        var root = System.IO.Path.Combine(DefaultKitParent, DefaultKitName); var manifestPath = System.IO.Path.Combine(root, "_metadata", "manifest.json"); if (!System.IO.File.Exists(manifestPath)) return; var array = JsonNode.Parse(System.IO.File.ReadAllText(manifestPath))?.AsArray(); if (array is null) return; var oldRelative = System.IO.Path.GetRelativePath(root, oldPath);
        foreach (var node in array) { if (node is JsonObject obj && string.Equals(obj["RelativePath"]?.GetValue<string>(), oldRelative, StringComparison.OrdinalIgnoreCase)) { obj["RelativePath"] = System.IO.Path.GetRelativePath(root, newPath); obj["Category"] = category.ToString(); obj["Confidence"] = 1; break; } }
        System.IO.File.WriteAllText(manifestPath, array.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
    private void SectionAction_Click(object sender, RoutedEventArgs e)
    {
        if (currentSection is not ("Sample Library" or "Drumkits")) { NavigateToProjects(); return; }
        using var dialog = new FolderBrowserDialog { Description = currentSection == "Drumkits" ? "Choose your Drumkits folder (read-only scan)" : "Choose a sample-library folder (read-only scan)", UseDescriptionForTitle = true };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        try
        {
            if (currentSection == "Drumkits")
            {
                var profile = new DrumkitStructureAnalyzer().ScanReadOnly(dialog.SelectedPath);
                SectionResult.Text = profile.Count == 0 ? "No category folders found." : "Most common folders:\n" + string.Join("\n", profile.Take(12).Select(x => $"{x.Key}: {x.Value}"));
                SectionState.Text = $"Scanned read-only:\n{dialog.SelectedPath}\n\nFolder patterns found: {profile.Count}";
            }
            else
            {
                var extensions = new HashSet<string>(new[] { ".wav", ".mp3", ".flac", ".ogg", ".aif", ".aiff" }, StringComparer.OrdinalIgnoreCase);
                if (!sampleLibraryRoots.Contains(dialog.SelectedPath, StringComparer.OrdinalIgnoreCase)) sampleLibraryRoots.Add(dialog.SelectedPath);
                var count = System.IO.Directory.EnumerateFiles(dialog.SelectedPath, "*", System.IO.SearchOption.AllDirectories).Count(x => extensions.Contains(System.IO.Path.GetExtension(x)));
                SectionResult.Text = $"Supported audio files discovered: {count:N0}"; SectionState.Text = $"Inspected read-only:\n{dialog.SelectedPath}";
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { SectionResult.Text = "Scan could not complete: " + ex.Message; }
    }
    private void NavigateToProjects() { ProjectsNav.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); }
    private void NavigateToBuildKit(){BuildKitNav.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));}
    private static IEnumerable<T> FindVisualChildren<T>(System.Windows.DependencyObject root) where T : System.Windows.DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++) { var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i); if (child is T match) yield return match; foreach (var nested in FindVisualChildren<T>(child)) yield return nested; }
    }
    private void AddFiles_Click(object sender, RoutedEventArgs e) { var d = new Microsoft.Win32.OpenFileDialog { Filter = "FL Studio projects (*.flp)|*.flp", Multiselect = true }; if (d.ShowDialog() == true) Add(d.FileNames); }
    private void AddFolder_Click(object sender, RoutedEventArgs e) { using var d = new FolderBrowserDialog { Description = "Choose a folder containing FLP projects", UseDescriptionForTitle = true }; if (d.ShowDialog() == System.Windows.Forms.DialogResult.OK) Add(System.IO.Directory.EnumerateFiles(d.SelectedPath, "*.flp", System.IO.SearchOption.AllDirectories)); }
    private void Add(IEnumerable<string> paths) { foreach (var p in paths.Select(System.IO.Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)) if (!projects.Any(x => x.Path.Equals(p, StringComparison.OrdinalIgnoreCase))) projects.Add(new() { Path = p }); RefreshSummary(); }
    private void Remove_Click(object sender, RoutedEventArgs e) { foreach (ProjectRow p in ProjectsGrid.SelectedItems.Cast<ProjectRow>().ToArray()) projects.Remove(p); RefreshSummary(); }
    private void Clear_Click(object sender, RoutedEventArgs e) { projects.Clear(); RefreshSummary(); }
    private void ProjectsGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ProjectsGrid.SelectedItem is not ProjectRow row) return;
        WarningDetailsText.Text = row.WarningMessages.Count == 0
            ? $"{row.Name}: all clear — no warnings recorded."
            : $"{row.Name}\n\n" + string.Join("\n\n", row.WarningMessages.Select((message, index) => $"{index + 1}. {FriendlyWarning(message)}"));
    }
    private async void Analyze_Click(object sender, RoutedEventArgs e)
    {
        AnalyzeButton.IsEnabled = false; AddFilesButton.IsEnabled = false; StatusText.Text = "Parsing projects read-only…";
        try
        {
            var parser = new EventStreamFlpParser(); var ok = 0; var partial = 0; var failed = 0; var refs = 0;
            foreach (var row in projects) { var a = await Task.Run(() => parser.ParseProject(row.Path)); row.Analysis = a; row.Status = a.Status.ToString(); row.Samples = a.SampleReferences.Count; row.WarningMessages = a.Warnings.ToList(); refs += row.Samples; if (a.Status == ParseStatus.Success) ok++; else if (a.Status == ParseStatus.Partial) partial++; else failed++; ProjectsGrid.Items.Refresh(); }
            var extractedMidi = ExtractMidiPatternsFromAnalysis(); LoadStack();
            var planResult = await Task.Run(CreateBuildPlan); pendingBuild = planResult.Plan; pendingSkippedDuplicates = planResult.AlreadyProcessed; BuildButton.IsEnabled = pendingBuild?.Files.Count > 0; BuildPreviewText.Text = $"Destination:\n{System.IO.Path.Combine(DefaultKitParent, DefaultKitName)}\n\nNew unique samples: {pendingBuild?.Files.Count ?? 0}\nAlready imported (skipped): {planResult.AlreadyProcessed}\nExcluded loops/songs/tags: {planResult.Excluded}\nMissing references: {planResult.Missing}\nAmbiguous references: {planResult.Ambiguous}\nMode: {(appState.MergeGeneratedKit ? "merge generated kit" : "create new kit")}\n\n{(BuildButton.IsEnabled ? "Ready for explicit approval." : "No new resolved samples are available to build.")}";
            SummaryText.Text = $"Projects scanned: {projects.Count}\nSuccessful: {ok}\nPartial: {partial}\nFailed: {failed}\n\nSample references found: {refs}\nSampler-aligned MIDI extracted: {extractedMidi}\n\nNo kit destination files have been created. Extracted MIDI was saved in the app cache without modifying the projects."; var warned = projects.Where(x => x.Warnings > 0).ToList(); WarningDetailsText.Text = warned.Count == 0 ? "All clear — no warnings recorded." : $"{warned.Sum(x => x.Warnings)} warning(s) across {warned.Count} project(s).\n\nSelect a project row to see exactly what happened."; RefreshRecovery(); StatusText.Text = $"Analysis complete · {extractedMidi} MIDI extracted · kit destination untouched";
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.IO.InvalidDataException)
        {
            pendingBuild = null; BuildButton.IsEnabled = false; StatusText.Text = "Analysis stopped safely"; WarningDetailsText.Text = "Analysis could not complete: " + ex.Message;
        }
        finally { AnalyzeButton.IsEnabled = true; AddFilesButton.IsEnabled = true; }
    }
    private (BuildPlan? Plan, int Missing, int Ambiguous, int Excluded, int AlreadyProcessed) CreateBuildPlan()
    {
        var resolver = new PathResolver(sampleLibraryRoots); var resolved = new List<(string Path, string Project)>(); var missing = 0; var ambiguous = 0; var excluded = 0;
            foreach (var row in projects.Where(x => x.Analysis is not null)) foreach (var reference in row.Analysis!.SampleReferences)
            {
                var result = ResolveForBuild(resolver, row.Path, reference); if (result.Status == ResolutionStatus.Resolved && result.Path is not null) { if (SampleScreening.ShouldExclude(result.Path, out _)) excluded++; else resolved.Add((result.Path, row.Path)); } else if (result.Status == ResolutionStatus.Ambiguous) ambiguous++; else missing++;
            }
        if (resolved.Count == 0) return (null, missing, ambiguous, excluded, 0);
        var unique = new Deduplicator().Consolidate(resolved); var classifier = new SampleClassifier(); var names = new SweetNameGenerator(); var root = System.IO.Path.Combine(DefaultKitParent, DefaultKitName); var used = new HashSet<string>(System.IO.Directory.Exists(root) ? System.IO.Directory.EnumerateFiles(root, "*", System.IO.SearchOption.AllDirectories).Select(x => System.IO.Path.GetRelativePath(root, x)) : Array.Empty<string>(), StringComparer.OrdinalIgnoreCase); var files = new List<PlannedFile>(); var alreadyProcessed = 0;
        foreach (var sample in unique)
        {
            if (appState.ProcessedSounds.TryGetValue(sample.Hash, out var processed))
            {
                var resolvedPriorCategory=Enum.TryParse<SampleCategory>(processed.Category, out var priorCategory) ? priorCategory : SampleCategory.Unknown;var priorFile = System.IO.Path.Combine(processed.KitPath, CategoryRelativePath(processed.KitPath,resolvedPriorCategory), processed.OutputName);
                if (string.Equals(System.IO.Path.GetFullPath(processed.KitPath).TrimEnd('\\'), System.IO.Path.GetFullPath(root).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(priorFile)) { alreadyProcessed++; continue; }
                var reusedCategory = Enum.TryParse<SampleCategory>(processed.Category, out var storedCategory) ? storedCategory : SampleCategory.Unknown; var reusedName = System.IO.Path.ChangeExtension(processed.OutputName, System.IO.Path.GetExtension(sample.CanonicalPath));var reusedFolder=CategoryRelativePath(root,reusedCategory); var reusedRelative = System.IO.Path.Combine(reusedFolder, reusedName);
                if (!used.Add(reusedRelative)) { var stem = System.IO.Path.GetFileNameWithoutExtension(reusedName); var extension = System.IO.Path.GetExtension(reusedName); var number = 2; do { reusedRelative = System.IO.Path.Combine(reusedFolder, $"{stem} {number++}{extension}"); } while (!used.Add(reusedRelative)); }
                files.Add(new(sample.Hash, sample.CanonicalPath, reusedRelative, sample.SourcePaths, sample.Projects, new(reusedCategory, null, 1, new[] { "reused from processed-sound history" }))); continue;
            }
            sample.Classification = classifier.Classify(sample.CanonicalPath); if (sample.Classification.Category is SampleCategory.DrumLoop or SampleCategory.PercLoop or SampleCategory.MelodyLoop) { excluded++; continue; }
            var categoryFolder=CategoryRelativePath(root,sample.Classification.Category);var salt = 0; string filename; do { filename = System.IO.Path.ChangeExtension(names.Generate(sample.Hash, sample.Classification, salt++), System.IO.Path.GetExtension(sample.CanonicalPath)); } while (!used.Add(System.IO.Path.Combine(categoryFolder, filename)));
            files.Add(new(sample.Hash, sample.CanonicalPath, System.IO.Path.Combine(categoryFolder, filename), sample.SourcePaths, sample.Projects, sample.Classification));
        }
        return (files.Count == 0 ? null : new BuildPlan(DefaultKitName, DefaultKitParent, files, false, appState.MergeGeneratedKit), missing, ambiguous, excluded, alreadyProcessed);
    }
    private async void BuildButton_Click(object sender, RoutedEventArgs e)
    {
        if (pendingBuild is null || pendingBuild.Files.Count == 0) { System.Windows.MessageBox.Show("Analyze projects first so there is something real to build.", "No build plan", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var answer = System.Windows.MessageBox.Show($"Create {pendingBuild.Files.Count} verified sample copies in:\n\n{pendingBuild.RootPath}\n\nOriginal FLPs and samples will remain untouched.", "Approve stash-kit build", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        StopAllPlayback();BuildButton.IsEnabled = false; StatusText.Text = "Building and verifying stash kit…";
        try
        {
            var result = await new KitBuilder().BuildAsync(pendingBuild, true);
            foreach (var file in pendingBuild.Files)
            {
                var destination = System.IO.Path.Combine(result.RootPath, file.RelativePath);
                if (System.IO.File.Exists(destination) && Hashing.Sha256(destination) == file.Hash) appState.ProcessedSounds[file.Hash] = new() { Hash = file.Hash, OutputName = System.IO.Path.GetFileName(file.RelativePath), Category = file.Classification.Category.ToString(), KitPath = result.RootPath };
            }
            appState.History.Add(new() { CreatedUtc = DateTime.UtcNow, KitPath = result.RootPath, Added = result.Copied, SkippedDuplicates = pendingSkippedDuplicates }); AppStateStore.Save(appState); RefreshHistory(); BuildPreviewText.Text = $"Build complete.\n\nCopied and verified: {result.Copied}\nAlready imported: {pendingSkippedDuplicates}\nFailures: {result.Failures.Count}\n\n{result.RootPath}"; StatusText.Text = result.Failures.Count==0?"Kit build complete":"Kit built with copy failures";selectedFinalKitRoot=result.RootPath;finalKitDirty=true;finalKitChoicesDirty=true;reviewDirty=true;NavigateToBuildKit();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.IO.InvalidDataException) { BuildButton.IsEnabled = true; StatusText.Text = "Build stopped safely"; System.Windows.MessageBox.Show(ex.Message, "Build could not start", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private static string FriendlyWarning(string warning) => warning switch
    {
        var value when value.StartsWith("Partial parser:", StringComparison.OrdinalIgnoreCase) => "Partial scan: sampler paths were found, but this FLP may contain plugin-owned or newer data the current parser cannot inspect yet.",
        var value when value.Contains("signature", StringComparison.OrdinalIgnoreCase) => "This does not look like a readable FL Studio project file.",
        var value when value.Contains("event length", StringComparison.OrdinalIgnoreCase) => "The project contains an FLP event format this parser could not safely read.",
        var value when value.Contains("access", StringComparison.OrdinalIgnoreCase) => "Windows would not allow the project file to be read.",
        _ => warning
    };
    private void RefreshSummary() { pendingBuild = null; BuildButton.IsEnabled = false; BuildPreviewText.Text = "Analyze projects to create a verified build plan."; SummaryText.Text = $"Projects discovered: {projects.Count}\n\nNo destination files have been created."; if (projects.Count == 0) WarningDetailsText.Text = "No warnings yet. After analysis, select a project row to inspect its issues."; }
}
