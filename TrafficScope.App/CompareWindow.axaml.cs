using Avalonia.Controls;
using Avalonia.Media;
using TrafficScope.Core;
using TrafficScope.ViewModels;

namespace TrafficScope;

public partial class CompareWindow : Window
{
    static readonly IBrush Add = Brush.Parse("#332E9E5B"), Del = Brush.Parse("#33E24B4A"), Same = Brushes.Transparent;
    public CompareWindow() { InitializeComponent(); }

    public CompareWindow(Flow a, Flow b) : this()
    {
        LeftTitle.Text = $"− #{a.Id}  {a.Method} {a.Url}  ({a.StatusCode})";
        RightTitle.Text = $"+ #{b.Id}  {b.Method} {b.Url}  ({b.StatusCode})";
        var req = LineDiff.Compare(Formatting.RawRequest(a), Formatting.RawRequest(b));
        var res = LineDiff.Compare(Formatting.RawResponse(a), Formatting.RawResponse(b));
        ReqLines.ItemsSource = req.Select(Vm).ToList();
        ResLines.ItemsSource = res.Select(Vm).ToList();
        int Changes(List<DiffLine> d) => d.Count(x => x.Kind != DiffKind.Same);
        SummaryText.Text = $"{Changes(req)} changed lines in the request, {Changes(res)} in the response.";
    }

    static DiffLineVm Vm(DiffLine l) => l.Kind switch
    {
        DiffKind.Added => new("+", l.Text, Add),
        DiffKind.Removed => new("−", l.Text, Del),
        _ => new(" ", l.Text, Same)
    };
}
