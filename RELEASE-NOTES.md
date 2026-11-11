# TrafficScope 0.3.1

See every HTTP/HTTPS request and response from the apps you choose, on Mac and Windows.

## Downloads
| File | For |
|---|---|
| `TrafficScope-0.3.1-macOS-AppleSilicon.dmg` | Macs with Apple Silicon (M1 and later), macOS 12+ |
| `TrafficScope-0.3.1-Windows-Setup.exe` | Windows 10/11 x64, installs for the current user (no admin rights) |
| `TrafficScope-0.3.1-Windows-Portable.zip` | Windows x64 without installing (single `TrafficScope.exe`) |
| `SHA256SUMS-0.3.1.txt` | Checksums to confirm the files arrived intact |

## Install
**Mac:** open the dmg, drag TrafficScope to Applications, open it. The first time, macOS blocks it because the build is not
signed with an Apple Developer ID yet: click Done, then **System Settings > Privacy & Security > Open Anyway**.

**Windows:** run `TrafficScope-0.3.1-Windows-Setup.exe`. If SmartScreen says "Windows protected your PC", click
**More info > Run anyway** (the installer is not code-signed yet). Remove it from **Settings > Apps > Installed apps**.
IT/silent: `TrafficScope-0.3.1-Windows-Setup.exe /S` (add `/nodesktop` to skip the Desktop shortcut); uninstall with `/uninstall /S`.

## First use
1. Press **Install certificate** and approve the system prompt (needed to read HTTPS).
2. Switch on the apps to inspect, press **Record**, use the app, press **Stop**.

## Features
- Per-app capture with favourites, only from the moment you press Record
- Inspector: JSON tree, decoded forms, headers with JWT decode, params, cookies, timing, raw; copy icons everywhere
- Filters (Errors, JSON, Images, Slow) and search across URL, app, status and body
- Replay, Edit and resend, Compare two requests, Save as HAR
- Mock responses, request/response breakpoints, slow network, decrypt-only host list, secret masking
- Send to Postman (saves into your Postman workspace via the Postman API)
- Light, Dark and System themes
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
