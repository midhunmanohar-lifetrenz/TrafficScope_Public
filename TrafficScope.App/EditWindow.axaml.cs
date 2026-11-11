using Avalonia.Controls;
using Avalonia.Interactivity;
using TrafficScope.ViewModels;

namespace TrafficScope;

public partial class EditWindow : Window
{
    readonly EditModel? _m;
    public EditWindow() { InitializeComponent(); }

    public EditWindow(EditModel m) : this()
    {
        _m = m;
        Title = m.Title; Heading.Text = m.Title;
        MethodBox.Text = m.Method; UrlBox.Text = m.Url; StatusBox.Text = m.Status.ToString();
        HeadersBox.Text = m.Headers; BodyBox.Text = m.Body;
        StatusLine.IsVisible = m.Mode == EditMode.BreakResponse;
        MethodBox.IsReadOnly = UrlBox.IsReadOnly = m.Mode == EditMode.BreakResponse;
        switch (m.Mode)
        {
            case EditMode.Compose:
                PrimaryButton.Content = "Send"; SecondaryButton.Content = "Cancel";
                Hint.Text = "Sent directly from TrafficScope. The result appears in the list as Resent.";
                break;
            case EditMode.BreakRequest:
                PrimaryButton.Content = "Continue"; SecondaryButton.Content = "Cancel request";
                Hint.Text = "Your app is waiting. Edit anything, then Continue. Cancel request answers the app with 403.";
                break;
            default:
                PrimaryButton.Content = "Continue"; SecondaryButton.Content = "Send unchanged";
                Hint.Text = "Your app is waiting for this response. Edit the status, headers or body, then Continue.";
                break;
        }
        Closing += (_, _) => { if (!m.Accepted && !m.Abort && m.Mode != EditMode.Compose) m.Accepted = false; };
    }

    void OnPrimary(object? sender, RoutedEventArgs e)
    {
        if (_m == null) return;
        if (_m.Mode == EditMode.BreakResponse && (!int.TryParse(StatusBox.Text, out var st) || st < 100 || st > 599))
        { Hint.Text = "Enter a status code between 100 and 599."; return; }
        if (_m.Mode != EditMode.BreakResponse && !Uri.TryCreate(UrlBox.Text?.Trim(), UriKind.Absolute, out _))
        { Hint.Text = "Enter a full URL, for example https://api.example.com/v1/items."; return; }
        _m.Method = MethodBox.Text ?? "GET"; _m.Url = UrlBox.Text ?? ""; _m.Headers = HeadersBox.Text ?? ""; _m.Body = BodyBox.Text ?? "";
        if (int.TryParse(StatusBox.Text, out var s)) _m.Status = s;
        _m.Accepted = true;
        Close();
    }

    void OnSecondary(object? sender, RoutedEventArgs e)
    {
        if (_m == null) return;
        if (_m.Mode == EditMode.BreakRequest) _m.Abort = true;
        Close();
    }
}
