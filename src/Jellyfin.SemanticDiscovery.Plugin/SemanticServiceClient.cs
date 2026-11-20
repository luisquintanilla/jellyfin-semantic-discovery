using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Jellyfin.SemanticDiscovery.Plugin;

public sealed record MediaItem(Guid Id, string Title, string Text);

public sealed record SearchHit(Guid Id, double Score, int? Page, string? Snippet);

/// <summary>
/// The whole plugin talks to the semantic service through these four calls. Aspire passes
/// the service address to the Jellyfin container as <c>services__semantic-service__http__0</c>.
/// </summary>
public sealed class SemanticServiceClient : IDisposable
{
    private readonly HttpClient _http = new()
    {
        BaseAddress = new Uri(
            Environment.GetEnvironmentVariable("services__semantic-service__http__0")
            ?? Environment.GetEnvironmentVariable("SEMANTIC_SERVICE_URL")
            ?? "http://localhost:5180"),
        // Reading a scanned book with a vision model can take minutes.
        Timeout = TimeSpan.FromMinutes(30),
    };

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken) =>
        await _http.GetFromJsonAsync<SearchHit[]>(
            $"search?q={Uri.EscapeDataString(query)}&limit={limit}", cancellationToken) ?? [];

    public async Task<IReadOnlyList<SearchHit>> SimilarAsync(Guid id, int limit, CancellationToken cancellationToken) =>
        await _http.GetFromJsonAsync<SearchHit[]>($"similar/{id}?limit={limit}", cancellationToken) ?? [];

    public async Task IndexMediaAsync(IReadOnlyList<MediaItem> items, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _http.PostAsJsonAsync("index/media", items, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task IndexBookAsync(Guid id, string pdfPath, CancellationToken cancellationToken)
    {
        await using FileStream pdf = File.OpenRead(pdfPath);
        using StreamContent content = new(pdf);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        using HttpResponseMessage response = await _http.PutAsync($"index/books/{id}", content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public void Dispose() => _http.Dispose();
}
