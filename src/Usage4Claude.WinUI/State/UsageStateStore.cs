using Usage4Claude.Core.Usage;

namespace Usage4Claude.WinUI.State;

internal sealed class UsageStateStore
{
    public event EventHandler<UsageState>? Changed;

    public UsageState Current { get; private set; } = UsageState.Empty;

    public void SetClaude(ClaudeUsageSnapshot usage)
    {
        Current = Current.WithClaude(usage, DateTimeOffset.UtcNow);
        Changed?.Invoke(this, Current);
    }

    public void SetCodex(CodexUsageSnapshot usage)
    {
        Current = Current.WithCodex(usage, DateTimeOffset.UtcNow);
        Changed?.Invoke(this, Current);
    }
}
