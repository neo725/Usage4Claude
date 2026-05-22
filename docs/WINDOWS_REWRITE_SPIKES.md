# Windows rewrite spikes

The `net-csharp-1` branch replaces the macOS Swift app with a Windows-first C# rewrite.
The shared projects must stay portable enough for a future macOS C# host:

- `Usage4Claude.Core` contains usage models and API wire transforms.
- `Usage4Claude.Infrastructure` contains HTTP clients and endpoint/header behavior.
- `Usage4Claude.WinUI` contains Windows UI, WebView2 login, MSIX packaging, and later tray/system integration.

## Spike order

### Spike 1: Authentication and usage endpoints

Acceptance checks:

1. Claude can be probed from a manually pasted `sessionKey`.
2. Claude WebView2 login can capture `sessionKey` and probe organizations plus usage.
3. Codex WebView2 login can capture direct or chunked ChatGPT session cookies.
4. Codex session and usage endpoints can be probed from the captured cookie set.
5. Cloudflare/HTML challenge responses are reported as probe failures instead of JSON parse noise.

Open questions to resolve with real accounts:

- Whether WebView2 receives the same cookie names as the current `WKWebView` flow.
- Whether the existing macOS-oriented browser headers remain sufficient from Windows `HttpClient`.
- Whether Codex SSR token refresh needs a Windows equivalent before full parity work starts.

Validated on May 22, 2026:

- Claude WebView2 login captured the `sessionKey` cookie and browser-backed probes returned organization and usage data.
- Codex WebView2 login captured ChatGPT session cookies and browser-backed probes returned session, rate-limit, and credits data.
- The standalone `HttpClient` replay probes returned HTTP 403 from both providers after cookie capture. The currently verified Windows path is browser-backed requests from the authenticated WebView2 context; production work must decide whether to preserve that path, add a richer browser-cookie replay strategy, or keep both as fallbacks.

### Spike 2: Notification area host

Prototype tray residency, dynamic quota icon rendering, left-click detail surface, and right-click menu behavior before porting the full detail UI.

Implemented on May 22, 2026:

- The packaged WinUI host adds and removes a Win32 notification-area icon with the existing Usage4Claude app icon.
- Left click toggles a compact borderless detail prototype and the right-click menu can open the probe window or quit the resident host.
- Closing the main probe window hides it while notification-area residency is enabled.
- The right-click `Cycle quota icon` action swaps the notification-area icon at runtime between single-quota progress prototypes and the brand icon.

Spike note:

- The Swift menu-bar renderer can lay out several provider and metric icons horizontally. A Windows notification-area icon is a small square icon slot, so the Windows product should treat the tray icon as a single compact status signal and keep multi-provider detail in the click surface.

### Spike 3: Packaged Windows integration

Prototype secure local credential storage, notifications, launch at login, and MSIX update/distribution decisions. Cross-device sync is a future storage concern and is not required by the first Windows release.

Implemented on May 22, 2026:

- `Package.appxmanifest` declares app-notification activation and the `Usage4ClaudeStartup` startup task for the MSIX host.
- The probe surface can store and reload a Claude session key through Windows Credential Locker.
- The probe surface can request a local Windows App SDK app notification.
- The probe surface can request enablement for the packaged launch-at-login startup task and report the resulting state.

Manual packaged validation still needed:

1. Store and reload a real Claude session key from Credential Locker after restarting the app.
2. Send the notification probe from a non-elevated packaged run and click it to confirm foreground activation.
3. Enable launch at login, inspect the startup state in Windows, sign out or reboot, and confirm the resident host returns.
4. Decide the first release update channel: Microsoft Store, signed sideloaded MSIX with App Installer/update feed, or a controlled internal package flow.
