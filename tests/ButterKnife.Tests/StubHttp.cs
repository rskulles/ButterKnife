using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Text;

namespace ButterKnife.Tests;

/// <summary>Routes every request through a delegate; records the last request for assertions.</summary>
internal sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastRequestBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        return await respond(request, cancellationToken);
    }

    public static StubHandler Text(string body, HttpStatusCode status = HttpStatusCode.OK, string mediaType = "application/json") =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, mediaType),
        }));

    /// <summary>Streams the given prefix and then blocks until the request is cancelled.</summary>
    public static StubHandler NeverEnding(string prefix)
    {
        var pipe = new Pipe();
        return new((_, _) =>
        {
            var bytes = Encoding.UTF8.GetBytes(prefix);
            pipe.Writer.Write(bytes);
            _ = pipe.Writer.FlushAsync();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(pipe.Reader.AsStream()),
            });
        });
    }
}

internal sealed class StubClientFactory(HttpMessageHandler handler, string baseUrl) : IHttpClientFactory
{
    public string? LastName { get; private set; }

    public HttpClient CreateClient(string name)
    {
        LastName = name;
        return new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }
}
