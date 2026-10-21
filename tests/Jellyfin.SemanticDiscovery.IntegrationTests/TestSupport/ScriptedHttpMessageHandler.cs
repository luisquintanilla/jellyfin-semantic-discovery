using System.Net;

namespace Jellyfin.SemanticDiscovery.IntegrationTests.TestSupport;

/// <summary>
/// A scripted <see cref="HttpMessageHandler"/> that records every request
/// (method, URI, and body bytes captured eagerly so they remain readable
/// after the request/content is disposed) and delegates the response to a
/// caller-supplied handler function.
/// </summary>
public sealed class ScriptedHttpMessageHandler : HttpMessageHandler
{
    public List<CapturedRequest> Requests { get; } = [];

    public Func<CapturedRequest, CancellationToken, Task<HttpResponseMessage>> Handler { get; set; } =
        static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        byte[] body = request.Content is null
            ? []
            : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        CapturedRequest captured = new(request.Method, request.RequestUri, body);
        Requests.Add(captured);
        return await Handler(captured, cancellationToken).ConfigureAwait(false);
    }
}

public sealed record CapturedRequest(HttpMethod Method, Uri? RequestUri, byte[] Body)
{
    public string BodyAsUtf8Text => System.Text.Encoding.UTF8.GetString(Body);
}
