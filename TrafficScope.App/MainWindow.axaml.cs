using Avalonia.Controls;
using Avalonia.Platform.Storage;
using TrafficScope.Core;
using TrafficScope.ViewModels;

namespace TrafficScope;

public partial class MainWindow : Window, IUiServices
{
    readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        _vm.Ui = this;
        _vm.ApplyTheme();
        FlowGrid.SelectionChanged += (_, _) => _vm.SetMultiSelection(FlowGrid.SelectedItems.OfType<FlowVm>());
        Closing += (_, _) => _vm.Dispose();
    }

    public MainViewModel ViewModel => _vm;

    public async Task SetClipboardAsync(string text) { if (Clipboard != null) await Clipboard.SetTextAsync(text); }

    public async Task<string?> PickSaveFileAsync(string suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedName, DefaultExtension = "har",
            FileTypeChoices = new[] { new FilePickerFileType("HTTP Archive") { Patterns = new[] { "*.har" } } },
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickOpenFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a saved session or HAR file", AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("HTTP Archive") { Patterns = new[] { "*.har", "*.json" } }, FilePickerFileTypes.All },
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task EditAsync(EditModel model) => await new EditWindow(model).ShowDialog(this);

    public async Task<bool> OpenRulesAsync(AppSettings settings, int tab)
    {
        await new RulesWindow(new RulesVm(settings, tab)).ShowDialog(this);
        return true;
    }

    public void ShowCompare(Flow a, Flow b) => new CompareWindow(a, b).Show(this);

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        var result = false;
        var dlg = new Window
        {
            Title = title, Width = 460, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
        };
        var ok = new Button { Content = confirmText, Classes = { "primary", "stop" } };
        var cancel = new Button { Content = "Cancel", Classes = { "tool" }, IsCancel = true };
        ok.Click += (_, _) => { result = true; dlg.Close(); };
        cancel.Click += (_, _) => dlg.Close();
        dlg.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20), Spacing = 12,
            Children =
            {
                new TextBlock { Text = title, FontSize = 16, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "muted" } },
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Children = { cancel, ok } },
            },
        };
        await dlg.ShowDialog(this);
        return result;
    }
}
