using Usage4Claude.Core.Codex;
using Usage4Claude.Core.Usage;
using Usage4Claude.Infrastructure.Http;

namespace Usage4Claude.Infrastructure.Codex;

public sealed class CodexUsageClient(HttpClient httpClient, TimeProvider? timeProvider = null)
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<CodexSessionResponse> GetSessionAsync(
        string cookieHeader,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/api/auth/session");
        BrowserLikeHeaders.ApplyCodexSessionHeaders(request, cookieHeader);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        return await UsageApiResponse.ReadJsonAsync<CodexSessionResponse>(response, cancellationToken);
    }

    public async Task<CodexUsageSnapshot> GetUsageAsync(
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/backend-api/wham/usage");
        BrowserLikeHeaders.ApplyCodexUsageHeaders(request, accessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var usage = await UsageApiResponse.ReadJsonAsync<CodexUsageResponse>(response, cancellationToken);
        return usage.ToSnapshot(_timeProvider.GetUtcNow());
    }
}
