# Usage4Claude

`net-csharp-1` is the Windows-first rewrite branch for Usage4Claude.

The rewrite targets:

- C# and .NET 8
- WinUI 3 for the Windows host
- MSIX packaged deployment for the first Windows release
- A shared C# core that a future macOS C# host can reuse
- Product parity with the previous Swift macOS implementation

The current branch intentionally does not keep the Swift app or Xcode project.
Historical docs, screenshots, and website assets remain in the repository while
parity work is in progress.

## Projects

- `src/Usage4Claude.Core`: portable usage models and API wire transforms.
- `src/Usage4Claude.Infrastructure`: HTTP clients and endpoint/header behavior.
- `src/Usage4Claude.WinUI`: packaged WinUI app and Windows-specific probes.
- `tests/Usage4Claude.Core.Tests`: shared core response-mapping tests.

## Rewrite order

1. Spike 1: validate Claude/Codex authentication and usage endpoints from Windows.
2. Spike 2: validate Windows notification-area residency and detail/menu behavior.
3. Spike 3: validate MSIX system integration, credential storage, notifications,
   launch at login, and update/distribution decisions.

See `docs/WINDOWS_REWRITE_SPIKES.md` for the current spike acceptance checks.

## Current Windows rewrite

The WinUI app keeps the validated probe surface while the Windows product host is
being built out. It can currently:

- Accept a manually pasted Claude `sessionKey` fallback.
- Open Claude login in WebView2 and capture the Claude session cookie.
- Open ChatGPT login in WebView2 and capture direct or chunked Codex session cookies.
- Probe Claude and Codex usage through the authenticated WebView2 browser context.
- Refresh the signed-in browser provider automatically after cookie capture and
  from the notification-area detail surface.
- Feed the latest browser-probed usage snapshots into the notification-area
  detail surface and quota icon.
- Keep the WebView2 fetch bridge and browser usage refresh parsing in Windows
  browser services while shared usage snapshots stay in the portable core.
- Track signed-in provider session availability in WinUI state so tray refresh
  and empty-state rendering no longer rely only on the last login button clicked.
- Restore surviving WebView2 browser sessions on app activation and keep the
  active browser provider refreshed on a conservative resident cadence.
- Stay resident in the notification area with a compact detail surface, a
  right-click menu, and a runtime quota-icon swap test.
- Probe packaged Credential Locker storage, a local app notification, and launch-at-login startup task enablement.

Build the branch with:

```powershell
dotnet build Usage4Claude.sln
```

Run `Usage4Claude.WinUI` from Visual Studio or the packaged debugging profile
while Spike 1 is being validated with real accounts.
