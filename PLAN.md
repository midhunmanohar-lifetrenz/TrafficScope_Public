# TrafficScope: plan and design (working name)

A Fiddler/Proxyman-class HTTP(S) debugging proxy for macOS and Windows, with first-class **per-application capture**: pick one or more running/installed apps, and only their traffic is recorded.

Research basis: web search on competitors, mitmproxy's local-capture docs and Apple developer forums. Gaps are flagged under "Unverified".

---

## 1. Purpose and market

**What these tools do:** act as a man-in-the-middle proxy. The app's HTTPS connection is terminated by the tool using a locally trusted root CA, then re-encrypted to the server. This lets you see and edit URL, headers, request body and response body, timing, and replay requests.

**Typical uses:** debugging your own app's API calls, reverse-engineering third-party API behaviour, QA repro, mocking/map-local, throttling, security testing.

| Tool | Platforms | Model | Strengths | Gaps |
|---|---|---|---|---|
| Fiddler Classic / Everywhere | Win (Classic); Win/Mac/Linux (Everywhere) | Freemium/paid | Huge ecosystem, rules, scripting | Process filter is by system proxy "capture", UI dated |
| Charles | Mac/Win/Linux (Java) | Paid | Mature, throttling, map local | Heavy, dated UI, system-proxy only |
| Proxyman | Mac (native); Win/Linux versions exist or are announced (verify) | Freemium | Best UI, scripting, map/breakpoints, iOS device and simulator workflows | Mac-first |
| mitmproxy / mitmweb | All | Free, MIT | Scriptable, **local capture by process** on Mac/Win/Linux | CLI/web UI, no polished desktop UX, no favourites |
| HTTP Toolkit | All | Freemium | Easy "intercept this app" flows | Fewer pro features |
| Wireshark | All | Free | Packet-level | No HTTPS body without keys; not request-centric |

**Opportunity (your differentiator):** most GUI tools capture *everything* through the system proxy and make you filter by host. Per-process capture exists in mitmproxy but has no good GUI. A polished **"choose apps, capture only them, remember favourites"** experience on both OSes is a clear gap. It also solves Electron/Node/Go apps that ignore the system proxy.

---

## 2. Requirements (from you)

1. Mac + Windows desktop app.
2. Capture all HTTP/HTTPS traffic with full request and response bodies.
3. **App picker:** list of running apps and installed apps; select one or **many**.
4. **Capture starts from the moment of selection** (no history from before). Selection kept in memory for the session.
5. **Favourites:** save app list across launches, one click to re-enable.
6. Use case: inspect your own Mac desktop app's traffic (plus others).

---

## 3. How per-app capture actually works (core technical risk)

An HTTP proxy alone does not know which process sent a request. Two approaches:

### A. Transparent interception at OS level (recommended)
| OS | Mechanism | Process identification |
|---|---|---|
| macOS | **System Extension with `NETransparentProxyProvider`** (Network Extension). Flows are handed to your extension, which forwards them to the local proxy engine. mitmproxy's macOS local capture uses this same family of tech. | Each flow carries `sourceAppAuditToken` → `audit_token_to_pid` + code-signing APIs give pid, path, signing ID. Do not trust bundle ID alone. Pair with Endpoint Security if you need "responsible process" (e.g., WebKit helpers). |
| Windows | **WinDivert** redirector (mitmproxy's `windows-redirector.exe` approach) or a **WFP callout/driver**. | WinDivert network layer has **no PID**. Map connections to PIDs via the flow/socket layer or `GetExtendedTcpTable`/ETW, then filter. |

Strengths: captures apps that ignore system proxy; true per-process filtering; no per-app config.
Costs: Apple entitlements, notarization, user approval in System Settings; Windows needs admin and a signed driver.

### B. Fallback: system proxy + attribution after the fact
Set the OS proxy to the local engine; for each incoming connection look up the client socket's owning PID (`lsof`/`libproc` on Mac, `GetExtendedTcpTable` on Windows). Works for apps that honour system proxy only. Ship as the **MVP** and "compatibility mode".

### C. Launch-through-proxy (Mac-only trick)
Launch the selected app with proxy env vars / `--proxy-server`. Fragile; use only as a helper for Electron/Chromium apps.

**Decision:** MVP = B. v1 = A. Keep the engine independent of the capture method so both plug in.

---

## 4. Proposed architecture

```
┌──────────────────────────── UI (Avalonia, .NET) ────────────────────────────┐
│ App picker · Favourites · Session list · Request/Response inspector · Filters│
└───────────────▲─────────────────────────────────────────────────────────────┘
                │ gRPC / local named pipe (JSON events)
┌───────────────┴──────────────┐      ┌───────────────────────────────────────┐
│ Core engine (.NET)           │◄─────│ Capture providers                     │
│ - TLS MITM + HTTP/1.1,2,WS   │      │ macOS: Swift System Extension (NE)    │
│ - Cert authority             │      │ Windows: WinDivert redirector         │
│ - Flow store (SQLite + files)│      │ Fallback: system-proxy + PID lookup   │
│ - Rules: breakpoint/map/mock │      └───────────────────────────────────────┘
│ - Process registry + filter  │
└──────────────────────────────┘
```

**Stack options**
- **Recommended:** Avalonia UI + .NET 9 engine (you already shipped JSON Converter Pro on Avalonia, so reuse skills). Engine library: Titanium.Web.Proxy, or write on `SslStream` + HTTP/2 via Kestrel components. Native helpers: Swift (Mac extension), C/Rust (Windows redirector).
- **Alternative:** Rust core embedding `mitmproxy_rs` (MIT) which already provides local capture on Mac/Win, plus Tauri UI. Faster to correct per-app capture, steeper for your team.
- **Not recommended:** Electron UI (heavy for a high-volume list), unless team is web-only.

---

## 5. Key features and design

### 5.1 App picker (your headline feature)
- **Running tab:** live list of processes with GUI/app identity, icon, name, pid(s), path, signing ID. Group helper processes under their parent app (Chrome Helper, Electron helpers) using the process tree/responsible process.
- **Installed tab:** `/Applications`, `~/Applications`, and Launch Services on Mac; Start Menu/registry uninstall keys/UWP packages on Windows. Matched by bundle ID / exe path, so traffic is captured when the app **launches later**.
- **Multi-select** with checkboxes; search; "Select all running"; ★ to favourite.
- **Capture-from-now:** pressing Start records a timestamp T0. Only flows whose connection **starts after T0** from selected apps are stored. Selected-app set held in memory by the engine; changing it applies immediately without restart.
- **Favourites:** persisted JSON (`favorites.json`) keyed by bundle ID/exe path, optional "auto-enable at launch" and "capture child processes" toggles.
- **Presets:** named groups ("My app + backend helper").
- Own process and the proxy engine are always excluded to avoid loops.

### 5.2 Traffic list and inspector
- Columns: #, app icon/name, method, host, path, status, size, time, protocol.
- Inspector tabs: Headers, Query, Cookies, **Request body**, **Response body**, Timing, TLS info, Raw.
- Body renderers: JSON tree, XML, HTML, form, multipart, images, protobuf (with .proto import), gzip/br/deflate auto-decode, hex view, WebSocket frames, SSE.
- **One-click Copy buttons** (no menus needed) on every inspector pane:
  - **Copy Request** (full raw: request line, headers, body) and **Copy Response** (status line, headers, body), placed in the toolbar of each tab.
  - Separate small copy buttons for URL, Headers, Request body, Response body, and a "Copy as cURL" button.
  - Body copy uses the decoded form (gzip/br already decompressed, JSON pretty-printed) with a toggle for "raw/as-sent"; binary bodies copy as Base64.
  - Also available via right-click on a row in the list and keyboard shortcuts (Cmd/Ctrl+Shift+C = request, Cmd/Ctrl+Shift+R = response). Brief "Copied" confirmation toast.
  - Multi-select rows → copy all requests/responses together.
  - Respect redaction settings: optional "mask Authorization/Cookie when copying" toggle.
- **UI revision:** copy is a small icon in the top-right corner of each text box (Request, Response, Headers, Body), not a large button. One extra toolbar button, **Send to Postman**, sits beside the cURL icon.
- **Send to Postman (feasibility, unverified on current Postman builds):**
  1. *Reliable default:* copy the request as cURL, launch Postman (`postman://` / `open -a Postman` / `start postman://`), and show "Paste into Import (Cmd/Ctrl+V)". Postman's Import box auto-detects raw cURL.
  2. *One-click option:* generate a Postman Collection v2.1 JSON for the selected request(s) and push it with the Postman API (`POST https://api.getpostman.com/collections`, user supplies their own API key, stored in Keychain/Credential Manager). It then appears in their workspace.
  3. *File fallback:* save the `.postman_collection.json` and reveal it for drag-and-drop import.
  No documented Postman deep link that imports a cURL string directly was found; confirm in a spike.
- Search/filter by app, host, status, content type, body text. Save/export as HAR, cURL, curl-ready copy, Postman collection.

### 5.3 Pro features (later)
Breakpoints, map local/remote, rewrite rules, throttling, repeat/edit-and-resend, compare two requests, scripting (JS/C#), mock responses, diff, port/host allow/deny list (SSL proxying list), iOS/Android device and simulator proxy setup, remote device pairing by QR.

### 5.4 Certificates and trust
- Generate per-install root CA; install into System keychain (Mac, prompts for admin) or Windows cert store.
- Per-host leaf cert signing on the fly; offer **SSL-proxying allow list** so banking/health domains are not decrypted by default.
- Firefox and Java keep own stores: provide guided installers.
- Known limit: **certificate pinning** apps will fail; show a clear "pinned, tunnelled undecrypted" indicator rather than breaking the app silently.

### 5.5 Storage
SQLite for metadata, body blobs on disk (content-addressed, size-capped, compressed), ring-buffer retention, memory-mapped viewer for large bodies.

### 5.6 Security and privacy (important for healthcare context)
Captured traffic can contain PHI, tokens and passwords. Defaults: encrypted-at-rest option, redaction rules for Authorization/Cookie in exports, auto-purge, clear banner when decryption is active, never upload anything.

---

## 6. Platform-specific delivery requirements

**macOS**
- Apple Developer Program (paid) for Developer ID signing, notarization, and the **Network Extension entitlement** (system extension). User must approve extension and allow proxy config in System Settings.
- Hardened runtime; separate privileged helper (SMAppService) for keychain/proxy settings.
- Universal binary (arm64 + x64).
- Capturing hardened/SIP-protected Apple apps is limited; NE transparent proxy still sees their network flows but some system daemons should be bypassed.

**Windows**
- Admin elevation (or service) for the redirector. WinDivert driver is signed; **check licence** (LGPLv3/GPLv2 dual) against your distribution model.
- Code-sign with EV certificate to avoid SmartScreen.
- Installer: MSIX or WiX/Inno; ARM64 build later.

---

## 7. Phased roadmap

| Phase | Weeks (est.) | Scope |
|---|---|---|
| 0. Spike | 2 | Prove per-app capture on each OS: Mac NE transparent proxy with audit-token PID; Windows redirector + PID mapping; test with Chrome, an Electron app, your own Mac app |
| 1. MVP | 6–8 | Engine (HTTP/1.1, HTTPS MITM), system-proxy mode with PID attribution, running-app picker (multi-select), traffic list, body viewer, HAR export, cert install |
| 2. v1 | 8–10 | OS-level capture on both platforms, installed-app list, favourites/presets, HTTP/2, WebSocket, filters, search, throttling, map local |
| 3. Pro | ongoing | Breakpoints, scripting, mocking, protobuf/gRPC, device pairing, team sharing of rules |

Estimates assume 2–3 engineers; Phase 0 is the go/no-go gate.

---

## 8. Risks

| Risk | Mitigation |
|---|---|
| Apple NE entitlement/approval delays | Apply on day 1; keep MVP on system proxy |
| Windows PID mapping unreliable (reported mitmproxy issue with some Node processes) | Spike early; fall back to proxy-mode |
| Cert-pinned or QUIC/HTTP3 traffic | Block/downgrade QUIC (force fallback to TCP) with a toggle; show pinned flows as tunnelled |
| Helper-process attribution (browsers, Electron) | Process tree + Endpoint Security responsible-process |
| Antivirus flags driver/redirector | Signed binaries, vendor reputation, docs |
| Legal/privacy | Decryption only for user-selected apps; warnings; no telemetry of payloads |

---

## 9. Unverified (check before committing)
- Current pricing of Proxyman, Charles, Fiddler Everywhere; whether Proxyman's Windows build is released.
- Whether Proxyman uses a Network Extension for per-app capture in its main app (it does for a separate product, TinyShield; the main app uses a system-proxy helper).
- Exact WinDivert PID-mapping technique used by mitmproxy's redirector.
- Whether `sourceAppAuditToken` is populated for every provider type on current macOS (reported nil for packet-tunnel providers).

## Sources
- [mitmproxy: Intercepting macOS applications](https://mitmproxy.org/posts/local-capture/macos/)
- [mitmproxy: Intercepting Windows applications](https://mitmproxy.org/posts/local-capture/windows/)
- [mitmproxy proxy modes](https://docs.mitmproxy.org/stable/concepts/modes/)
- [Apple forums: process ID from flow audit token](https://developer.apple.com/forums/thread/126820)
- [Apple forums: Transparent Proxy Provider and signing IDs](https://developer.apple.com/forums/thread/706446)
- [WinDivert docs](https://rust-digger.code-maven.com/src/windivert-sys-0.10.0/vendor/doc/windivert.html)
- [Proxyman TinyShield network extension](https://tinyshield.proxyman.com/docs/features/network-extension)
- [NCC Group advisory on Proxyman helper](https://nccgroup.com/us/research-blog/technical-advisory-insufficient-proxyman-helpertool-xpc-validation)
- [Fiddler alternatives (BrowserStack)](https://www.browserstack.com/guide/fiddler-alternative)
- [Proxyman vs Charles vs Wireshark](https://proxyman.com/posts/2019-10-26-Alternatives-for-charles-proxy-and-wireshark)
