using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Windows.ApplicationModel;
using Usage4Claude.Core.Claude;
using Usage4Claude.Core.Usage;
using Usage4Claude.Infrastructure.Claude;
using Usage4Claude.Infrastructure.Codex;
using Usage4Claude.WinUI.Integration;
using Usage4Claude.WinUI.State;
using Usage4Claude.WinUI.Tray;

namespace Usage4Claude.WinUI;

public sealed partial class MainWindow : Window
{
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(45) };
    private readonly ClaudeUsageClient _claudeClient;
    private readonly CodexUsageClient _codexClient;
    private readonly CredentialLockerProbe _credentialLocker = new();
    private readonly UsageStateStore _usageState = new();
    private readonly DispatcherTimer _cookieTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly TrayDetailWindow _trayDetailWindow;
    private readonly TrayIconHost _trayIconHost;

    private LoginTarget _loginTarget;
    private bool _isQuitting;
    private string? _claudeSessionKey;
    private string? _codexCookieHeader;

    public MainWindow()
    {
        InitializeComponent();
        _claudeClient = new ClaudeUsageClient(_httpClient);
        _codexClient = new CodexUsageClient(_httpClient);
        _cookieTimer.Tick += CookieTimer_Tick;
        AppWindow.SetIcon(Usage4ClaudeIconPath);
        _trayDetailWindow = new TrayDetailWindow(Usage4ClaudeIconPath, ShowProbeWindow);
        _trayIconHost = new TrayIconHost(
            this,
            Usage4ClaudeIconPath,
            ToggleDetailWindowFromTray,
            ShowProbeWindow,
            QuitFromTray);
        _usageState.Changed += UsageState_Changed;
        _trayDetailWindow.UpdateUsage(_usageState.Current);
        AppWindow.Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
    }

    private async void StartClaudeLogin_Click(object sender, RoutedEventArgs e)
    {
        _loginTarget = LoginTarget.Claude;
        BrowserModeText.Text = "Claude login";
        SetStatus("Opening Claude", "Complete login in WebView2. Cookie capture starts after navigation.", InfoBarSeverity.Informational);
        await NavigateAsync("https://claude.ai/login");
    }

    private async void StartCodexLogin_Click(object sender, RoutedEventArgs e)
    {
        _loginTarget = LoginTarget.Codex;
        BrowserModeText.Text = "Codex login";
        SetStatus("Opening ChatGPT", "Complete login in WebView2. The probe will capture the session cookie.", InfoBarSeverity.Informational);
        await NavigateAsync("https://chatgpt.com/auth/login");
    }

    private async void LoginWebView_NavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (!args.IsSuccess)
        {
            SetStatus("Navigation failed", $"WebView2 reported {args.WebErrorStatus}.", InfoBarSeverity.Warning);
            return;
        }

        _cookieTimer.Start();
        await CaptureCookiesAsync();
    }

    private async void CookieTimer_Tick(object? sender, object e)
    {
        await CaptureCookiesAsync();
    }

    private async void CaptureCookies_Click(object sender, RoutedEventArgs e)
    {
        await CaptureCookiesAsync(showNoCookieMessage: true);
    }

    private async void ProbeClaude_Click(object sender, RoutedEventArgs e)
    {
        await RunProbeAsync("Claude browser", ProbeClaudeInBrowserAsync);
    }

    private async void ProbeClaudeHttp_Click(object sender, RoutedEventArgs e)
    {
        var sessionKey = ClaudeSessionKeyBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(sessionKey))
        {
            sessionKey = _claudeSessionKey ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(sessionKey))
        {
            SetStatus("Claude credentials missing", "Paste a session key or complete Claude WebView2 login first.", InfoBarSeverity.Warning);
            return;
        }

        await RunProbeAsync("Claude HttpClient", async () =>
        {
            var organizations = await _claudeClient.GetOrganizationsAsync(sessionKey);
            var organizationId = ClaudeOrganizationBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(organizationId))
            {
                organizationId = organizations.FirstOrDefault()?.Uuid ?? string.Empty;
                ClaudeOrganizationBox.Text = organizationId;
            }

            if (string.IsNullOrWhiteSpace(organizationId))
            {
                throw new InvalidOperationException("Claude returned no organizations for the supplied session key.");
            }

            var usage = await _claudeClient.GetUsageAsync(organizationId, sessionKey);
            _usageState.SetClaude(usage);
            return FormatClaudeProbe(organizations, usage);
        });
    }

    private async void ProbeCodex_Click(object sender, RoutedEventArgs e)
    {
        await RunProbeAsync("Codex browser", ProbeCodexInBrowserAsync);
    }

    private async void ProbeCodexHttp_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_codexCookieHeader))
        {
            await CaptureCookiesAsync(showNoCookieMessage: true);
        }

        if (string.IsNullOrWhiteSpace(_codexCookieHeader))
        {
            SetStatus("Codex credentials missing", "Complete Codex WebView2 login before probing usage.", InfoBarSeverity.Warning);
            return;
        }

        await RunProbeAsync("Codex HttpClient", async () =>
        {
            var session = await _codexClient.GetSessionAsync(_codexCookieHeader);
            if (string.IsNullOrWhiteSpace(session.AccessToken))
            {
                throw new InvalidOperationException("ChatGPT session response did not contain an access token.");
            }

            var usage = await _codexClient.GetUsageAsync(session.AccessToken);
            _usageState.SetCodex(usage);
            return FormatCodexProbe(session.User?.Email, session.User?.Name, usage);
        });
    }

    private async void ClearWebViewData_Click(object sender, RoutedEventArgs e)
    {
        await LoginWebView.EnsureCoreWebView2Async();
        await LoginWebView.CoreWebView2.Profile.ClearBrowsingDataAsync();
        _claudeSessionKey = null;
        _codexCookieHeader = null;
        ClaudeSessionKeyBox.Text = string.Empty;
        SetStatus("WebView data cleared", "Start a fresh browser login for the next probe.", InfoBarSeverity.Success);
    }

    private void StoreClaudeCredential_Click(object sender, RoutedEventArgs e)
    {
        var sessionKey = ClaudeSessionKeyBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(sessionKey))
        {
            SetStatus("Credential not stored", "Paste or capture a Claude session key first.", InfoBarSeverity.Warning);
            return;
        }

        _credentialLocker.SaveClaudeSessionKey(sessionKey);
        SetStatus("Credential stored", "Credential Locker accepted the Claude session key probe value.", InfoBarSeverity.Success);
    }

    private void LoadClaudeCredential_Click(object sender, RoutedEventArgs e)
    {
        var sessionKey = _credentialLocker.LoadClaudeSessionKey();
        if (string.IsNullOrWhiteSpace(sessionKey))
        {
            SetStatus("Credential not found", "Credential Locker has no Claude session key probe value yet.", InfoBarSeverity.Warning);
            return;
        }

        _claudeSessionKey = sessionKey;
        ClaudeSessionKeyBox.Text = sessionKey;
        SetStatus("Credential loaded", "Credential Locker returned the Claude session key probe value.", InfoBarSeverity.Success);
    }

    private void SendNotification_Click(object sender, RoutedEventArgs e)
    {
        var notification = new AppNotificationBuilder()
            .AddArgument("action", "openProbe")
            .AddText("Usage4Claude notification probe")
            .AddText("The packaged WinUI host can send local quota notifications.")
            .BuildNotification();

        AppNotificationManager.Default.Show(notification);
        SetStatus("Notification requested", "Check the Windows notification surface for the Spike 3 test message.", InfoBarSeverity.Success);
    }

    private async void EnableStartup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var startupTask = await StartupTask.GetAsync(StartupTaskId);
            var state = startupTask.State == StartupTaskState.Disabled
                ? await startupTask.RequestEnableAsync()
                : startupTask.State;
            SetStatus("Startup task probe", $"Launch-at-login state: {state}.", InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            SetStatus("Startup task unavailable", exception.Message, InfoBarSeverity.Warning);
        }
    }

    private async Task NavigateAsync(string url)
    {
        await LoginWebView.EnsureCoreWebView2Async();
        LoginWebView.Source = new Uri(url);
    }

    private async Task<string> ProbeClaudeInBrowserAsync()
    {
        await EnsureBrowserOriginAsync(LoginTarget.Claude, "https://claude.ai/settings/usage");
        var organizationsJson = await ExecuteFetchScriptAsync("/api/organizations");
        var organizations = JsonSerializer.Deserialize<List<ClaudeOrganizationResponse>>(organizationsJson)
            ?? throw new InvalidOperationException("Claude browser probe returned no organizations.");
        var organizationId = ClaudeOrganizationBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(organizationId))
        {
            organizationId = organizations.FirstOrDefault()?.Uuid ?? string.Empty;
            ClaudeOrganizationBox.Text = organizationId;
        }

        if (string.IsNullOrWhiteSpace(organizationId))
        {
            throw new InvalidOperationException("Claude browser probe returned no organization UUID.");
        }

        var escapedOrganizationId = Uri.EscapeDataString(organizationId);
        var usageJson = await ExecuteFetchScriptAsync($"/api/organizations/{escapedOrganizationId}/usage");
        var extraJson = await ExecuteFetchScriptAsync(
            $"/api/organizations/{escapedOrganizationId}/overage_spend_limit",
            allowFailure: true);
        var usage = DeserializeBrowserPayload<ClaudeUsageResponse>(usageJson).ToSnapshot(
            TryDeserializeBrowserPayload<ClaudeExtraUsageResponse>(extraJson)?.ToSnapshot());
        _usageState.SetClaude(usage);
        return FormatClaudeProbe(organizations.Select(value => value.ToOrganization()).ToList(), usage);
    }

    private async Task<string> ProbeCodexInBrowserAsync()
    {
        await EnsureBrowserOriginAsync(LoginTarget.Codex, "https://chatgpt.com/");
        var sessionJson = await ExecuteFetchScriptAsync("/api/auth/session");
        var session = DeserializeBrowserPayload<Core.Codex.CodexSessionResponse>(sessionJson);
        if (string.IsNullOrWhiteSpace(session.AccessToken))
        {
            throw new InvalidOperationException("ChatGPT browser session response did not contain an access token.");
        }

        var usageJson = await ExecuteFetchScriptAsync(
            "/backend-api/wham/usage",
            bearerToken: session.AccessToken);
        var usage = DeserializeBrowserPayload<Core.Codex.CodexUsageResponse>(usageJson)
            .ToSnapshot(DateTimeOffset.UtcNow);
        _usageState.SetCodex(usage);
        return FormatCodexProbe(session.User?.Email, session.User?.Name, usage);
    }

    private async Task CaptureCookiesAsync(bool showNoCookieMessage = false)
    {
        if (LoginWebView.CoreWebView2 is null || _loginTarget == LoginTarget.None)
        {
            return;
        }

        var uri = _loginTarget == LoginTarget.Claude ? "https://claude.ai" : "https://chatgpt.com";
        var cookies = await LoginWebView.CoreWebView2.CookieManager.GetCookiesAsync(uri);

        if (_loginTarget == LoginTarget.Claude)
        {
            var sessionCookie = cookies.FirstOrDefault(cookie =>
                cookie.Name == "sessionKey" &&
                cookie.Domain.Contains("claude.ai", StringComparison.OrdinalIgnoreCase));
            if (sessionCookie is not null)
            {
                _claudeSessionKey = sessionCookie.Value;
                ClaudeSessionKeyBox.Text = sessionCookie.Value;
                _cookieTimer.Stop();
                SetStatus("Claude cookie captured", "Probe Claude usage or keep the manual key as a fallback.", InfoBarSeverity.Success);
                return;
            }
        }
        else
        {
            var chatGptCookies = cookies
                .Where(cookie => cookie.Domain.Contains("chatgpt.com", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var sessionToken = ExtractChunkedSessionToken(chatGptCookies);
            if (!string.IsNullOrWhiteSpace(sessionToken))
            {
                _codexCookieHeader = string.Join("; ", chatGptCookies.Select(cookie => $"{cookie.Name}={cookie.Value}"));
                _cookieTimer.Stop();
                SetStatus("Codex cookie captured", "Probe Codex usage while the captured ChatGPT session is fresh.", InfoBarSeverity.Success);
                return;
            }
        }

        if (showNoCookieMessage)
        {
            SetStatus("Cookie not found", $"No {_loginTarget} session cookie is available yet.", InfoBarSeverity.Warning);
        }
    }

    private async Task EnsureBrowserOriginAsync(LoginTarget target, string url)
    {
        await LoginWebView.EnsureCoreWebView2Async();
        var sourceHost = LoginWebView.Source?.Host;
        var expectedHost = new Uri(url).Host;
        if (string.Equals(sourceHost, expectedHost, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _loginTarget = target;
        await NavigateAsync(url);
        throw new InvalidOperationException($"Browser moved to {expectedHost}. Run the browser probe again after the page finishes loading.");
    }

    private async Task<string> ExecuteFetchScriptAsync(
        string path,
        string? bearerToken = null,
        bool allowFailure = false)
    {
        if (LoginWebView.CoreWebView2 is null)
        {
            throw new InvalidOperationException("WebView2 is not initialized.");
        }

        var pathJson = JsonSerializer.Serialize(path);
        var bearerJson = JsonSerializer.Serialize(bearerToken);
        var probeId = Guid.NewGuid().ToString("N");
        var probeIdJson = JsonSerializer.Serialize(probeId);
        var rawResult = await ExecuteBrowserProbeAsync(
            probeId,
            $$"""
            (() => {
              const id = {{probeIdJson}};
              const send = payload => window.chrome.webview.postMessage({ id, ...payload });
              (async () => {
                try {
                  const bearer = {{bearerJson}};
                  const headers = bearer ? { authorization: `Bearer ${bearer}` } : {};
                  const response = await fetch({{pathJson}}, {
                    method: "GET",
                    credentials: "include",
                    headers
                  });
                  const body = await response.text();
                  send({
                    ok: response.ok,
                    status: response.status,
                    contentType: response.headers.get("content-type"),
                    body
                  });
                } catch (error) {
                  send({
                    ok: false,
                    status: 0,
                    contentType: null,
                    body: error instanceof Error ? `${error.name}: ${error.message}` : String(error)
                  });
                }
              })();
              return "probe-started";
            })()
            """);
        var payload = DeserializeFetchPayload(rawResult);

        if (!payload.Ok && !allowFailure)
        {
            throw new InvalidOperationException(
                $"Browser fetch returned HTTP {payload.Status}. Content-Type: {payload.ContentType ?? "unknown"}. Body preview: {Preview(payload.Body)}");
        }

        return payload.Ok
            ? payload.Body ?? throw new InvalidOperationException("Browser fetch returned an empty body.")
            : string.Empty;
    }

    private static T DeserializeBrowserPayload<T>(string json) =>
        JsonSerializer.Deserialize<T>(json)
        ?? throw new InvalidOperationException($"Browser payload did not deserialize as {typeof(T).Name}.");

    private static BrowserFetchPayload DeserializeFetchPayload(string rawResult)
    {
        using var document = JsonDocument.Parse(rawResult);
        var payloadJson = document.RootElement.ValueKind == JsonValueKind.String
            ? document.RootElement.GetString()
            : document.RootElement.GetRawText();

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            throw new InvalidOperationException("WebView2 fetch probe returned no script result.");
        }

        return JsonSerializer.Deserialize<BrowserFetchPayload>(payloadJson)
            ?? throw new InvalidOperationException("WebView2 fetch probe returned no response payload.");
    }

    private async Task<string> ExecuteBrowserProbeAsync(string probeId, string script)
    {
        if (LoginWebView.CoreWebView2 is null)
        {
            throw new InvalidOperationException("WebView2 is not initialized.");
        }

        var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        void HandleMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            try
            {
                using var message = JsonDocument.Parse(args.WebMessageAsJson);
                if (!message.RootElement.TryGetProperty("id", out var id) ||
                    id.GetString() != probeId)
                {
                    return;
                }

                result.TrySetResult(message.RootElement.GetRawText());
            }
            catch (JsonException exception)
            {
                result.TrySetException(exception);
            }
        }

        LoginWebView.CoreWebView2.WebMessageReceived += HandleMessage;
        try
        {
            await LoginWebView.CoreWebView2.ExecuteScriptAsync(script);
            return await result.Task.WaitAsync(TimeSpan.FromSeconds(45));
        }
        finally
        {
            LoginWebView.CoreWebView2.WebMessageReceived -= HandleMessage;
        }
    }

    private static T? TryDeserializeBrowserPayload<T>(string json) =>
        string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json);

    private static string Preview(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "empty";
        }

        var compact = text.ReplaceLineEndings(" ");
        return compact.Length <= 240 ? compact : $"{compact[..240]}...";
    }

    private async Task RunProbeAsync(string provider, Func<Task<string>> action)
    {
        SetStatus($"Probing {provider}", "Calling the current usage endpoints.", InfoBarSeverity.Informational);
        try
        {
            ProbeResultBox.Text = await action();
            SetStatus($"{provider} probe succeeded", "Usage data reached the tray snapshot state.", InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            ProbeResultBox.Text = exception.ToString();
            SetStatus($"{provider} probe failed", exception.Message, InfoBarSeverity.Error);
        }
    }

    private static string? ExtractChunkedSessionToken(IReadOnlyList<CoreWebView2Cookie> cookies)
    {
        foreach (var baseName in new[] { "__Secure-next-auth.session-token", "next-auth.session-token" })
        {
            var direct = cookies.FirstOrDefault(cookie => cookie.Name == baseName);
            if (direct is not null)
            {
                return direct.Value;
            }

            var chunks = cookies
                .Where(cookie => cookie.Name.StartsWith($"{baseName}.", StringComparison.Ordinal))
                .Select(cookie => new
                {
                    Cookie = cookie,
                    Index = ParseChunkIndex(cookie.Name, baseName),
                })
                .Where(value => value.Index is not null)
                .OrderBy(value => value.Index)
                .Select(value => value.Cookie.Value)
                .ToList();
            if (chunks.Count > 0)
            {
                return string.Concat(chunks);
            }
        }

        return null;
    }

    private static int? ParseChunkIndex(string cookieName, string baseName)
    {
        var suffix = cookieName[(baseName.Length + 1)..];
        return int.TryParse(suffix, out var index) ? index : null;
    }

    private static string FormatClaudeProbe(
        IReadOnlyList<ClaudeOrganization> organizations,
        ClaudeUsageSnapshot usage)
    {
        var result = new StringBuilder();
        result.AppendLine($"Organizations: {organizations.Count}");
        foreach (var organization in organizations)
        {
            result.AppendLine($"- {organization.Name} ({organization.Uuid})");
        }

        result.AppendLine();
        result.AppendLine($"5-hour: {FormatLimit(usage.FiveHour)}");
        result.AppendLine($"7-day: {FormatLimit(usage.SevenDay)}");
        result.AppendLine($"Opus weekly: {FormatLimit(usage.OpusWeekly)}");
        result.AppendLine($"Sonnet weekly: {FormatLimit(usage.SonnetWeekly)}");
        result.AppendLine(
            usage.ExtraUsage is null
                ? "Extra usage: unavailable"
                : $"Extra usage: enabled={usage.ExtraUsage.Enabled}, used={usage.ExtraUsage.Used}, limit={usage.ExtraUsage.Limit}, currency={usage.ExtraUsage.Currency}");
        return result.ToString();
    }

    private static string FormatCodexProbe(string? email, string? name, CodexUsageSnapshot usage)
    {
        var result = new StringBuilder();
        result.AppendLine($"User: {name ?? "unknown"} <{email ?? "unknown"}>");
        result.AppendLine($"Primary: {FormatLimit(usage.Primary)}");
        result.AppendLine($"Secondary: {FormatLimit(usage.Secondary)}");
        result.AppendLine(
            usage.Credits is null
                ? "Credits: unavailable"
                : $"Credits: enabled={usage.Credits.Enabled}, unlimited={usage.Credits.Unlimited}, balance={usage.Credits.Balance}");
        return result.ToString();
    }

    private static string FormatLimit(UsageLimit? limit) =>
        limit is null
            ? "unavailable"
            : $"{limit.Percentage:0.##}% resets at {limit.ResetsAt?.ToString("u") ?? "not started"}";

    private void SetStatus(string title, string message, InfoBarSeverity severity)
    {
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _cookieTimer.Stop();
        _usageState.Changed -= UsageState_Changed;
        _trayDetailWindow.Close();
        _trayIconHost.Dispose();
        _httpClient.Dispose();
    }

    private void MainWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_isQuitting || !KeepInTraySwitch.IsOn)
        {
            return;
        }

        args.Cancel = true;
        AppWindow.Hide();
    }

    private void ToggleDetailWindowFromTray()
    {
        if (_trayDetailWindow.AppWindow.IsVisible)
        {
            _trayDetailWindow.HideDetail();
            return;
        }

        RefreshTraySurfaces();
        _trayDetailWindow.AppWindow.Show();
        _trayDetailWindow.Activate();
    }

    private void ShowProbeWindow()
    {
        AppWindow.Show();
        Activate();
    }

    private void QuitFromTray()
    {
        _isQuitting = true;
        _trayDetailWindow.Close();
        _trayIconHost.Dispose();
        Close();
    }

    private void UsageState_Changed(object? sender, UsageState state)
    {
        RefreshTraySurfaces();
    }

    private void RefreshTraySurfaces()
    {
        var state = _usageState.Current;
        _trayDetailWindow.UpdateUsage(state);
        _trayIconHost.UpdateUsage(state);
    }

    private static string Usage4ClaudeIconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "Usage4Claude.ico");

    private const string StartupTaskId = "Usage4ClaudeStartup";

    private enum LoginTarget
    {
        None,
        Claude,
        Codex,
    }

    private sealed class BrowserFetchPayload
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; init; }

        [JsonPropertyName("status")]
        public int Status { get; init; }

        [JsonPropertyName("contentType")]
        public string? ContentType { get; init; }

        [JsonPropertyName("body")]
        public string? Body { get; init; }
    }
}
