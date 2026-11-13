# TrafficScope 0.3

Proxyman/Fiddler-style HTTP(S) inspector for macOS (Apple Silicon) and Windows x64, with **per-app capture**.
Plan and market research: [PLAN.md](PLAN.md). What to send people: [RELEASE-NOTES.md](RELEASE-NOTES.md).
Version, company and product name live in `Directory.Build.props`.

## First run
1. Open TrafficScope. If a yellow bar says "Set up once", press **Install certificate** and approve both prompts.
   Recording is refused until the certificate is trusted, so your apps never see certificate errors.
2. Switch on the apps to record (Running or Installed tab). Star apps to keep them as favourites.
3. Press **Record**, use your app, press **Stop** (restores the system proxy).
4. Optional (macOS): **Tools > Settings > Proxy permission > Don't ask for my password** installs `/etc/sudoers.d/trafficscope` (checked with visudo) allowing only the four `networksetup` proxy on/off commands, so Record and Stop stop asking for your password. **Remove permission** deletes it.

## Features
| Area | What you get |
|---|---|
| Capture | Only switched-on apps, only from the moment you press Record. Other apps pass through untouched. Pause/Resume, Clear. |
| List | Method badge, status dot (red for errors), host and path, tags (Mock, Edited, Replay, Resent, Tunnel), app, type, size, time. Filter chips: All, Errors, JSON, Images, Slow > 1 s. Search URL/app/status/body. |
| Inspector | Body (JSON tree, Expand/Collapse all, Raw toggle, image preview, decoded form), Headers (with JWT decode), Params (query + form), Cookies, Timing, Raw. Small copy icon on every pane. |
| Postman | Tools > Settings > Postman: paste your API key once (stored in macOS Keychain / Windows DPAPI), pick a workspace and collection. **Send to Postman** saves the request (method, URL, headers, JSON/form body) into that collection via the Postman API and opens Postman; open it there and press Send. Respects "mask secrets". |
| Actions | Copy cURL or code (Python, JavaScript, C#), Replay, Edit and resend, Compare two requests (Ctrl/Cmd+click), Save and Open sessions as HAR. Right-click a row for shortcuts. |
| Tools (top-bar icons) | Mock responses, Breakpoints (edit request before send / response before the app), Map Local (answer from a file) and Map Remote (send to another server), Settings: decrypt-only hosts, slow network delay, mask secrets, Light/Dark/System theme, Reset all. |
| Footer | Recording state, app/request counts, total size, other-app connections ignored, active rules, selected URL. |

## Build and test
```
export PATH=$HOME/.dotnet:$PATH
dotnet test          # 80 tests (Postman API tested against a local fake): unit, live proxy integration (local server + curl), headless UI
./publish.sh         # dist/TrafficScope-<ver>-macOS-AppleSilicon.dmg, -Windows-Setup.exe, -Windows-Portable.zip, SHA256SUMS
```
Stack: .NET 10, Avalonia 11, Titanium.Web.Proxy. Icons: `tools/IconGen`. Tests can use `TS_DATA_DIR` to avoid touching real settings.

## Known limits
- Capture uses the **system proxy** plus process lookup. Apps that ignore the system proxy will not appear (OS-level capture is the planned next step).
- Certificate-pinned apps fail while decrypted: add only your own hosts to "Decrypt only these hosts".
- Slow network adds latency per request; it does not limit bandwidth.
- HTTP/1.1 towards the client; WebSocket frames and SSE bodies are not shown.
- Postman has no deep link to open or run a request, so the saved request must be opened and sent from the Postman sidebar. Not yet tested against the real Postman API.
- Windows build is cross-compiled and has not been run on Windows. Mac app is ad-hoc signed, not notarized.

## Responsible use
TrafficScope decrypts HTTPS by installing its own root certificate on your computer. Use it only on devices and traffic
you own or are authorised to inspect. Recorded traffic stays on your machine; only **Send to Postman** uploads a request,
to your own Postman workspace. Remove the certificate when you no longer need it (Windows uninstaller offers this; on
macOS delete "TrafficScope Root CA" in Keychain Access).

## Third-party components
.NET (MIT), Avalonia (MIT), Titanium.Web.Proxy (MIT), CommunityToolkit.Mvvm (MIT), SkiaSharp (MIT, icon tool only).

## License
MIT, see [LICENSE](LICENSE).
