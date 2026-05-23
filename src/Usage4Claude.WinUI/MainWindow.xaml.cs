using System.Text;
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
using Usage4Claude.WinUI.Browser;
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
    private readonly ProviderSessionStateStore _sessionState = new();
    private readonly DispatcherTimer _cookieTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _usageRefreshTimer = new() { Interval = TimeSpan.FromMinutes(15) };
    private readonly TrayDetailWindow _trayDetailWindow;
    private readonly TrayIconHost _trayIconHost;
    private readonly BrowserUsageRefreshService _browserRefresh;

    private LoginTarget _loginTarget;
    private bool _isQuitting;
    private bool _refreshInProgress;
    private bool _startupRecoveryStarted;
    private bool _suppressNavigationCookieCapture;
    private string? _claudeSessionKey;
    private string? _codexCookieHeader;

    public MainWindow()
    {
        InitializeComponent();
        _claudeClient = new ClaudeUsageClient(_httpClient);
        _codexClient = new CodexUsageClient(_httpClient);
        _browserRefresh = new BrowserUsageRefreshService(new WebViewBrowserFetchClient(LoginWebView));
        _cookieTimer.Tick += CookieTimer_Tick;
        _usageRefreshTimer.Tick += UsageRefreshTimer_Tick;
        AppWindow.SetIcon(Usage4ClaudeIconPath);
        _trayDetailWindow = new TrayDetailWindow(Usage4ClaudeIconPath, ShowProbeWindow, RefreshCapturedUsageAsync);
        _trayIconHost = new TrayIconHost(
            this,
            Usage4ClaudeIconPath,
            ToggleDetailWindowFromTray,
            ShowProbeWindow,
            QuitFromTray);
        _usageState.Changed += UsageState_Changed;
        _sessionState.Changed += SessionState_Changed;
        RefreshTraySurfaces();
        Activated += MainWindow_Activated;
        AppWindow.Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
    }

    private async void StartClaudeLogin_Click(object sender, RoutedEventArgs e)
    {
        _loginTarget = LoginTarget.Claude;
        _sessionState.ActivateBrowser(ProviderKind.Claude);
        BrowserModeText.Text = "Claude login";
        SetStatus("Opening Claude", "Complete login in WebView2. Cookie capture starts after navigation.", InfoBarSeverity.Informational);
        await NavigateAsync("https://claude.ai/login");
    }

    private async void StartCodexLogin_Click(object sender, RoutedEventArgs e)
    {
        _loginTarget = LoginTarget.Codex;
        _sessionState.ActivateBrowser(ProviderKind.Codex);
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

        if (_suppressNavigationCookieCapture)
        {
            return;
        }

        _cookieTimer.Start();
        await CaptureCookiesAsync();
    }

    private async void CookieTimer_Tick(object? sender, object e)
    {
        await CaptureCookiesAsync();
    }

    private async void UsageRefreshTimer_Tick(object? sender, object e)
    {
        await RefreshCapturedUsageAsync();
    }

    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (_startupRecoveryStarted)
        {
            return;
        }

        _startupRecoveryStarted = true;
        await RecoverBrowserSessionsAsync();
    }

    private async void CaptureCookies_Click(object sender, RoutedEventArgs e)
    {
        await CaptureCookiesAsync(showNoCookieMessage: true);
    }

    private async void ProbeClaude_Click(object sender, RoutedEventArgs e)
    {
        await RunBrowserRefreshAsync(LoginTarget.Claude);
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
        await RunBrowserRefreshAsync(LoginTarget.Codex);
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
        _usageRefreshTimer.Stop();
        _claudeSessionKey = null;
        _codexCookieHeader = null;
        _sessionState.ClearBrowserSessions();
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
        _sessionState.SetClaudeCredentialSession();
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
        _sessionState.SetClaudeCredentialSession();
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

    private async Task NavigateAndWaitAsync(string url)
    {
        await LoginWebView.EnsureCoreWebView2Async();
        var navigation = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void HandleNavigation(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args) =>
            navigation.TrySetResult(args);

        LoginWebView.NavigationCompleted += HandleNavigation;
        try
        {
            LoginWebView.Source = new Uri(url);
            var args = await navigation.Task.WaitAsync(TimeSpan.FromSeconds(45));
            if (!args.IsSuccess)
            {
                throw new InvalidOperationException($"WebView2 startup navigation failed with {args.WebErrorStatus}.");
            }
        }
        finally
        {
            LoginWebView.NavigationCompleted -= HandleNavigation;
        }
    }

    private async Task RecoverBrowserSessionsAsync()
    {
        try
        {
            await LoginWebView.EnsureCoreWebView2Async();

            var claudeCookies = await LoginWebView.CoreWebView2.CookieManager.GetCookiesAsync("https://claude.ai");
            var claudeSessionCookie = claudeCookies.FirstOrDefault(cookie =>
                cookie.Name == "sessionKey" &&
                cookie.Domain.Contains("claude.ai", StringComparison.OrdinalIgnoreCase));
            if (claudeSessionCookie is not null)
            {
                _claudeSessionKey = claudeSessionCookie.Value;
                ClaudeSessionKeyBox.Text = claudeSessionCookie.Value;
                _sessionState.SetClaudeWebViewSession();
            }

            var chatGptCookies = await LoginWebView.CoreWebView2.CookieManager.GetCookiesAsync("https://chatgpt.com");
            var sessionToken = ExtractChunkedSessionToken(chatGptCookies);
            if (!string.IsNullOrWhiteSpace(sessionToken))
            {
                _codexCookieHeader = string.Join("; ", chatGptCookies.Select(cookie => $"{cookie.Name}={cookie.Value}"));
                _sessionState.SetCodexWebViewSession();
            }

            var recoveredProviders = GetAvailableBrowserProviders().ToList();
            if (recoveredProviders.Count == 0)
            {
                return;
            }

            BrowserModeText.Text = recoveredProviders.Count == 1
                ? $"{recoveredProviders[0]} restored session"
                : "Restored browser sessions";
            _usageRefreshTimer.Start();
            await RefreshAvailableBrowserUsageAsync();
        }
        catch (Exception exception)
        {
            SetStatus("Startup browser recovery skipped", exception.Message, InfoBarSeverity.Warning);
        }
    }

    private async Task<string> ProbeClaudeInBrowserAsync()
    {
        await EnsureBrowserOriginAsync(LoginTarget.Claude, "https://claude.ai/settings/usage");
        var refresh = await _browserRefresh.RefreshClaudeAsync(ClaudeOrganizationBox.Text.Trim());
        ClaudeOrganizationBox.Text = refresh.OrganizationId;
        _usageState.SetClaude(refresh.Usage);
        return FormatClaudeProbe(refresh.Organizations, refresh.Usage);
    }

    private async Task<string> ProbeCodexInBrowserAsync()
    {
        await EnsureBrowserOriginAsync(LoginTarget.Codex, "https://chatgpt.com/");
        var refresh = await _browserRefresh.RefreshCodexAsync();
        _usageState.SetCodex(refresh.Usage);
        return FormatCodexProbe(refresh.Email, refresh.Name, refresh.Usage);
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
                _sessionState.SetClaudeWebViewSession();
                ClaudeSessionKeyBox.Text = sessionCookie.Value;
                _cookieTimer.Stop();
                _usageRefreshTimer.Start();
                SetStatus("Claude cookie captured", "Refreshing usage through the signed-in browser context.", InfoBarSeverity.Success);
                await RefreshCapturedUsageAsync();
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
                _sessionState.SetCodexWebViewSession();
                _cookieTimer.Stop();
                _usageRefreshTimer.Start();
                SetStatus("Codex cookie captured", "Refreshing usage through the signed-in browser context.", InfoBarSeverity.Success);
                await RefreshCapturedUsageAsync();
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
        _suppressNavigationCookieCapture = true;
        try
        {
            await NavigateAndWaitAsync(url);
        }
        finally
        {
            _suppressNavigationCookieCapture = false;
        }
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

    private Task RefreshCapturedUsageAsync()
    {
        if (!GetAvailableBrowserProviders().Any())
        {
            SetStatus("Refresh unavailable", "Open a Claude or Codex browser login first.", InfoBarSeverity.Warning);
            return Task.CompletedTask;
        }

        return RefreshAvailableBrowserUsageAsync();
    }

    private Task RunBrowserRefreshAsync(ProviderKind provider) =>
        RunBrowserRefreshAsync(provider == ProviderKind.Claude ? LoginTarget.Claude : LoginTarget.Codex);

    private async Task RunBrowserRefreshAsync(LoginTarget target)
    {
        if (_refreshInProgress)
        {
            return;
        }

        _refreshInProgress = true;
        try
        {
            await RunBrowserRefreshCoreAsync(target);
        }
        finally
        {
            _refreshInProgress = false;
        }
    }

    private async Task RefreshAvailableBrowserUsageAsync()
    {
        if (_refreshInProgress)
        {
            return;
        }

        _refreshInProgress = true;
        try
        {
            foreach (var provider in GetAvailableBrowserProviders())
            {
                await RunBrowserRefreshCoreAsync(provider);
            }
        }
        finally
        {
            _refreshInProgress = false;
        }
    }

    private async Task RunBrowserRefreshCoreAsync(LoginTarget target)
    {
        switch (target)
        {
            case LoginTarget.Claude:
                _sessionState.ActivateBrowser(ProviderKind.Claude);
                await RunProbeAsync("Claude browser refresh", ProbeClaudeInBrowserAsync);
                break;
            case LoginTarget.Codex:
                _sessionState.ActivateBrowser(ProviderKind.Codex);
                await RunProbeAsync("Codex browser refresh", ProbeCodexInBrowserAsync);
                break;
            default:
                SetStatus("Refresh unavailable", "Open a Claude or Codex browser login first.", InfoBarSeverity.Warning);
                break;
        }
    }

    private IEnumerable<LoginTarget> GetAvailableBrowserProviders()
    {
        var state = _sessionState.Current;
        if (state.Claude == ProviderSessionSource.WebViewCookie)
        {
            yield return LoginTarget.Claude;
        }

        if (state.Codex == ProviderSessionSource.WebViewCookie)
        {
            yield return LoginTarget.Codex;
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
        _usageRefreshTimer.Stop();
        _usageState.Changed -= UsageState_Changed;
        _sessionState.Changed -= SessionState_Changed;
        Activated -= MainWindow_Activated;
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

    private void SessionState_Changed(object? sender, ProviderSessionState state)
    {
        RefreshTraySurfaces();
    }

    private void RefreshTraySurfaces()
    {
        var usageState = _usageState.Current;
        _trayDetailWindow.UpdateState(usageState, _sessionState.Current);
        _trayIconHost.UpdateUsage(usageState);
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

}
