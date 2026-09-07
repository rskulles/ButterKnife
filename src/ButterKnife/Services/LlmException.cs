using System.Net;

namespace ButterKnife.Services;

public sealed class LlmException : Exception
{
    public LlmException(string backend, HttpStatusCode statusCode, string body)
        : base($"{backend} returned {(int)statusCode} {statusCode}: {Truncate(body)}")
    {
        Backend = backend;
        StatusCode = statusCode;
        Body = body;
    }

    public string Backend { get; }
    public HttpStatusCode StatusCode { get; }
    public string Body { get; }

    private static string Truncate(string s) => s.Length <= 500 ? s : s[..500] + "…";
}
