using System.Net.Http.Json;
using Usage4Claude.Core.Claude;
using Usage4Claude.Core.Usage;
using Usage4Claude.Infrastructure.Http;

namespace Usage4Claude.Infrastructure.Claude;

public sealed class ClaudeUsageClient(HttpClient httpClient)
{
    private readonly HttpClient _httpClient = httpClient;

    public async Task<IReadOnlyList<ClaudeOrganization>> GetOrganizationsAsync(
        string sessionKey,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest("https://claude.ai/api/organizations", sessionKey);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var organizations = await UsageApiResponse.ReadJsonAsync<List<ClaudeOrganizationResponse>>(
            response,
            cancellationToken);

        return organizations.Select(value => value.ToOrganization()).ToList();
    }

    public async Task<ClaudeUsageSnapshot> GetUsageAsync(
        string organizationUuid,
        string sessionKey,
        CancellationToken cancellationToken = default)
    {
        using var usageRequest = CreateRequest(
            $"https://claude.ai/api/organizations/{Uri.EscapeDataString(organizationUuid)}/usage",
            sessionKey);
        using var usageResponse = await _httpClient.SendAsync(usageRequest, cancellationToken);
        var usage = await UsageApiResponse.ReadJsonAsync<ClaudeUsageResponse>(
            usageResponse,
            cancellationToken);

        using var extraRequest = CreateRequest(
            $"https://claude.ai/api/organizations/{Uri.EscapeDataString(organizationUuid)}/overage_spend_limit",
            sessionKey);
        using var extraResponse = await _httpClient.SendAsync(extraRequest, cancellationToken);
        var extra = await TryReadExtraUsageAsync(extraResponse, cancellationToken);

        return usage.ToSnapshot(extra);
    }

    private static HttpRequestMessage CreateRequest(string url, string sessionKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        BrowserLikeHeaders.ApplyClaudeHeaders(request, sessionKey);
        return request;
    }

    private static async Task<ClaudeExtraUsageSnapshot?> TryReadExtraUsageAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var value = await response.Content.ReadFromJsonAsync<ClaudeExtraUsageResponse>(
            cancellationToken: cancellationToken);
        return value?.ToSnapshot();
    }
}
