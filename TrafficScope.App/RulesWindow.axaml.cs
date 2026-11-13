using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using TrafficScope.ViewModels;

namespace TrafficScope;

public partial class RulesWindow : Window
{
    public RulesWindow() { InitializeComponent(); }
    public RulesWindow(RulesVm vm) : this() { DataContext = vm; Closing += (_, _) => vm.Commit(); }
    void OnDone(object? sender, RoutedEventArgs e) => Close();

    async void OnChooseMapFile(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RulesVm { SelectedMap: { } map }) return;
        var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions { Title = "Choose the file to answer with", AllowMultiple = false });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) map.LocalPath = path;
    }
}
