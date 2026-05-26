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
    private readonly UserPreferencesStore _preferencesStore = new();
    private readonly UsageStateStore _usageState = new();
    private readonly ProviderSessionStateStore _sessionState = new();
    private readonly DispatcherTimer _cookieTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _usageRefreshTimer = new();
    private readonly TrayDetailWindow _trayDetailWindow;
    private readonly TrayIconHost _trayIconHost;
    private readonly BrowserUsageRefreshService _browserRefresh;
    private readonly Dictionary<LoginTarget, DateTimeOffset> _lastBrowserRefreshAt = new();
    private readonly Dictionary<LoginTarget, DateTimeOffset> _nextBrowserRefreshAllowedAt = new();
    private readonly Dictionary<LoginTarget, int> _browserRefreshFailureCounts = new();
    private UserPreferences _preferences = UserPreferences.Default;
    private DisplaySettings _displaySettings = DisplaySettings.Default;

    private LoginTarget _loginTarget;
    private bool _isQuitting;
    private bool _refreshInProgress;
    private bool _startupRecoveryStarted;
    private bool _suppressNavigationCookieCapture;
    private bool _syncingDisplaySettingsControls;
    private bool _syncingAccountControls;
    private DateTimeOffset _lastManualRefreshAt = DateTimeOffset.MinValue;
    private string? _claudeSessionKey;
    private string? _codexCookieHeader;

    public MainWindow()
    {
        _preferences = _preferencesStore.Load();
        _displaySettings = _preferences.DisplaySettings;
        InitializeComponent();
        _claudeClient = new ClaudeUsageClient(_httpClient);
        _codexClient = new CodexUsageClient(_httpClient);
        _browserRefresh = new BrowserUsageRefreshService(new WebViewBrowserFetchClient(LoginWebView));
        _cookieTimer.Tick += CookieTimer_Tick;
        _usageRefreshTimer.Tick += UsageRefreshTimer_Tick;
        AppWindow.SetIcon(Usage4ClaudeIconPath);
        _trayDetailWindow = new TrayDetailWindow(
            Usage4ClaudeIconPath,
            _preferences.TrayDetailTopMost,
            ShowProbeWindow,
            RefreshCapturedUsageAsync);
        _trayIconHost = new TrayIconHost(
            this,
            Usage4ClaudeIconPath,
            ToggleDetailWindowFromTray,
            ShowProbeWindow,
            QuitFromTray);
        _usageState.Changed += UsageState_Changed;
        _sessionState.Changed += SessionState_Changed;
        ApplyDisplaySettingsToControls();
        RefreshAccountControls();
        RefreshTraySurfaces();
        RefreshMainSurface();
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
        SetStatus("Opening ChatGPT", "Complete login in WebView2. Usage4Claude will capture the session cookie.", InfoBarSeverity.Informational);
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
        _usageRefreshTimer.Stop();
        await RefreshCapturedUsageAsync(RefreshTrigger.Background);
        if (GetAvailableBrowserProviders().Any())
        {
            ScheduleBackgroundRefresh();
        }
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

    private async void RefreshUsage_Click(object sender, RoutedEventArgs e)
    {
        await RefreshCapturedUsageAsync(RefreshTrigger.Manual);
    }

    private async void OpenTrayDetail_Click(object sender, RoutedEventArgs e)
    {
        RefreshTraySurfaces();
        _trayDetailWindow.ShowNearCursor();
        await RefreshCapturedUsageAsync(RefreshTrigger.DetailOpen);
    }

    private void DisplaySettings_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingDisplaySettingsControls || !IsDisplaySettingsUiReady())
        {
            return;
        }

        EnsureAtLeastOneCustomDisplayOption();
        SyncColoredThemeAvailability();
        _displaySettings = ReadDisplaySettings();
        SavePreferences();
        ApplyAppearance();
        RefreshMainSurface();
        RefreshTraySurfaces();
    }

    private void KeepInTraySwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingDisplaySettingsControls)
        {
            return;
        }

        SavePreferences();
    }

    private void AccountSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingAccountControls)
        {
            return;
        }

        if (ReferenceEquals(sender, ClaudeAccountsList) && ClaudeAccountsList.SelectedItem is AccountListItem claudeItem)
        {
            SetProfileProviderAccount(ProviderKind.Claude, claudeItem.Id);
        }
        else if (ReferenceEquals(sender, CodexAccountsList) && CodexAccountsList.SelectedItem is AccountListItem codexItem)
        {
            SetProfileProviderAccount(ProviderKind.Codex, codexItem.Id);
        }
    }

    private void ProfileSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingAccountControls || ProfilesList.SelectedItem is not ProfileListItem profileItem)
        {
            return;
        }

        SetCurrentProfile(profileItem.Id);
    }

    private void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        var currentProfile = GetCurrentProfile();
        if (currentProfile is null)
        {
            SetStatus("No profile selected", "Create or select a profile before naming it.", InfoBarSeverity.Warning);
            return;
        }

        var name = AccountAliasBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            SetStatus("Profile name required", "Enter a display name for this profile.", InfoBarSeverity.Warning);
            return;
        }

        UpdateProfile(currentProfile with { Name = name, UpdatedAt = DateTimeOffset.UtcNow });
        SetStatus("Profile saved", "The selected profile name was updated.", InfoBarSeverity.Success);
    }

    private void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        var profile = CreateProfile("New profile");
        SetCurrentProfile(profile.Id);
        SetStatus("Profile created", "Select Claude and Codex accounts for this profile.", InfoBarSeverity.Success);
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        var currentProfile = GetCurrentProfile();
        if (currentProfile is null)
        {
            SetStatus("No profile selected", "There is no current profile to delete.", InfoBarSeverity.Warning);
            return;
        }

        RemoveProfile(currentProfile);
        SetStatus("Profile removed", $"{currentProfile.Name} was removed.", InfoBarSeverity.Success);
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
        _lastBrowserRefreshAt.Clear();
        _nextBrowserRefreshAllowedAt.Clear();
        _browserRefreshFailureCounts.Clear();
        _lastManualRefreshAt = DateTimeOffset.MinValue;
        _claudeSessionKey = null;
        _codexCookieHeader = null;
        _sessionState.ClearBrowserSessions();
        ClaudeSessionKeyBox.Text = string.Empty;
        SetStatus("WebView data cleared", "Start a fresh browser login to refresh usage again.", InfoBarSeverity.Success);
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
        UpsertAccount(
            ProviderKind.Claude,
            "Claude stored credential",
            sessionKey,
            ProviderSessionSource.CredentialLocker);
        SetStatus("Credential stored", "Credential Locker saved the Claude session key.", InfoBarSeverity.Success);
    }

    private void LoadClaudeCredential_Click(object sender, RoutedEventArgs e)
    {
        var sessionKey = _credentialLocker.LoadClaudeSessionKey();
        if (string.IsNullOrWhiteSpace(sessionKey))
        {
            SetStatus("Credential not found", "Credential Locker has no Claude session key yet.", InfoBarSeverity.Warning);
            return;
        }

        _claudeSessionKey = sessionKey;
        _sessionState.SetClaudeCredentialSession();
        ClaudeSessionKeyBox.Text = sessionKey;
        UpsertAccount(
            ProviderKind.Claude,
            "Claude stored credential",
            sessionKey,
            ProviderSessionSource.CredentialLocker);
        SetStatus("Credential loaded", "Credential Locker returned the Claude session key.", InfoBarSeverity.Success);
    }

    private void SendNotification_Click(object sender, RoutedEventArgs e)
    {
        var notification = new AppNotificationBuilder()
            .AddArgument("action", "openUsage4Claude")
            .AddText("Usage4Claude notifications are ready")
            .AddText("Windows can show local quota notifications for this app.")
            .BuildNotification();

        AppNotificationManager.Default.Show(notification);
        SetStatus("Notification requested", "Check the Windows notification surface for the test message.", InfoBarSeverity.Success);
    }

    private async void EnableStartup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var startupTask = await StartupTask.GetAsync(StartupTaskId);
            var state = startupTask.State == StartupTaskState.Disabled
                ? await startupTask.RequestEnableAsync()
                : startupTask.State;
            SetStatus("Launch at login", $"Startup task state: {state}.", InfoBarSeverity.Success);
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
                UpsertAccount(
                    ProviderKind.Claude,
                    "Claude browser session",
                    claudeSessionCookie.Value,
                    ProviderSessionSource.WebViewCookie);
            }

            var chatGptCookies = await LoginWebView.CoreWebView2.CookieManager.GetCookiesAsync("https://chatgpt.com");
            var sessionToken = ExtractChunkedSessionToken(chatGptCookies);
            if (!string.IsNullOrWhiteSpace(sessionToken))
            {
                _codexCookieHeader = string.Join("; ", chatGptCookies.Select(cookie => $"{cookie.Name}={cookie.Value}"));
                _sessionState.SetCodexWebViewSession();
                UpsertAccount(
                    ProviderKind.Codex,
                    "Codex browser session",
                    sessionToken,
                    ProviderSessionSource.WebViewCookie);
            }

            var recoveredProviders = GetAvailableBrowserProviders().ToList();
            if (recoveredProviders.Count == 0)
            {
                return;
            }

            BrowserModeText.Text = recoveredProviders.Count == 1
                ? $"{recoveredProviders[0]} restored session"
                : "Restored browser sessions";
            ScheduleBackgroundRefresh();
            await RefreshAvailableBrowserUsageAsync(RefreshTrigger.Startup);
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
                UpsertAccount(
                    ProviderKind.Claude,
                    "Claude browser session",
                    sessionCookie.Value,
                    ProviderSessionSource.WebViewCookie);
                _cookieTimer.Stop();
                ScheduleBackgroundRefresh();
                SetStatus("Claude cookie captured", "Refreshing usage through the signed-in browser context.", InfoBarSeverity.Success);
                await RefreshCapturedUsageAsync(RefreshTrigger.CookieCapture);
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
                UpsertAccount(
                    ProviderKind.Codex,
                    "Codex browser session",
                    sessionToken,
                    ProviderSessionSource.WebViewCookie);
                _cookieTimer.Stop();
                ScheduleBackgroundRefresh();
                SetStatus("Codex cookie captured", "Refreshing usage through the signed-in browser context.", InfoBarSeverity.Success);
                await RefreshCapturedUsageAsync(RefreshTrigger.CookieCapture);
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

    private async Task<bool> RunProbeAsync(
        string provider,
        Func<Task<string>> action,
        bool throwOnFailure = false)
    {
        SetStatus($"Probing {provider}", "Calling the current usage endpoints.", InfoBarSeverity.Informational);
        try
        {
            ProbeResultBox.Text = await action();
            SetStatus($"{provider} succeeded", "Usage data refreshed successfully.", InfoBarSeverity.Success);
            return true;
        }
        catch (Exception exception)
        {
            ProbeResultBox.Text = exception.ToString();
            SetStatus($"{provider} failed", exception.Message, InfoBarSeverity.Error);
            if (throwOnFailure)
            {
                throw;
            }

            return false;
        }
    }

    private Task RefreshCapturedUsageAsync() =>
        RefreshCapturedUsageAsync(RefreshTrigger.Manual);

    private Task RefreshCapturedUsageAsync(RefreshTrigger trigger)
    {
        if (!GetAvailableBrowserProviders().Any())
        {
            SetStatus("Refresh unavailable", "Open a Claude or Codex browser login first.", InfoBarSeverity.Warning);
            return Task.CompletedTask;
        }

        return RefreshAvailableBrowserUsageAsync(trigger);
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

    private async Task RefreshAvailableBrowserUsageAsync(RefreshTrigger trigger)
    {
        if (_refreshInProgress)
        {
            return;
        }

        _refreshInProgress = true;
        try
        {
            if (trigger == RefreshTrigger.Manual && IsManualRefreshCoolingDown())
            {
                return;
            }

            foreach (var provider in GetAvailableBrowserProviders())
            {
                if (!ShouldRefreshProvider(provider, trigger))
                {
                    continue;
                }

                await RunBrowserRefreshCoreAsync(provider, trigger);
            }
        }
        finally
        {
            _refreshInProgress = false;
        }
    }

    private async Task RunBrowserRefreshCoreAsync(LoginTarget target, RefreshTrigger trigger = RefreshTrigger.ManualProbe)
    {
        switch (target)
        {
            case LoginTarget.Claude:
                _sessionState.ActivateBrowser(ProviderKind.Claude);
                await RunProviderRefreshAsync(target, "Claude browser refresh", ProbeClaudeInBrowserAsync, trigger);
                break;
            case LoginTarget.Codex:
                _sessionState.ActivateBrowser(ProviderKind.Codex);
                await RunProviderRefreshAsync(target, "Codex browser refresh", ProbeCodexInBrowserAsync, trigger);
                break;
            default:
                SetStatus("Refresh unavailable", "Open a Claude or Codex browser login first.", InfoBarSeverity.Warning);
                break;
        }
    }

    private async Task RunProviderRefreshAsync(
        LoginTarget target,
        string provider,
        Func<Task<string>> action,
        RefreshTrigger trigger)
    {
        try
        {
            var succeeded = await RunProbeAsync(provider, action, throwOnFailure: IsPolicyManagedRefresh(trigger));
            if (!succeeded)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            _lastBrowserRefreshAt[target] = now;
            _nextBrowserRefreshAllowedAt.Remove(target);
            _browserRefreshFailureCounts[target] = 0;
            if (trigger == RefreshTrigger.Manual)
            {
                _lastManualRefreshAt = now;
            }
        }
        catch (Exception exception) when (IsPolicyManagedRefresh(trigger))
        {
            ApplyRefreshBackoff(target, exception);
        }
    }

    private bool ShouldRefreshProvider(LoginTarget provider, RefreshTrigger trigger)
    {
        var now = DateTimeOffset.UtcNow;
        if (_nextBrowserRefreshAllowedAt.TryGetValue(provider, out var nextAllowed) &&
            now < nextAllowed)
        {
            SetStatus(
                "Refresh delayed",
                nextAllowed == DateTimeOffset.MaxValue
                    ? $"{provider} refresh is paused until you sign in again."
                    : $"{provider} refresh is paused until {nextAllowed.ToLocalTime():t} after the last failure.",
                InfoBarSeverity.Warning);
            return false;
        }

        if (trigger == RefreshTrigger.DetailOpen &&
            _lastBrowserRefreshAt.TryGetValue(provider, out var lastRefresh) &&
            now - lastRefresh < DetailOpenRefreshStaleAfter)
        {
            return false;
        }

        return true;
    }

    private bool IsManualRefreshCoolingDown()
    {
        var now = DateTimeOffset.UtcNow;
        var nextAllowed = _lastManualRefreshAt + ManualRefreshCooldown;
        if (now >= nextAllowed)
        {
            return false;
        }

        SetStatus(
            "Refresh cooling down",
            $"Manual refresh is available again at {nextAllowed.ToLocalTime():t}.",
            InfoBarSeverity.Informational);
        return true;
    }

    private void ApplyRefreshBackoff(LoginTarget provider, Exception exception)
    {
        var message = exception.Message;
        if (message.Contains("HTTP 401", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("HTTP 403", StringComparison.OrdinalIgnoreCase))
        {
            _nextBrowserRefreshAllowedAt[provider] = DateTimeOffset.MaxValue;
            SetStatus(
                $"{provider} sign-in required",
                "Automatic refresh paused because the browser session is no longer authorized.",
                InfoBarSeverity.Warning);
            return;
        }

        var failureCount = _browserRefreshFailureCounts.TryGetValue(provider, out var currentFailures)
            ? currentFailures + 1
            : 1;
        _browserRefreshFailureCounts[provider] = failureCount;
        var delay = BackoffDelayFor(failureCount, message);
        var nextAllowed = DateTimeOffset.UtcNow + delay;
        _nextBrowserRefreshAllowedAt[provider] = nextAllowed;
        SetStatus(
            $"{provider} refresh delayed",
            $"Keeping the last usage snapshot. Next automatic retry is after {nextAllowed.ToLocalTime():t}.",
            InfoBarSeverity.Warning);
    }

    private static TimeSpan BackoffDelayFor(int failureCount, string message)
    {
        if (TryParseRetryAfter(message, out var retryAfter))
        {
            return retryAfter;
        }

        var exponent = Math.Min(failureCount - 1, 4);
        var minutes = Math.Min(BackgroundRefreshBaseInterval.TotalMinutes * Math.Pow(2, exponent), MaximumRefreshBackoff.TotalMinutes);
        return TimeSpan.FromMinutes(minutes);
    }

    private static bool TryParseRetryAfter(string message, out TimeSpan retryAfter)
    {
        const string marker = "Retry-After:";
        var markerIndex = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            retryAfter = default;
            return false;
        }

        var valueStart = markerIndex + marker.Length;
        var valueEnd = message.IndexOfAny(new[] { '.', '\r', '\n' }, valueStart);
        var value = valueEnd < 0
            ? message[valueStart..].Trim()
            : message[valueStart..valueEnd].Trim();
        if (int.TryParse(value, out var seconds))
        {
            retryAfter = TimeSpan.FromSeconds(Math.Max(seconds, 60));
            return true;
        }

        retryAfter = default;
        return false;
    }

    private void ScheduleBackgroundRefresh()
    {
        var jitterSeconds = BackgroundRefreshBaseInterval.TotalSeconds * BackgroundRefreshJitterRatio;
        var offsetSeconds = (Random.Shared.NextDouble() * 2d - 1d) * jitterSeconds;
        _usageRefreshTimer.Interval = BackgroundRefreshBaseInterval + TimeSpan.FromSeconds(offsetSeconds);
        _usageRefreshTimer.Start();
    }

    private static bool IsPolicyManagedRefresh(RefreshTrigger trigger) =>
        trigger is RefreshTrigger.Startup or RefreshTrigger.Background or RefreshTrigger.DetailOpen or RefreshTrigger.CookieCapture or RefreshTrigger.Manual;

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
        SaveCurrentPreferencesFromUi();
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

    private async void ToggleDetailWindowFromTray()
    {
        if (_trayDetailWindow.AppWindow.IsVisible)
        {
            _trayDetailWindow.HideDetail();
            return;
        }

        RefreshTraySurfaces();
        _trayDetailWindow.ShowNearCursor();
        await RefreshCapturedUsageAsync(RefreshTrigger.DetailOpen);
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
        SyncColoredThemeAvailability();
        _displaySettings = ReadDisplaySettings();
        RefreshTraySurfaces();
        RefreshMainSurface();
    }

    private void SessionState_Changed(object? sender, ProviderSessionState state)
    {
        RefreshTraySurfaces();
        RefreshMainSurface();
    }

    private void RefreshTraySurfaces()
    {
        var usageState = _usageState.Current;
        _trayDetailWindow.UpdateDisplaySettings(_displaySettings);
        _trayDetailWindow.UpdateState(usageState, _sessionState.Current);
        _trayIconHost.UpdateUsage(usageState);
    }

    private void RefreshMainSurface()
    {
        var usageState = _usageState.Current;
        var sessionState = _sessionState.Current;
        var currentClaudeAccount = GetCurrentAccount(ProviderKind.Claude);
        var currentCodexAccount = GetCurrentAccount(ProviderKind.Codex);

        LastUpdatedText.Text = usageState.UpdatedAt is null
            ? "No usage snapshot yet"
            : $"Updated {usageState.UpdatedAt.Value.ToLocalTime():g}";

        ClaudeStatusText.Text = FormatSessionStatus(sessionState.Claude);
        ClaudeUsageSummaryText.Text = usageState.Claude is null
            ? "--"
            : FormatProviderSummary(usageState.Claude.FiveHour, usageState.Claude.SevenDay);
        ClaudeUsageDetailText.Text = usageState.Claude is null
            ? "Connect Claude to show selected limits."
            : FormatClaudeDetails(usageState.Claude, _displaySettings);

        CodexStatusText.Text = FormatSessionStatus(sessionState.Codex);
        CodexUsageSummaryText.Text = usageState.Codex is null
            ? "--"
            : FormatProviderSummary(usageState.Codex.Primary, usageState.Codex.Secondary);
        CodexUsageDetailText.Text = usageState.Codex is null
            ? "Connect Codex to show rate limits and credits."
            : FormatCodexDetails(usageState.Codex, _displaySettings);

        ClaudeAccountDetailText.Text = sessionState.Claude switch
        {
            ProviderSessionSource.WebViewCookie => currentClaudeAccount is null
                ? "Browser session available"
                : $"{currentClaudeAccount.DisplayName} - browser session",
            ProviderSessionSource.CredentialLocker => currentClaudeAccount is null
                ? "Stored credential available"
                : $"{currentClaudeAccount.DisplayName} - stored credential",
            _ => "No account connected",
        };
        CodexAccountDetailText.Text = sessionState.Codex == ProviderSessionSource.WebViewCookie
            ? currentCodexAccount is null
                ? "Browser session available"
                : $"{currentCodexAccount.DisplayName} - browser session"
            : "No account connected";
    }

    private static string FormatSessionStatus(ProviderSessionSource source) =>
        source switch
        {
            ProviderSessionSource.WebViewCookie => "Signed in with browser",
            ProviderSessionSource.CredentialLocker => "Credential loaded",
            _ => "Not signed in",
        };

    private static string FormatProviderSummary(UsageLimit? primary, UsageLimit? secondary)
    {
        var primaryText = primary is null ? "--" : $"{primary.Percentage:0.#}%";
        var secondaryText = secondary is null ? "--" : $"{secondary.Percentage:0.#}%";
        return $"{primaryText} / {secondaryText}";
    }

    private DisplaySettings ReadDisplaySettings()
    {
        var mode = DisplayModeComboBox?.SelectedIndex == 1
            ? DisplayMode.Custom
            : DisplayMode.Smart;
        var settings = new DisplaySettings(
            mode,
            ShowFiveHourCheckBox?.IsChecked == true,
            ShowSevenDayCheckBox?.IsChecked == true,
            ShowExtraUsageCheckBox?.IsChecked == true,
            ShowOpusCheckBox?.IsChecked == true,
            ShowSonnetCheckBox?.IsChecked == true,
            ShowCodexPrimaryCheckBox?.IsChecked == true,
            ShowCodexSecondaryCheckBox?.IsChecked == true,
            ShowCodexCreditsCheckBox?.IsChecked == true,
            ColoredThemeSwitch?.IsOn == true,
            DetailTimeModeComboBox?.SelectedIndex == 1 ? DetailTimeMode.TimeRemaining : DetailTimeMode.ResetTime,
            AppearanceComboBox?.SelectedIndex switch
            {
                1 => AppAppearance.Light,
                2 => AppAppearance.Dark,
                _ => AppAppearance.System,
            });

        if (CustomDisplayPanel is not null)
        {
            CustomDisplayPanel.Visibility = mode == DisplayMode.Custom ? Visibility.Visible : Visibility.Collapsed;
        }

        return settings;
    }

    private void ApplyDisplaySettingsToControls()
    {
        if (!IsDisplaySettingsUiReady())
        {
            return;
        }

        _syncingDisplaySettingsControls = true;
        KeepInTraySwitch.IsOn = _preferences.KeepInTray;
        DisplayModeComboBox.SelectedIndex = _displaySettings.DisplayMode == DisplayMode.Custom ? 1 : 0;
        DetailTimeModeComboBox.SelectedIndex = _displaySettings.DetailTimeMode == DetailTimeMode.TimeRemaining ? 1 : 0;
        AppearanceComboBox.SelectedIndex = _displaySettings.Appearance switch
        {
            AppAppearance.Light => 1,
            AppAppearance.Dark => 2,
            _ => 0,
        };
        ShowFiveHourCheckBox.IsChecked = _displaySettings.ShowFiveHour;
        ShowSevenDayCheckBox.IsChecked = _displaySettings.ShowSevenDay;
        ShowExtraUsageCheckBox.IsChecked = _displaySettings.ShowExtraUsage;
        ShowOpusCheckBox.IsChecked = _displaySettings.ShowOpus;
        ShowSonnetCheckBox.IsChecked = _displaySettings.ShowSonnet;
        ShowCodexPrimaryCheckBox.IsChecked = _displaySettings.ShowCodexPrimary;
        ShowCodexSecondaryCheckBox.IsChecked = _displaySettings.ShowCodexSecondary;
        ShowCodexCreditsCheckBox.IsChecked = _displaySettings.ShowCodexCredits;
        ColoredThemeSwitch.IsOn = _displaySettings.UseColoredTheme;
        CustomDisplayPanel.Visibility = _displaySettings.DisplayMode == DisplayMode.Custom
            ? Visibility.Visible
            : Visibility.Collapsed;
        _syncingDisplaySettingsControls = false;
        ApplyAppearance();
        SyncColoredThemeAvailability();
    }

    private void ApplyAppearance()
    {
        if (Content is not FrameworkElement root)
        {
            return;
        }

        root.RequestedTheme = _displaySettings.Appearance switch
        {
            AppAppearance.Light => ElementTheme.Light,
            AppAppearance.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }

    private void EnsureAtLeastOneCustomDisplayOption()
    {
        if (DisplayModeComboBox?.SelectedIndex != 1 || CountSelectedCustomDisplayOptions() > 0)
        {
            return;
        }

        _syncingDisplaySettingsControls = true;
        if (ShowFiveHourCheckBox is not null)
        {
            ShowFiveHourCheckBox.IsChecked = true;
        }
        _syncingDisplaySettingsControls = false;
        SetStatus("Display option kept", "At least one usage limit must remain visible.", InfoBarSeverity.Informational);
    }

    private void SyncColoredThemeAvailability()
    {
        if (ColoredThemeSwitch is null)
        {
            return;
        }

        var selectedCount = DisplayModeComboBox?.SelectedIndex == 1
            ? CountSelectedCustomDisplayOptions()
            : CountSmartDisplayOptionsWithData(_usageState.Current);
        var canUseColor = selectedCount <= 2;

        _syncingDisplaySettingsControls = true;
        ColoredThemeSwitch.IsEnabled = canUseColor;
        if (!canUseColor)
        {
            ColoredThemeSwitch.IsOn = false;
        }
        _syncingDisplaySettingsControls = false;
    }

    private int CountSelectedCustomDisplayOptions()
    {
        var options = new[]
        {
            ShowFiveHourCheckBox?.IsChecked == true,
            ShowSevenDayCheckBox?.IsChecked == true,
            ShowExtraUsageCheckBox?.IsChecked == true,
            ShowOpusCheckBox?.IsChecked == true,
            ShowSonnetCheckBox?.IsChecked == true,
            ShowCodexPrimaryCheckBox?.IsChecked == true,
            ShowCodexSecondaryCheckBox?.IsChecked == true,
            ShowCodexCreditsCheckBox?.IsChecked == true,
        };
        return options.Count(selected => selected);
    }

    private bool IsDisplaySettingsUiReady() =>
        DisplayModeComboBox is not null &&
        DetailTimeModeComboBox is not null &&
        AppearanceComboBox is not null &&
        ColoredThemeSwitch is not null &&
        CustomDisplayPanel is not null &&
        ShowFiveHourCheckBox is not null &&
        ShowSevenDayCheckBox is not null &&
        ShowExtraUsageCheckBox is not null &&
        ShowOpusCheckBox is not null &&
        ShowSonnetCheckBox is not null &&
        ShowCodexPrimaryCheckBox is not null &&
        ShowCodexSecondaryCheckBox is not null &&
        ShowCodexCreditsCheckBox is not null;

    private static int CountSmartDisplayOptionsWithData(UsageState state)
    {
        var count = 0;
        if (state.Claude?.FiveHour is not null)
        {
            count++;
        }

        if (state.Claude?.SevenDay is not null)
        {
            count++;
        }

        if (state.Claude?.ExtraUsage?.Enabled == true)
        {
            count++;
        }

        if (state.Claude?.OpusWeekly is not null)
        {
            count++;
        }

        if (state.Claude?.SonnetWeekly is not null)
        {
            count++;
        }

        if (state.Codex?.Primary is not null)
        {
            count++;
        }

        if (state.Codex?.Secondary is not null)
        {
            count++;
        }

        if (state.Codex?.Credits?.Enabled == true)
        {
            count++;
        }

        return count;
    }

    private void SavePreferences()
    {
        var keepInTray = KeepInTraySwitch?.IsOn ?? _preferences.KeepInTray;
        _preferences = _preferences with
        {
            DisplaySettings = _displaySettings,
            KeepInTray = keepInTray,
            TrayDetailTopMost = _trayDetailWindow?.IsTopMost ?? _preferences.TrayDetailTopMost,
        };
        try
        {
            _preferencesStore.Save(_preferences);
        }
        catch (Exception exception)
        {
            SetStatus("Settings not saved", exception.Message, InfoBarSeverity.Warning);
        }
    }

    private void SaveCurrentPreferencesFromUi()
    {
        if (IsDisplaySettingsUiReady())
        {
            _displaySettings = ReadDisplaySettings();
        }

        SavePreferences();
    }

    private void UpsertAccount(
        ProviderKind provider,
        string displayName,
        string stableSecret,
        ProviderSessionSource sessionSource)
    {
        var now = DateTimeOffset.UtcNow;
        var stableId = CreateStableAccountId(stableSecret);
        var accounts = _preferences.Accounts.ToList();
        var existingIndex = accounts.FindIndex(account =>
            account.Provider == provider &&
            string.Equals(account.StableId, stableId, StringComparison.Ordinal));

        ProviderAccount account;
        if (existingIndex >= 0)
        {
            account = accounts[existingIndex] with
            {
                DisplayName = accounts[existingIndex].DisplayName,
                SessionSource = sessionSource,
                LastSeenAt = now,
            };
            accounts[existingIndex] = account;
        }
        else
        {
            account = new ProviderAccount(
                Guid.NewGuid(),
                provider,
                displayName,
                StableId: stableId,
                sessionSource,
                CreatedAt: now,
                LastSeenAt: now);
            accounts.Add(account);
        }

        var profile = EnsureCurrentProfile();
        var profiles = _preferences.Profiles
            .Select(existing => existing.Id == profile.Id
                ? LinkProfileAccount(existing, provider, account.Id)
                : existing)
            .ToList();
        var currentProfile = profiles.First(existing => existing.Id == profile.Id);

        _preferences = _preferences with
        {
            Accounts = accounts,
            Profiles = profiles,
            CurrentProfileId = profile.Id,
            CurrentClaudeAccountId = currentProfile.ClaudeAccountId,
            CurrentCodexAccountId = currentProfile.CodexAccountId,
        };
        SavePreferences();
        RefreshAccountControls();
        RefreshMainSurface();
    }

    private void SetProfileProviderAccount(ProviderKind provider, Guid accountId)
    {
        var profile = EnsureCurrentProfile();
        var profiles = _preferences.Profiles
            .Select(existing => existing.Id == profile.Id
                ? LinkProfileAccount(existing, provider, accountId)
                : existing)
            .ToList();
        var currentProfile = profiles.First(existing => existing.Id == profile.Id);
        _preferences = _preferences with
        {
            Profiles = profiles,
            CurrentClaudeAccountId = currentProfile.ClaudeAccountId,
            CurrentCodexAccountId = currentProfile.CodexAccountId,
        };
        SavePreferences();
        RefreshAccountControls();
        RefreshMainSurface();
    }

    private void SetCurrentProfile(Guid profileId)
    {
        var profile = _preferences.Profiles.FirstOrDefault(existing => existing.Id == profileId);
        if (profile is null)
        {
            return;
        }

        _preferences = _preferences with
        {
            CurrentProfileId = profile.Id,
            CurrentClaudeAccountId = profile.ClaudeAccountId,
            CurrentCodexAccountId = profile.CodexAccountId,
        };
        SavePreferences();
        RefreshAccountControls();
        RefreshMainSurface();
    }

    private void UpdateProfile(AccountProfile profile)
    {
        var profiles = _preferences.Profiles
            .Select(existing => existing.Id == profile.Id ? profile : existing)
            .ToList();
        _preferences = _preferences with { Profiles = profiles };
        SavePreferences();
        RefreshAccountControls();
        RefreshMainSurface();
    }

    private AccountProfile CreateProfile(string name)
    {
        var now = DateTimeOffset.UtcNow;
        var profile = new AccountProfile(
            Guid.NewGuid(),
            name,
            _preferences.CurrentClaudeAccountId,
            _preferences.CurrentCodexAccountId,
            now,
            now);
        _preferences = _preferences with
        {
            Profiles = _preferences.Profiles.Append(profile).ToList(),
            CurrentProfileId = profile.Id,
        };
        SavePreferences();
        RefreshAccountControls();
        return profile;
    }

    private void RemoveProfile(AccountProfile profile)
    {
        var profiles = _preferences.Profiles
            .Where(existing => existing.Id != profile.Id)
            .ToList();
        if (profiles.Count == 0)
        {
            var now = DateTimeOffset.UtcNow;
            profiles.Add(new AccountProfile(Guid.NewGuid(), "Default", null, null, now, now));
        }

        var nextProfile = profiles.First();

        _preferences = _preferences with
        {
            Profiles = profiles,
            CurrentProfileId = nextProfile.Id,
            CurrentClaudeAccountId = nextProfile.ClaudeAccountId,
            CurrentCodexAccountId = nextProfile.CodexAccountId,
        };
        SavePreferences();
        RefreshAccountControls();
        RefreshMainSurface();
    }

    private AccountProfile EnsureCurrentProfile() =>
        GetCurrentProfile() ?? CreateProfile("Default");

    private AccountProfile? GetCurrentProfile() =>
        _preferences.Profiles.FirstOrDefault(profile => profile.Id == _preferences.CurrentProfileId)
        ?? _preferences.Profiles.FirstOrDefault();

    private ProviderAccount? GetCurrentAccount(ProviderKind provider)
    {
        var currentId = provider == ProviderKind.Claude
            ? _preferences.CurrentClaudeAccountId
            : _preferences.CurrentCodexAccountId;
        return _preferences.Accounts.FirstOrDefault(account =>
            account.Provider == provider &&
            account.Id == currentId)
            ?? _preferences.Accounts.FirstOrDefault(account => account.Provider == provider);
    }

    private static AccountProfile LinkProfileAccount(AccountProfile profile, ProviderKind provider, Guid accountId) =>
        provider == ProviderKind.Claude
            ? profile with { ClaudeAccountId = accountId, UpdatedAt = DateTimeOffset.UtcNow }
            : profile with { CodexAccountId = accountId, UpdatedAt = DateTimeOffset.UtcNow };

    private void RefreshAccountControls()
    {
        if (ProfilesList is null ||
            ClaudeAccountsList is null ||
            CodexAccountsList is null ||
            AccountAliasBox is null)
        {
            return;
        }

        _syncingAccountControls = true;
        var profileItems = _preferences.Profiles
            .OrderBy(profile => profile.CreatedAt)
            .Select(profile => new ProfileListItem(profile))
            .ToList();
        var claudeItems = _preferences.Accounts
            .Where(account => account.Provider == ProviderKind.Claude)
            .OrderBy(account => account.CreatedAt)
            .Select(account => new AccountListItem(account))
            .ToList();
        var codexItems = _preferences.Accounts
            .Where(account => account.Provider == ProviderKind.Codex)
            .OrderBy(account => account.CreatedAt)
            .Select(account => new AccountListItem(account))
            .ToList();

        ProfilesList.ItemsSource = profileItems;
        ClaudeAccountsList.ItemsSource = claudeItems;
        CodexAccountsList.ItemsSource = codexItems;
        ProfilesList.SelectedItem = profileItems.FirstOrDefault(item => item.Id == _preferences.CurrentProfileId);
        ClaudeAccountsList.SelectedItem = claudeItems.FirstOrDefault(item => item.Id == _preferences.CurrentClaudeAccountId);
        CodexAccountsList.SelectedItem = codexItems.FirstOrDefault(item => item.Id == _preferences.CurrentCodexAccountId);

        var currentProfile = GetCurrentProfile();
        AccountAliasBox.Text = currentProfile?.Name ?? string.Empty;
        _syncingAccountControls = false;
    }

    private static string CreateStableAccountId(string value)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes[..12]);
    }

    private sealed record ProfileListItem(Guid Id, string DisplayText)
    {
        public ProfileListItem(AccountProfile profile)
            : this(profile.Id, profile.Name)
        {
        }

        public override string ToString() => DisplayText;
    }

    private sealed record AccountListItem(Guid Id, string DisplayText)
    {
        public AccountListItem(ProviderAccount account)
            : this(account.Id, $"{account.DisplayName} ({FormatSource(account.SessionSource)})")
        {
        }

        public override string ToString() => DisplayText;

        private static string FormatSource(ProviderSessionSource source) =>
            source switch
            {
                ProviderSessionSource.WebViewCookie => "browser",
                ProviderSessionSource.CredentialLocker => "stored",
                _ => "none",
            };
    }

    private static string FormatClaudeDetails(ClaudeUsageSnapshot usage, DisplaySettings settings)
    {
        var lines = new List<string>();
        if (ShouldShow(settings, settings.ShowFiveHour, usage.FiveHour is not null))
        {
            lines.Add($"5-hour {FormatLimitInline(usage.FiveHour, settings.DetailTimeMode)}");
        }

        if (ShouldShow(settings, settings.ShowSevenDay, usage.SevenDay is not null))
        {
            lines.Add($"7-day {FormatLimitInline(usage.SevenDay, settings.DetailTimeMode)}");
        }

        if (ShouldShow(settings, settings.ShowExtraUsage, usage.ExtraUsage?.Enabled == true))
        {
            lines.Add($"Extra {FormatExtraUsageInline(usage.ExtraUsage)}");
        }

        if (ShouldShow(settings, settings.ShowOpus, usage.OpusWeekly is not null))
        {
            lines.Add($"Opus {FormatLimitInline(usage.OpusWeekly, settings.DetailTimeMode)}");
        }

        if (ShouldShow(settings, settings.ShowSonnet, usage.SonnetWeekly is not null))
        {
            lines.Add($"Sonnet {FormatLimitInline(usage.SonnetWeekly, settings.DetailTimeMode)}");
        }

        return lines.Count == 0 ? "No selected Claude limits have data yet." : string.Join("  |  ", lines);
    }

    private static string FormatCodexDetails(CodexUsageSnapshot usage, DisplaySettings settings)
    {
        var lines = new List<string>();
        if (ShouldShow(settings, settings.ShowCodexPrimary, usage.Primary is not null))
        {
            lines.Add($"Primary {FormatLimitInline(usage.Primary, settings.DetailTimeMode)}");
        }

        if (ShouldShow(settings, settings.ShowCodexSecondary, usage.Secondary is not null))
        {
            lines.Add($"Secondary {FormatLimitInline(usage.Secondary, settings.DetailTimeMode)}");
        }

        if (ShouldShow(settings, settings.ShowCodexCredits, usage.Credits?.Enabled == true))
        {
            lines.Add($"Credits {FormatCodexCreditsInline(usage.Credits)}");
        }

        return lines.Count == 0 ? "No selected Codex limits have data yet." : string.Join("  |  ", lines);
    }

    private static bool ShouldShow(DisplaySettings settings, bool customEnabled, bool hasData) =>
        hasData && (settings.DisplayMode == DisplayMode.Smart || customEnabled);

    private static string FormatLimitInline(UsageLimit? limit, DetailTimeMode timeMode)
    {
        if (limit is null)
        {
            return "--";
        }

        var suffix = timeMode == DetailTimeMode.TimeRemaining
            ? FormatRemaining(limit.ResetsAt)
            : FormatResetInline(limit.ResetsAt);
        return $"{limit.Percentage:0.#}% {suffix}";
    }

    private static string FormatResetInline(DateTimeOffset? resetsAt) =>
        resetsAt is null ? "reset unavailable" : $"resets {resetsAt.Value.ToLocalTime():g}";

    private static string FormatRemaining(DateTimeOffset? resetsAt)
    {
        if (resetsAt is null)
        {
            return "remaining unavailable";
        }

        var remaining = resetsAt.Value - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return "ready to reset";
        }

        return remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours}h {remaining.Minutes}m left"
            : $"{Math.Max(1, remaining.Minutes)}m left";
    }

    private static string FormatExtraUsageInline(ClaudeExtraUsageSnapshot? extraUsage)
    {
        if (extraUsage is null || !extraUsage.Enabled)
        {
            return "--";
        }

        if (extraUsage.Used is null || extraUsage.Limit is null)
        {
            return "enabled";
        }

        return $"{extraUsage.Currency}{extraUsage.Used:0.##}/{extraUsage.Limit:0.##}";
    }

    private static string FormatCodexCreditsInline(CodexCreditsSnapshot? credits)
    {
        if (credits is null || !credits.Enabled)
        {
            return "--";
        }

        if (credits.Unlimited)
        {
            return "unlimited";
        }

        if (credits.OverageLimitReached || credits.SpendControlReached)
        {
            return "limit reached";
        }

        return credits.Balance is null ? "available" : $"balance {credits.Balance:0.##}";
    }

    private static string Usage4ClaudeIconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "Usage4Claude.ico");

    private const string StartupTaskId = "Usage4ClaudeStartup";
    private const double BackgroundRefreshJitterRatio = 0.2d;
    private static readonly TimeSpan BackgroundRefreshBaseInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan DetailOpenRefreshStaleAfter = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ManualRefreshCooldown = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaximumRefreshBackoff = TimeSpan.FromHours(1);

    private enum LoginTarget
    {
        None,
        Claude,
        Codex,
    }

    private enum RefreshTrigger
    {
        Startup,
        Background,
        DetailOpen,
        CookieCapture,
        Manual,
        ManualProbe,
    }

}
