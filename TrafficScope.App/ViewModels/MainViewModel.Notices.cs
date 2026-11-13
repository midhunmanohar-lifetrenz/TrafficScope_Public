using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrafficScope.Core;

namespace TrafficScope.ViewModels;

/// <summary>Notification banner: what it says, its colour and when it hides.</summary>
public partial class MainViewModel
{
    // ---------- notification banner ----------
    [ObservableProperty] string _noticeText = "";
    [ObservableProperty] NoticeKind _noticeKind;
    CancellationTokenSource? _noticeCts;
    public bool HasNotice => NoticeText.Length > 0;
    public bool NoticeIsError => NoticeKind == NoticeKind.Error;
    public bool NoticeIsWarning => NoticeKind == NoticeKind.Warning;
    public bool NoticeIsSuccess => NoticeKind == NoticeKind.Success;
    public bool NoticeIsInfo => NoticeKind == NoticeKind.Info;
    public string NoticeGlyph => NoticeKind switch { NoticeKind.Success => "✓", NoticeKind.Info => "i", _ => "!" };
    partial void OnNoticeTextChanged(string value) => OnPropertyChanged(nameof(HasNotice));
    partial void OnNoticeKindChanged(NoticeKind value)
    {
        foreach (var p in new[] { nameof(NoticeIsError), nameof(NoticeIsWarning), nameof(NoticeIsSuccess), nameof(NoticeIsInfo), nameof(NoticeGlyph) })
            OnPropertyChanged(p);
    }
    NoticeKind? _nextKind;
    partial void OnStatusChanged(string value) { var kind = _nextKind ?? Classify(value); _nextKind = null; Notify(value, kind); }
    /// <summary>Sets the status with an explicit kind, so the colour never depends on the wording.</summary>
    void Say(string text, NoticeKind kind)
    {
        _nextKind = kind;
        if (Status == text) { _nextKind = null; Notify(text, kind); } else Status = text;
    }
    void Fail(string text) => Say(text, NoticeKind.Error);

    public static NoticeKind Classify(string text)
    {
        if (text.StartsWith("Could not") || text.StartsWith("Failed") || text.StartsWith("Not started") || text.Contains("not trusted yet"))
            return NoticeKind.Error;
        if (text.StartsWith("Select at least")) return NoticeKind.Warning;
        if (text.EndsWith("…")) return NoticeKind.Info;
        return NoticeKind.Success;
    }

    /// <summary>Shows a banner that hides itself; progress messages ("…") stay until the next one, errors stay longer.</summary>
    public async void Notify(string text, NoticeKind kind)
    {
        _noticeCts?.Cancel();
        _noticeCts = new CancellationTokenSource();
        var token = _noticeCts.Token;
        NoticeKind = kind; NoticeText = text;
        if (string.IsNullOrEmpty(text) || (kind == NoticeKind.Info && text.EndsWith("…"))) return;
        try { await Task.Delay(kind == NoticeKind.Error ? 10000 : kind == NoticeKind.Warning ? 6000 : 4500, token); NoticeText = ""; }
        catch (TaskCanceledException) { }
    }

    [RelayCommand] void DismissNotice() { _noticeCts?.Cancel(); NoticeText = ""; }
}
