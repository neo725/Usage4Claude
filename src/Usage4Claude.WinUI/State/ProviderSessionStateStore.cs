namespace Usage4Claude.WinUI.State;

internal sealed class ProviderSessionStateStore
{
    public event EventHandler<ProviderSessionState>? Changed;

    public ProviderSessionState Current { get; private set; } = ProviderSessionState.Empty;

    public void ActivateBrowser(ProviderKind provider)
    {
        Current = Current with { ActiveBrowserProvider = provider };
        NotifyChanged();
    }

    public void SetClaudeWebViewSession()
    {
        Current = Current with
        {
            Claude = ProviderSessionSource.WebViewCookie,
            ActiveBrowserProvider = ProviderKind.Claude,
        };
        NotifyChanged();
    }

    public void SetClaudeCredentialSession()
    {
        Current = Current with { Claude = ProviderSessionSource.CredentialLocker };
        NotifyChanged();
    }

    public void SetCodexWebViewSession()
    {
        Current = Current with
        {
            Codex = ProviderSessionSource.WebViewCookie,
            ActiveBrowserProvider = ProviderKind.Codex,
        };
        NotifyChanged();
    }

    public void ClearBrowserSessions()
    {
        Current = Current with
        {
            Claude = Current.Claude == ProviderSessionSource.WebViewCookie
                ? ProviderSessionSource.None
                : Current.Claude,
            Codex = ProviderSessionSource.None,
            ActiveBrowserProvider = null,
        };
        NotifyChanged();
    }

    private void NotifyChanged() => Changed?.Invoke(this, Current);
}
