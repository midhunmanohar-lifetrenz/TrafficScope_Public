using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace TrafficScope.ViewModels;

public partial class AppItemVm : ObservableObject
{
    readonly Action<AppItemVm> _selectionChanged;
    readonly Action<AppItemVm> _favoriteToggled;

    public AppItemVm(string name, string key, bool running, bool selected, bool favorite, Action<AppItemVm> selectionChanged, Action<AppItemVm> favoriteToggled)
    {
        Name = name; Key = key; IsRunning = running;
        _isSelected = selected; _isFavorite = favorite;
        _selectionChanged = selectionChanged; _favoriteToggled = favoriteToggled;
    }

    public string Name { get; }
    public string Key { get; }
    public bool IsRunning { get; }
    public string Subtitle => IsRunning ? "Running" : "Not running";
    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";
    public IBrush AvatarBrush => Palette.Avatar(Name);
    [ObservableProperty] Bitmap? _icon;
    [ObservableProperty] int _count;
    public bool HasIcon => Icon != null;
    partial void OnIconChanged(Bitmap? value) => OnPropertyChanged(nameof(HasIcon));
    public string CountText => Count > 0 ? Count.ToString() : "";
    partial void OnCountChanged(int value) => OnPropertyChanged(nameof(CountText));

    [ObservableProperty] bool _isSelected;
    [ObservableProperty] bool _isFavorite;

    partial void OnIsSelectedChanged(bool value) => _selectionChanged(this);
    public string StarGlyph => IsFavorite ? "★" : "☆";
    partial void OnIsFavoriteChanged(bool value) => OnPropertyChanged(nameof(StarGlyph));

    [RelayCommand] void ToggleFavorite() => _favoriteToggled(this);
}
