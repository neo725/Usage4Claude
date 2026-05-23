using System.Net.Http.Json;

namespace Usage4Claude.Infrastructure.Http;

internal static class UsageApiResponse
{
    public static async Task<T> ReadJsonAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new UsageApiException(
                $"Usage API returned HTTP {(int)response.StatusCode} ({response.StatusCode}).",
                response.StatusCode);
        }

        if (LooksLikeHtml(payload))
        {
            throw new UsageApiException("Usage API returned HTML instead of JSON. A browser challenge may be blocking the request.");
        }

        var value = await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken);
        return value ?? throw new UsageApiException("Usage API response did not contain the expected JSON payload.");
    }

    private static bool LooksLikeHtml(string payload) =>
        payload.Contains("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase) ||
        payload.Contains("<html", StringComparison.OrdinalIgnoreCase) ||
        payload.Contains("Just a moment", StringComparison.OrdinalIgnoreCase);
}
