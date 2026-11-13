# TrafficScope 0.3.3

See every HTTP/HTTPS request and response from the apps you choose, on Mac and Windows.

## Downloads
| File | For |
|---|---|
| `TrafficScope-0.3.3-macOS-AppleSilicon.dmg` | Macs with Apple Silicon (M1 and later), macOS 12+ |
| `TrafficScope-0.3.3-Windows-Setup.exe` | Windows 10/11 x64, installs for the current user (no admin rights) |
| `TrafficScope-0.3.3-Windows-Portable.zip` | Windows x64 without installing (single `TrafficScope.exe`) |
| `SHA256SUMS-0.3.3.txt` | Checksums to confirm the files arrived intact |

## Install
**Mac:** open the dmg, drag TrafficScope to Applications, open it. The first time, macOS blocks it because the build is not
signed with an Apple Developer ID yet: click Done, then **System Settings > Privacy & Security > Open Anyway**.

**Windows:** run `TrafficScope-0.3.3-Windows-Setup.exe`. If SmartScreen says "Windows protected your PC", click
**More info > Run anyway** (the installer is not code-signed yet). Remove it from **Settings > Apps > Installed apps**.
IT/silent: `TrafficScope-0.3.3-Windows-Setup.exe /S` (add `/nodesktop` to skip the Desktop shortcut); uninstall with `/uninstall /S`.

## First use
1. Press **Install certificate** (top right) and approve the system prompt (needed to read HTTPS). It turns into a green **Verified** button.
2. Switch on the apps to inspect, press **Record**, use the app, press **Stop**.

## Features
- Per-app capture with favourites, only from the moment you press Record
- Inspector: JSON tree, decoded forms, headers with JWT decode, params, cookies, timing, raw; copy icons everywhere
- Filters (Errors, JSON, Images, Slow) and search across URL, app, status and body
- Replay, Edit and resend, Compare two requests, Save as HAR
- Mock responses, request/response breakpoints, slow network, decrypt-only host list, secret masking
- Send to Postman (saves into your Postman workspace via the Postman API)
- Light, Dark and System themes
- New in 0.3.3 (security and reliability; updating is recommended):
  - **Certificate key is protected.** The key file is now readable only by you and has a random password kept in the
    Keychain (Windows: DPAPI). An existing key is converted on first start; the certificate stays the same and stays trusted
  - The optional Mac "don't ask for my password" rule is installed more safely and is narrower: it only allows pointing
    the proxy at this computer or switching it off. If you turned it on before, remove it and turn it on again in Settings
  - Bodies over 8 MB and binary streams of unknown size pass straight through instead of filling memory
  - Requests that cannot reach the server show **ERR** with the reason instead of staying pending
  - Mac: a proxy that was switched on before recording (for example a company proxy) is put back on Stop
  - Faster when not recording; the list keeps the latest 5,000 requests
  - Settings has **Remove certificate**
- New in 0.3.2:
  - New look: one calm surface with a pill search bar, a Record/Stop pill with a timer, pill chips and tabs, and the
    inspector as a rounded panel; Plum and gold colours for light and dark, with colour-coded method tags
  - **Stop no longer cuts apps off.** Apps such as Adobe AIR or Java apps keep using the proxy they saw at launch;
    after Stop, TrafficScope keeps passing their traffic through (without recording) until you quit it
  - If Stop cannot switch the system proxy off (for example the password prompt was cancelled), a red bar with
    **Turn proxy off** stays visible instead of a false "restored" message
  - Messages appear as notification banners (bottom right) that hide themselves and can be dismissed
  - The Tools menu is replaced by small icons: Mock responses, Breakpoints, Settings and **Reset all**
  - **Reset all** clears requests, mocks, breakpoints, the decrypt-only list, settings and the Postman connection,
    and keeps your favourites, selected apps and the certificate
- New in 0.3.1: when nothing shows up because of your decrypt-only list, the empty screen says how many
  connections were hidden, names their hosts, and offers **Decrypt <site>**, **Decrypt all hosts** or **Edit list…**;
  an empty list caused by a filter or search now offers **Clear filter**
- New in 0.3.0: Windows installer with uninstall; recovery bar if a previous session left the proxy on;
  optional Mac setting to stop the password prompt on Record/Stop; app icon and version details on Windows

## Known limits
- Apps that ignore the system proxy settings are not captured; apps that pin certificates fail while decrypted
  (leave their hosts out of "Decrypt only these hosts").
- Not code-signed or notarized yet, so first-run security prompts appear on both platforms. Some managed Windows PCs
  with Smart App Control block unsigned apps entirely.
- The Windows build is checked automatically on a Windows machine (silent install, launch, uninstall) but has had
  little hands-on use yet.
- Intel Macs are not supported in this build.

## Privacy
Recorded traffic stays on your computer. Only **Send to Postman** uploads a request, to your own Postman workspace.
Turn on **Mask Authorization, cookies and API keys** before copying, saving or sending if they contain secrets.
