using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Media;

namespace StashKitMaker.App;

public sealed class FinalKitItemViewModel : INotifyPropertyChanged
{
    private string displayName = "";
    private string unicodeIcon = "";
    private string textColor = "#454142";
    private string audioPath = "";
    private string playGlyph = "▶";
    public string StableId { get; init; } = "";
    public string ParentId { get; init; } = "";
    public bool IsGroup { get; init; }
    public bool HasAudio => !IsGroup && !string.IsNullOrWhiteSpace(AudioPath);
    public ObservableCollection<FinalKitItemViewModel> Children { get; } = new();
    public string DisplayName { get => displayName; set => Set(ref displayName, value); }
    public string UnicodeIcon { get => unicodeIcon; set => Set(ref unicodeIcon, value); }
    public string TextColor { get => textColor; set => Set(ref textColor, value); }
    public string AudioPath { get => audioPath; set { if(Set(ref audioPath,value)) OnPropertyChanged(nameof(HasAudio)); } }
    public string PlayGlyph { get => playGlyph; set => Set(ref playGlyph, value); }
    public event PropertyChangedEventHandler? PropertyChanged;
    private bool Set<T>(ref T field,T value,[CallerMemberName]string? name=null){if(EqualityComparer<T>.Default.Equals(field,value))return false;field=value;OnPropertyChanged(name);return true;}
    private void OnPropertyChanged([CallerMemberName]string? name=null)=>PropertyChanged?.Invoke(this,new(name));
}

public sealed class HexColorBrushConverter : IValueConverter
{
    public object Convert(object value,Type targetType,object parameter,CultureInfo culture){try{return new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value?.ToString()??"#454142"));}catch{return new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x45,0x41,0x42));}}
    public object ConvertBack(object value,Type targetType,object parameter,CultureInfo culture)=>System.Windows.Data.Binding.DoNothing;
}

public sealed class BoolVisibilityConverter : IValueConverter
{
    public object Convert(object value,Type targetType,object parameter,CultureInfo culture)=>value is true?System.Windows.Visibility.Visible:System.Windows.Visibility.Collapsed;
    public object ConvertBack(object value,Type targetType,object parameter,CultureInfo culture)=>System.Windows.Data.Binding.DoNothing;
}

public sealed class FolderEditorViewModel : INotifyPropertyChanged
{
    private string? parentId;
    private string displayName = "";
    private int flIconIndex = 1014;
    private int flHeightOffset = 5;
    private int flSortGroup = 8;
    private string flTip = "";
    private int depth;
    private string webColor = "#93977F";
    private string flColor = "$7F9793";
    public string StableId { get; init; } = "";
    public string? ParentId { get => parentId; set => Set(ref parentId, value); }
    public string DisplayName { get => displayName; set => Set(ref displayName, value); }
    public string OriginalRelativePath { get; set; } = "";
    public List<string> Categories { get; init; } = new();
    public bool IsRoot { get; init; }
    public int FlIconIndex { get => flIconIndex; set => Set(ref flIconIndex, value); }
    public int FlHeightOffset { get => flHeightOffset; set => Set(ref flHeightOffset, value); }
    public int FlSortGroup { get => flSortGroup; set => Set(ref flSortGroup, value); }
    public string FlTip { get => flTip; set => Set(ref flTip, value); }
    public int Depth { get => depth; set => Set(ref depth, value); }
    public string WebColor { get => webColor; set => Set(ref webColor, value); }
    public string FlColor { get => flColor; set => Set(ref flColor, value); }
    public string Label => IsRoot ? DisplayName + " (kit root)" : DisplayName;
    public event PropertyChangedEventHandler? PropertyChanged;
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; PropertyChanged?.Invoke(this, new(name)); if (name == nameof(DisplayName)) PropertyChanged?.Invoke(this, new(nameof(Label))); return true; }
}

public sealed record FolderParentOption(string? StableId, string Label);
