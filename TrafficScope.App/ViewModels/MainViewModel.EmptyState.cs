using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrafficScope.Core;

namespace TrafficScope.ViewModels;

/// <summary>Explains an empty request list (decrypt-only list, filters) and offers fixes.</summary>
public partial class MainViewModel
{
    // ---------- empty list explanations ----------
    /// <summary>Tunnels whose host is still outside the decrypt-only list (these are why the list can look empty).</summary>
    List<FlowVm> BlockedTunnels => Settings.DecryptOnlyHosts.Count == 0 ? new()
        : _all.Where(f => f.Flow.IsTunnel && !HostPattern.MatchesAny(f.Flow.Host, Settings.DecryptOnlyHosts)).ToList();
    List<string> SuggestedHosts => HostPattern.SuggestAll(BlockedTunnels.Select(f => f.Flow.Host));
    public bool ShowTunnelHint => Flows.Count == 0 && !ShowTunnels && BlockedTunnels.Count > 0;
    public bool ShowFilterHint => Flows.Count == 0 && !ShowTunnelHint && _all.Any(f => !f.Flow.IsTunnel);
    public bool ShowStartHint => Flows.Count == 0 && !ShowTunnelHint && !ShowFilterHint;
    public string TunnelHintTitle
    {
        get { var n = BlockedTunnels.Count; return $"{n} connection{(n == 1 ? " was" : "s were")} recorded but not decrypted"; }
    }
    public string TunnelHintDetail
    {
        get
        {
            var hosts = BlockedTunnels.Select(f => f.Flow.Host).Distinct().ToList();
            var shown = string.Join(", ", hosts.Take(3)) + (hosts.Count > 3 ? $" and {hosts.Count - 3} more" : "");
            return $"Your app talks to {shown}, which {(hosts.Count == 1 ? "is" : "are")} not on your decrypt-only list " +
                   $"({string.Join(", ", Settings.DecryptOnlyHosts)}). Add {(hosts.Count == 1 ? "it" : "them")} to see requests and bodies.";
        }
    }
    public string DecryptSuggestedText
    {
        get { var s = SuggestedHosts; return "Decrypt " + string.Join(", ", s.Take(2)) + (s.Count > 2 ? $" +{s.Count - 2}" : ""); }
    }

    void NotifyEmptyState()
    {
        foreach (var p in new[] { nameof(ShowTunnelHint), nameof(ShowFilterHint), nameof(ShowStartHint),
                                  nameof(TunnelHintTitle), nameof(TunnelHintDetail), nameof(DecryptSuggestedText) })
            OnPropertyChanged(p);
    }

    [RelayCommand]
    async Task DecryptSuggested()
    {
        var add = SuggestedHosts.Where(h => !Settings.DecryptOnlyHosts.Contains(h, StringComparer.OrdinalIgnoreCase)).ToList();
        if (add.Count == 0) return;
        Settings.DecryptOnlyHosts.AddRange(add);
        SaveSettings();
        await ShowToast($"Added {string.Join(", ", add)}. Use your app again. If nothing appears, press Stop, then Record.");
    }

    [RelayCommand]
    async Task DecryptAllHosts()
    {
        Settings.DecryptOnlyHosts.Clear();
        SaveSettings();
        await ShowToast("All hosts of the selected apps will be decrypted. Use your app again. If nothing appears, press Stop, then Record.");
    }

    [RelayCommand] void ClearFilters() { FilterText = ""; FilterKind = FlowFilterKind.All; }
}
