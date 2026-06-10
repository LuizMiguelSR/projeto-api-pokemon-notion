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

    public async Task<SyncLogResult> CreateSyncLogAsync(string status, string details, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.LogDatabaseId))
        {
            return SyncLogResult.Skipped("Notion:LogDatabaseId is not configured.");
        }

        var schema = await GetDatabaseSchemaAsync(_options.LogDatabaseId, cancellationToken);
        if (schema is null)
        {
            return SyncLogResult.Skipped("Could not read the log database schema. Check the database id and integration access.");
        }

        if (!schema.Value.TryGetProperty("properties", out var schemaProperties))
        {
            return SyncLogResult.Skipped("The log database schema response does not contain properties.");
        }

        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        AddLogProperty(properties, schemaProperties, _options.LogNameProperty, $"Sincronizacao {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}");
        AddDateProperty(properties, schemaProperties, _options.LogDateProperty, DateTimeOffset.UtcNow);
        AddStatusProperty(properties, schemaProperties, _options.LogStatusProperty, status);

        var payload = new
        {
            parent = new { database_id = _options.LogDatabaseId },
            properties,
            children = new object[]
            {
                new
                {
                    type = "paragraph",
                    paragraph = new
                    {
                        rich_text = new[]
                        {
                            new { type = "text", text = new { content = details } }
                        }
                    }
                }
            }
        };

        var body = JsonSerializer.Serialize(payload, JsonOptions);
        using var request = CreateRequest(HttpMethod.Post, "pages", body);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new NotionApiException((int)response.StatusCode, errorBody);
        }

        return SyncLogResult.Success();
    }

    public async Task UpdatePageAsync(string pageId, object payload, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(payload, JsonOptions);
        using var request = CreateRequest(new HttpMethod("PATCH"), $"pages/{pageId}", body);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new NotionApiException((int)response.StatusCode, errorBody);
        }
    }

    private async Task<JsonElement?> GetDatabaseSchemaAsync(string databaseId, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, $"databases/{databaseId}");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new NotionApiException((int)response.StatusCode, errorBody);
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return doc.RootElement.Clone();
    }

    private static void AddLogProperty(
        Dictionary<string, object?> properties,
        JsonElement schemaProperties,
        string propertyName,
        string value)
    {
        var propertyType = GetSchemaPropertyType(schemaProperties, propertyName);
        if (propertyType == "title")
        {
            properties[propertyName] = Title(value);
        }
    }

    private static void AddDateProperty(
        Dictionary<string, object?> properties,
        JsonElement schemaProperties,
        string propertyName,
        DateTimeOffset value)
    {
        var propertyType = GetSchemaPropertyType(schemaProperties, propertyName);
        if (propertyType == "date")
        {
            properties[propertyName] = new { date = new { start = value.ToString("O") } };
        }
    }

    private static void AddStatusProperty(
        Dictionary<string, object?> properties,
        JsonElement schemaProperties,
        string propertyName,
        string value)
    {
        var propertyType = GetSchemaPropertyType(schemaProperties, propertyName);
        properties[propertyName] = propertyType switch
        {
            "status" => new { status = new { name = value } },
            "select" => new { select = new { name = value } },
            "rich_text" => RichText(value),
            _ => null
        };

        if (properties[propertyName] is null)
        {
            properties.Remove(propertyName);
        }
    }

    private static string? GetSchemaPropertyType(JsonElement schemaProperties, string propertyName)
    {
        if (!schemaProperties.TryGetProperty(propertyName, out var property)) return null;
        if (!property.TryGetProperty("type", out var type)) return null;
        return type.GetString();
    }

    private static object Title(string value) => new
    {
        title = new[] { new { text = new { content = value } } }
    };

    private static object RichText(string value) => new
    {
        rich_text = new[] { new { text = new { content = value } } }
    };

    public async Task AppendImageBlockIfMissingAsync(string pageId, string imageUrl, CancellationToken cancellationToken)
    {
        if (await HasImageBlockAsync(pageId, imageUrl, cancellationToken))
        {
            return;
        }

        var payload = new
        {
            children = new object[]
            {
                new
                {
                    type = "image",
                    image = new
                    {
                        type = "external",
                        external = new { url = imageUrl }
                    }
                }
            }
        };

        var body = JsonSerializer.Serialize(payload, JsonOptions);
        using var request = CreateRequest(HttpMethod.Patch, $"blocks/{pageId}/children", body);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new NotionApiException((int)response.StatusCode, errorBody);
        }
    }

    private async Task<bool> HasImageBlockAsync(string pageId, string imageUrl, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, $"blocks/{pageId}/children?page_size=100");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return false;

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!doc.RootElement.TryGetProperty("results", out var results)) return false;

        foreach (var block in results.EnumerateArray())
        {
            if (!block.TryGetProperty("type", out var typeElement) ||
                typeElement.GetString() != "image" ||
                !block.TryGetProperty("image", out var image) ||
                !image.TryGetProperty("external", out var external) ||
                !external.TryGetProperty("url", out var url))
            {
                continue;
            }

            if (string.Equals(url.GetString(), imageUrl, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
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

public sealed class NotionApiException(int statusCode, string responseBody)
    : Exception($"Notion API returned HTTP {statusCode}: {responseBody}")
{
    public int StatusCode { get; } = statusCode;
    public string ResponseBody { get; } = responseBody;
}

public sealed record SyncLogResult(bool Created, string? SkippedReason = null, string? Error = null)
{
    public static SyncLogResult Success() => new(true);
    public static SyncLogResult Skipped(string reason) => new(false, SkippedReason: reason);
    public static SyncLogResult Failed(string error) => new(false, Error: error);
}
