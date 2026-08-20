using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using StashKitMaker.Core;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;

namespace StashKitMaker.Mac;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ProjectRow> projects = new();
    private readonly ObservableCollection<PlanRow> planRows = new();
    private readonly List<string> sampleRoots = new();
    private readonly MacState state;
    private BuildPlan? pendingPlan;

    public MainWindow()
    {
        InitializeComponent();
        state = MacState.Load();
        sampleRoots.AddRange(state.SampleRoots.Where(Directory.Exists));
        DestinationText.Text = state.DestinationParent;
        KitNameText.Text = string.IsNullOrWhiteSpace(state.KitName) ? "Renzy's Stash Kit" : state.KitName;
        ProjectsGrid.ItemsSource = projects;
        PlanGrid.ItemsSource = planRows;
        StatusText.Text = sampleRoots.Count == 0 ? "Ready · Add FL Studio projects and optionally a sample-library root." : $"Ready · {sampleRoots.Count} saved sample-library root(s).";
    }

    private async void AddFiles_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose FL Studio projects",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("FL Studio projects") { Patterns = ["*.flp"] }]
        });
        AddProjects(files.Select(file => file.TryGetLocalPath()).OfType<string>());
    }

    private async void AddFolder_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose a folder containing FL Studio projects", AllowMultiple = false });
        var root = folders.FirstOrDefault()?.TryGetLocalPath();
        if (root is null) return;
        AddProjects(Directory.EnumerateFiles(root, "*.flp", SearchOption.AllDirectories));
    }

    private async void AddSampleRoot_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose a sample-library search folder", AllowMultiple = false });
        var root = folders.FirstOrDefault()?.TryGetLocalPath();
        if (root is null || sampleRoots.Contains(root, StringComparer.OrdinalIgnoreCase)) return;
        sampleRoots.Add(root);
        SaveState();
        StatusText.Text = $"Added sample-library root: {root}";
    }

    private void AddProjects(IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(File.Exists).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
            if (!projects.Any(project => string.Equals(project.Path, path, StringComparison.OrdinalIgnoreCase)))
                projects.Add(new ProjectRow(path));
        pendingPlan = null;
        planRows.Clear();
        StatusText.Text = $"{projects.Count} FL Studio project(s) queued. Analysis is read-only.";
    }

    private void RemoveSelected_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var item in ProjectsGrid.SelectedItems.Cast<ProjectRow>().ToArray()) projects.Remove(item);
        pendingPlan = null;
        planRows.Clear();
        StatusText.Text = $"{projects.Count} project(s) remain.";
    }

    private void ClearProjects_Click(object? sender, RoutedEventArgs e)
    {
        projects.Clear();
        pendingPlan = null;
        planRows.Clear();
        StatusText.Text = "Project list cleared. No files were changed.";
    }

    private async void Analyze_Click(object? sender, RoutedEventArgs e)
    {
        if (projects.Count == 0) { StatusText.Text = "Add at least one FLP project first."; return; }
        StatusText.Text = "Analyzing FLPs and resolving sample references…";
        await Task.Run(CreateBuildPlan);
    }

    private void CreateBuildPlan()
    {
        var parser = new EventStreamFlpParser();
        var candidates = new List<(string Path, string Project, string? Channel)>();
        var warnings = 0;
        foreach (var row in projects)
        {
            var analysis = parser.ParseProject(row.Path);
            row.Status = analysis.Status.ToString();
            row.Samples = analysis.SampleReferences.Count;
            row.Notes = string.Join(" ", analysis.Warnings.Take(2));
            warnings += analysis.Warnings.Count;
            var roots = sampleRoots.Append(Path.GetDirectoryName(row.Path)!).Distinct(StringComparer.OrdinalIgnoreCase);
            var resolver = new PathResolver(roots);
            foreach (var reference in analysis.SampleReferences)
            {
                var resolution = resolver.Resolve(reference.StoredPath, row.Path);
                if (resolution.Status == ResolutionStatus.Resolved && resolution.Path is not null && !SampleScreening.ShouldExclude(resolution.Path, out _))
                    candidates.Add((resolution.Path, row.Path, reference.ChannelName));
                else warnings++;
            }
        }

        var unique = new Deduplicator().Consolidate(candidates.Select(item => (item.Path, item.Project)));
        var channels = candidates.GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Channel).FirstOrDefault(channel => !string.IsNullOrWhiteSpace(channel)), StringComparer.OrdinalIgnoreCase);
        var classifier = new SampleClassifier();
        var names = new SweetNameGenerator();
        var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<PlannedFile>();
        foreach (var sample in unique.OrderBy(sample => sample.CanonicalPath, StringComparer.OrdinalIgnoreCase))
        {
            sample.Classification = classifier.Classify(sample.CanonicalPath, channels.GetValueOrDefault(sample.CanonicalPath));
            var salt = 0;
            string relative;
            do
            {
                sample.OutputName = names.Generate(sample.Hash, sample.Classification, salt++);
                relative = Path.Combine(CategoryFolders.For(sample.Classification.Category), sample.OutputName);
            } while (!usedPaths.Add(relative));
            files.Add(new PlannedFile(sample.Hash, sample.CanonicalPath, relative, sample.SourcePaths, sample.Projects, sample.Classification));
        }

        Dispatcher.UIThread.Post(() =>
        {
            planRows.Clear();
            foreach (var file in files) planRows.Add(new PlanRow(file));
            pendingPlan = new BuildPlan(WindowsNames.Sanitize(KitNameText.Text?.Trim() ?? ""), DestinationText.Text?.Trim() ?? "", files, ProvenanceCheck.IsChecked == true, MergeCheck.IsChecked == true);
            ProjectsGrid.ItemsSource = null;
            ProjectsGrid.ItemsSource = projects;
            StatusText.Text = $"Analysis complete · {files.Count} unique sample(s) ready · {warnings} unresolved/excluded/parser warning(s).";
        });
    }

    private async void ChooseDestination_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose the parent folder for generated kits", AllowMultiple = false });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null) DestinationText.Text = path;
    }

    private async void Build_Click(object? sender, RoutedEventArgs e)
    {
        if (pendingPlan is null) { StatusText.Text = "Analyze projects to create a build plan first."; return; }
        var destination = DestinationText.Text?.Trim() ?? "";
        var name = WindowsNames.Sanitize(KitNameText.Text?.Trim() ?? "");
        if (!Directory.Exists(destination) || string.IsNullOrWhiteSpace(name)) { StatusText.Text = "Choose an existing destination parent and a valid kit name."; return; }
        var plan = pendingPlan with { KitName = name, DestinationParent = destination, IncludePrivateProvenance = ProvenanceCheck.IsChecked == true, MergeIntoGeneratedKit = MergeCheck.IsChecked == true };
        try
        {
            StatusText.Text = $"Building approved kit with {plan.Files.Count} file(s)…";
            var result = await new KitBuilder().BuildAsync(plan, explicitApproval: true);
            state.DestinationParent = destination;
            state.KitName = name;
            SaveState();
            StatusText.Text = result.Failures.Count == 0 ? $"Built {result.Copied} sample(s). Click here to reveal in Finder." : $"Built {result.Copied} sample(s); {result.Failures.Count} copy failure(s). Click here to reveal in Finder.";
            StatusText.PointerPressed += (_, _) => RevealInFinder(result.RootPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            StatusText.Text = "Build stopped safely: " + exception.Message;
        }
    }

    private static void RevealInFinder(string path) => Process.Start(new ProcessStartInfo("open", $"-R \"{path.Replace("\"", "\\\"")}\"") { UseShellExecute = false });

    private void SaveState()
    {
        state.SampleRoots = sampleRoots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        state.DestinationParent = DestinationText.Text?.Trim() ?? state.DestinationParent;
        state.KitName = KitNameText.Text?.Trim() ?? state.KitName;
        state.Save();
    }
}

public sealed class ProjectRow
{
    public ProjectRow(string path) => Path = path;
    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(Path);
    public string Status { get; set; } = "Queued";
    public int Samples { get; set; }
    public string Notes { get; set; } = "Not analyzed";
}

public sealed class PlanRow(PlannedFile file)
{
    public string Output { get; } = file.RelativePath;
    public string Category { get; } = SweetNameGenerator.Label(file.Classification.Category);
    public string Source { get; } = file.SourcePath;
    public string Evidence { get; } = string.Join("; ", file.Classification.Evidence);
}

public sealed class MacState
{
    public List<string> SampleRoots { get; set; } = new();
    public string DestinationParent { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
    public string KitName { get; set; } = "Renzy's Stash Kit";

    private static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RenzysStashKitMaker");
    private static string FilePath => Path.Combine(Folder, "macos-state.json");
    public static MacState Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<MacState>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch (JsonException) { return new(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(Folder);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, FilePath, true);
    }
}
