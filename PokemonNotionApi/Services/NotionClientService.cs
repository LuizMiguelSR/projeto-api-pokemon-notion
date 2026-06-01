using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PokemonNotionApi.Options;
using Microsoft.Extensions.Options;

namespace PokemonNotionApi.Services;

public sealed class NotionClientService(HttpClient httpClient, IOptions<NotionOptions> options)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly NotionOptions _options = options.Value;
    private const string NotionBaseUrl = "https://api.notion.com/v1/";

    public async Task<JsonElement?> QueryDatabaseAsync(CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, $"databases/{_options.DatabaseId}/query", "{}");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return doc.RootElement.Clone();
    }

    public async Task UpdatePageAsync(string pageId, object payload, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(payload, JsonOptions);
        using var request = CreateRequest(new HttpMethod("PATCH"), $"pages/{pageId}", body);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, string? body = null)
    {
        var request = new HttpRequestMessage(method, $"{NotionBaseUrl}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Token);
        request.Headers.Add("Notion-Version", _options.NotionVersion);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }
        return request;
    }
}
