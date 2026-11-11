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

    public async Task EditAsync(EditModel model) => await new EditWindow(model).ShowDialog(this);

    public async Task<bool> OpenRulesAsync(AppSettings settings, int tab)
    {
        await new RulesWindow(new RulesVm(settings, tab)).ShowDialog(this);
        return true;
    }

    public void ShowCompare(Flow a, Flow b) => new CompareWindow(a, b).Show(this);
}
