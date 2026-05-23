using System.Net;

namespace Usage4Claude.Infrastructure.Http;

public sealed class UsageApiException(string message, HttpStatusCode? statusCode = null) : Exception(message)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
}
