using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrafficScope.Core;

namespace TrafficScope.ViewModels;

/// <summary>Record, Stop and Pause.</summary>
public partial class MainViewModel
{
    // ---------- capture ----------
    [RelayCommand]
    async Task ToggleCaptureAsync()
    {
        if (IsCapturing) { await StopAsync(); return; }
        if (SelectedKeys().Length == 0) { Say("Select at least one app on the left first.", NoticeKind.Warning); return; }

        Status = "Starting proxy…";
        try
        {
            var wasRunning = _engine.IsRunning;   // still passing traffic through from an earlier recording
            _engine.Start();
            var cert = _engine.RootCertificate!;
            var trusted = await Task.Run(() => CertificateService.IsTrusted(cert));
            if (!trusted)
            {
                Status = "Trusting the TrafficScope certificate (admin approval needed)…";
                await Task.Run(() => CertificateService.Install(cert, FavoritesStore.DataDir));
                trusted = await Task.Run(() => CertificateService.IsTrusted(cert));
            }
            CertTrusted = trusted;
            if (!trusted)
            {
                // Never intercept HTTPS without a trusted certificate: apps would show "certificate invalid" errors.
                if (!wasRunning) _engine.Stop();
                Fail("Not started: the TrafficScope certificate is not trusted yet, so your apps would show certificate errors. Use Install certificate, then press Record again.");
                return;
            }
            Status = "Setting system proxy…";
            var (pok, pmsg) = await Task.Run(() => SystemProxy.Enable(_engine.Port));
            if (!pok) { if (!wasRunning) _engine.Stop(); Fail("Could not set the system proxy. " + pmsg); return; }
            _engine.BeginCapture(SelectedKeys());
            ProxyStillOn = false;
            IsPaused = false;
            IsCapturing = true;
            Status = $"Recording since {_engine.CaptureStartedAt:HH:mm:ss}. Only selected apps are recorded."
                + (OperatingSystem.IsMacOS() && !SystemProxy.LastRunWithoutPrompt ? " Tip: to stop the password prompts, open Settings > Proxy permission." : "");
            Tick();
        }
        catch (Exception ex) { Fail("Failed to start: " + ex.Message); }
    }

    public async Task StopAsync()
    {
        _engine.EndCapture();
        var (ok, msg) = await Task.Run(RestoreProxy);
        // Keep the proxy listening as a silent pass-through until TrafficScope quits: some apps
        // (Adobe AIR, Java, Electron) keep using the proxy they saw at launch and would lose
        // their connection ("No response from server") if it vanished.
        IsCapturing = false;
        IsPaused = false;
        ProxyStillOn = !ok;
        if (ok) Say("Stopped. System proxy restored; apps that are still using it keep working while TrafficScope is open.", NoticeKind.Success);
        else Fail("Could not turn the system proxy off. " + msg + " Press Turn proxy off and approve the prompt.");
        Tick();
    }

    [RelayCommand] void TogglePause() { if (IsCapturing) IsPaused = !IsPaused; Tick(); }

    [RelayCommand]
    void Clear()
    {
        _all.Clear(); _byFlow.Clear(); Flows.Clear(); SelectedFlow = null; _counts.Clear(); DroppedFlows = 0;
        foreach (var a in Favorites.Concat(Others)) a.Count = 0;
        NotifyCounts();
    }
}
