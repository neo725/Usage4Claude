namespace Usage4Claude.WinUI.State;

internal enum ProviderKind
{
    Claude,
    Codex,
}

internal enum ProviderSessionSource
{
    None,
    WebViewCookie,
    CredentialLocker,
}

internal sealed record ProviderSessionState(
    ProviderSessionSource Claude,
    ProviderSessionSource Codex,
    ProviderKind? ActiveBrowserProvider)
{
    public static ProviderSessionState Empty { get; } =
        new(ProviderSessionSource.None, ProviderSessionSource.None, null);

    public bool HasClaude => Claude != ProviderSessionSource.None;

    public bool HasCodex => Codex != ProviderSessionSource.None;
}
