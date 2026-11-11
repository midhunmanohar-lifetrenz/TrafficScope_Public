using Avalonia.Controls;
using Avalonia.Interactivity;
using TrafficScope.ViewModels;

namespace TrafficScope;

public partial class RulesWindow : Window
{
    public RulesWindow() { InitializeComponent(); }
    public RulesWindow(RulesVm vm) : this() { DataContext = vm; Closing += (_, _) => vm.Commit(); }
    void OnDone(object? sender, RoutedEventArgs e) => Close();
}
