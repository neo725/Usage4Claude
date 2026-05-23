namespace Usage4Claude.Infrastructure.Http;

internal static class BrowserLikeHeaders
{
    private const string DesktopChromeUserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    public static void ApplyClaudeHeaders(HttpRequestMessage request, string sessionKey)
    {
        ApplyCommonHeaders(request, "https://claude.ai", "https://claude.ai/settings/usage");
        request.Headers.TryAddWithoutValidation("anthropic-client-platform", "web_claude_ai");
        request.Headers.TryAddWithoutValidation("anthropic-client-version", "1.0.0");
        request.Headers.TryAddWithoutValidation("Cookie", $"sessionKey={sessionKey}");
    }

    public static void ApplyCodexSessionHeaders(HttpRequestMessage request, string cookieHeader)
    {
        ApplyCommonHeaders(request, "https://chatgpt.com", "https://chatgpt.com/");
        request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
    }

    public static void ApplyCodexUsageHeaders(HttpRequestMessage request, string accessToken)
    {
        ApplyCommonHeaders(request, "https://chatgpt.com", "https://chatgpt.com/");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
    }

    private static void ApplyCommonHeaders(HttpRequestMessage request, string origin, string referer)
    {
        request.Headers.TryAddWithoutValidation("accept", "*/*");
        request.Headers.TryAddWithoutValidation("accept-language", "zh-CN,zh;q=0.9,en;q=0.8");
        request.Headers.TryAddWithoutValidation("content-type", "application/json");
        request.Headers.TryAddWithoutValidation("origin", origin);
        request.Headers.Referrer = new Uri(referer);
        request.Headers.TryAddWithoutValidation("sec-fetch-dest", "empty");
        request.Headers.TryAddWithoutValidation("sec-fetch-mode", "cors");
        request.Headers.TryAddWithoutValidation("sec-fetch-site", "same-origin");
        request.Headers.UserAgent.ParseAdd(DesktopChromeUserAgent);
    }
}
