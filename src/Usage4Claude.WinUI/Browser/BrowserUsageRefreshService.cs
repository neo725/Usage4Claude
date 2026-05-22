using System.Text.Json;
using Usage4Claude.Core.Claude;
using Usage4Claude.Core.Codex;
using Usage4Claude.Core.Usage;

namespace Usage4Claude.WinUI.Browser;

internal sealed class BrowserUsageRefreshService
{
    private readonly WebViewBrowserFetchClient _fetchClient;

    public BrowserUsageRefreshService(WebViewBrowserFetchClient fetchClient)
    {
        _fetchClient = fetchClient;
    }

    public async Task<ClaudeBrowserRefreshResult> RefreshClaudeAsync(string? organizationId)
    {
        var organizationsJson = await _fetchClient.GetAsync("/api/organizations");
        var organizations = Deserialize<List<ClaudeOrganizationResponse>>(organizationsJson);
        var selectedOrganizationId = string.IsNullOrWhiteSpace(organizationId)
            ? organizations.FirstOrDefault()?.Uuid ?? string.Empty
            : organizationId;
        if (string.IsNullOrWhiteSpace(selectedOrganizationId))
        {
            throw new InvalidOperationException("Claude browser refresh returned no organization UUID.");
        }

        var escapedOrganizationId = Uri.EscapeDataString(selectedOrganizationId);
        var usageJson = await _fetchClient.GetAsync($"/api/organizations/{escapedOrganizationId}/usage");
        var extraJson = await _fetchClient.GetAsync(
            $"/api/organizations/{escapedOrganizationId}/overage_spend_limit",
            allowFailure: true);
        var usage = Deserialize<ClaudeUsageResponse>(usageJson).ToSnapshot(
            TryDeserialize<ClaudeExtraUsageResponse>(extraJson)?.ToSnapshot());
        return new ClaudeBrowserRefreshResult(
            organizations.Select(value => value.ToOrganization()).ToList(),
            selectedOrganizationId,
            usage);
    }

    public async Task<CodexBrowserRefreshResult> RefreshCodexAsync()
    {
        var sessionJson = await _fetchClient.GetAsync("/api/auth/session");
        var session = Deserialize<CodexSessionResponse>(sessionJson);
        if (string.IsNullOrWhiteSpace(session.AccessToken))
        {
            throw new InvalidOperationException("ChatGPT browser session response did not contain an access token.");
        }

        var usageJson = await _fetchClient.GetAsync(
            "/backend-api/wham/usage",
            bearerToken: session.AccessToken);
        var usage = Deserialize<CodexUsageResponse>(usageJson).ToSnapshot(DateTimeOffset.UtcNow);
        return new CodexBrowserRefreshResult(session.User?.Email, session.User?.Name, usage);
    }

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json)
        ?? throw new InvalidOperationException($"Browser payload did not deserialize as {typeof(T).Name}.");

    private static T? TryDeserialize<T>(string json) =>
        string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json);
}

internal sealed record ClaudeBrowserRefreshResult(
    IReadOnlyList<ClaudeOrganization> Organizations,
    string OrganizationId,
    ClaudeUsageSnapshot Usage);

internal sealed record CodexBrowserRefreshResult(
    string? Email,
    string? Name,
    CodexUsageSnapshot Usage);
