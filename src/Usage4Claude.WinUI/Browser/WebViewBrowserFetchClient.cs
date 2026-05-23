using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace Usage4Claude.WinUI.Browser;

internal sealed class WebViewBrowserFetchClient
{
    private readonly WebView2 _webView;

    public WebViewBrowserFetchClient(WebView2 webView)
    {
        _webView = webView;
    }

    public async Task<string> GetAsync(
        string path,
        string? bearerToken = null,
        bool allowFailure = false)
    {
        if (_webView.CoreWebView2 is null)
        {
            throw new InvalidOperationException("WebView2 is not initialized.");
        }

        var pathJson = JsonSerializer.Serialize(path);
        var bearerJson = JsonSerializer.Serialize(bearerToken);
        var probeId = Guid.NewGuid().ToString("N");
        var probeIdJson = JsonSerializer.Serialize(probeId);
        var rawResult = await ExecuteProbeAsync(
            probeId,
            $$"""
            (() => {
              const id = {{probeIdJson}};
              const send = payload => window.chrome.webview.postMessage({ id, ...payload });
              (async () => {
                try {
                  const bearer = {{bearerJson}};
                  const headers = bearer ? { authorization: `Bearer ${bearer}` } : {};
                  const response = await fetch({{pathJson}}, {
                    method: "GET",
                    credentials: "include",
                    headers
                  });
                  const body = await response.text();
                  send({
                    ok: response.ok,
                    status: response.status,
                    retryAfter: response.headers.get("retry-after"),
                    contentType: response.headers.get("content-type"),
                    body
                  });
                } catch (error) {
                  send({
                    ok: false,
                    status: 0,
                    retryAfter: null,
                    contentType: null,
                    body: error instanceof Error ? `${error.name}: ${error.message}` : String(error)
                  });
                }
              })();
              return "probe-started";
            })()
            """);
        var payload = DeserializeFetchPayload(rawResult);

        if (!payload.Ok && !allowFailure)
        {
            var retryAfter = string.IsNullOrWhiteSpace(payload.RetryAfter)
                ? string.Empty
                : $" Retry-After: {payload.RetryAfter}.";
            throw new InvalidOperationException(
                $"Browser fetch returned HTTP {payload.Status}.{retryAfter} Content-Type: {payload.ContentType ?? "unknown"}. Body preview: {Preview(payload.Body)}");
        }

        return payload.Ok
            ? payload.Body ?? throw new InvalidOperationException("Browser fetch returned an empty body.")
            : string.Empty;
    }

    private async Task<string> ExecuteProbeAsync(string probeId, string script)
    {
        if (_webView.CoreWebView2 is null)
        {
            throw new InvalidOperationException("WebView2 is not initialized.");
        }

        var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        void HandleMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            try
            {
                using var message = JsonDocument.Parse(args.WebMessageAsJson);
                if (!message.RootElement.TryGetProperty("id", out var id) ||
                    id.GetString() != probeId)
                {
                    return;
                }

                result.TrySetResult(message.RootElement.GetRawText());
            }
            catch (JsonException exception)
            {
                result.TrySetException(exception);
            }
        }

        _webView.CoreWebView2.WebMessageReceived += HandleMessage;
        try
        {
            await _webView.CoreWebView2.ExecuteScriptAsync(script);
            return await result.Task.WaitAsync(TimeSpan.FromSeconds(45));
        }
        finally
        {
            _webView.CoreWebView2.WebMessageReceived -= HandleMessage;
        }
    }

    private static BrowserFetchPayload DeserializeFetchPayload(string rawResult)
    {
        using var document = JsonDocument.Parse(rawResult);
        var payloadJson = document.RootElement.ValueKind == JsonValueKind.String
            ? document.RootElement.GetString()
            : document.RootElement.GetRawText();

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            throw new InvalidOperationException("WebView2 fetch probe returned no script result.");
        }

        return JsonSerializer.Deserialize<BrowserFetchPayload>(payloadJson)
            ?? throw new InvalidOperationException("WebView2 fetch probe returned no response payload.");
    }

    private static string Preview(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "empty";
        }

        var compact = text.ReplaceLineEndings(" ");
        return compact.Length <= 240 ? compact : $"{compact[..240]}...";
    }

    private sealed class BrowserFetchPayload
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; init; }

        [JsonPropertyName("status")]
        public int Status { get; init; }

        [JsonPropertyName("retryAfter")]
        public string? RetryAfter { get; init; }

        [JsonPropertyName("contentType")]
        public string? ContentType { get; init; }

        [JsonPropertyName("body")]
        public string? Body { get; init; }
    }
}
