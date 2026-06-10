using System.Text.Json;
using PokemonNotionApi.Models;
using PokemonNotionApi.Options;
using Microsoft.Extensions.Options;

namespace PokemonNotionApi.Services;

public sealed class SyncService(
    NotionClientService notionClientService,
    LigaPokemonScraperService scraperService,
    IOptions<NotionOptions> options,
    ILogger<SyncService> logger)
{
    private readonly NotionOptions _options = options.Value;

    public async Task<object> SyncDatabaseAsync(CancellationToken cancellationToken)
    {
        var db = await notionClientService.QueryDatabaseAsync(cancellationToken);
        if (db is null || !db.Value.TryGetProperty("results", out var results))
        {
            return new { processed = 0, updated = 0 };
        }

        var processed = 0;
        var updated = 0;
        var failed = 0;
        var errors = new List<object>();
        foreach (var page in results.EnumerateArray())
        {
            processed++;
            var pageId = page.GetProperty("id").GetString();
            if (string.IsNullOrWhiteSpace(pageId)) continue;

            try
            {
                var result = await SyncPageInternalAsync(page, cancellationToken);
                if (result is not null) updated++;
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add(new { pageId, error = ex.Message });
            }
        }

        var response = new { processed, updated, failed, errors };
        var status = failed == 0 ? "Sucesso" : "Erro";
        var details = JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
        var syncLog = await TryCreateSyncLogAsync(status, details, cancellationToken);

        return new { processed, updated, failed, errors, syncLog };
    }

    public async Task<object?> SyncSinglePageAsync(string pageId, CancellationToken cancellationToken)
    {
        var db = await notionClientService.QueryDatabaseAsync(cancellationToken);
        if (db is null || !db.Value.TryGetProperty("results", out var results)) return null;

        foreach (var page in results.EnumerateArray())
        {
            if (string.Equals(page.GetProperty("id").GetString(), pageId, StringComparison.OrdinalIgnoreCase))
            {
                return await SyncPageInternalAsync(page, cancellationToken);
            }
        }

        return null;
    }

    private async Task<object?> SyncPageInternalAsync(JsonElement page, CancellationToken cancellationToken)
    {
        var properties = page.GetProperty("properties");
        var url = ReadNotionPlainText(properties, _options.CardUrlProperty);
        if (string.IsNullOrWhiteSpace(url)) return null;

        var card = await scraperService.GetCardAsync(url, cancellationToken);
        if (card is null) return null;

        var updatePayload = BuildUpdatePayload(card, properties);
        var pageId = page.GetProperty("id").GetString()!;
        await notionClientService.UpdatePageAsync(pageId, updatePayload, cancellationToken);
        if (!string.IsNullOrWhiteSpace(card.ImageUrl))
        {
            await notionClientService.AppendImageBlockIfMissingAsync(pageId, card.ImageUrl, cancellationToken);
        }

        return new
        {
            pageId,
            card.Name,
            card.Number,
            card.PriceText,
            card.FoilPriceText,
            card.ReverseFoilPriceText,
            card.ImageUrl,
            updatedProperties = GetPayloadPropertyNames(updatePayload)
        };
    }

    private async Task<SyncLogResult> TryCreateSyncLogAsync(string status, string details, CancellationToken cancellationToken)
    {
        try
        {
            var result = await notionClientService.CreateSyncLogAsync(status, details, cancellationToken);
            if (!result.Created)
            {
                logger.LogWarning("Sync log was not created: {Reason}", result.SkippedReason);
            }

            return result;
        }
        catch (Exception ex)
        {
            // Logging must not make the sync endpoint fail after the sync has already run.
            logger.LogWarning(ex, "Sync log creation failed.");
            return SyncLogResult.Failed(ex.Message);
        }
    }

    private object BuildUpdatePayload(CardData card, JsonElement existingProperties)
    {
        var p = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        AddTitle(p, existingProperties, _options.CardNameProperty, card.Name);
        AddTextOrNumberIfPresent(p, existingProperties, _options.NumberProperty, card.Number);
        AddTextOrNumberIfPresent(p, existingProperties, _options.PriceProperty, card.PriceText, card.PriceValue);
        AddTextOrNumberIfPresent(p, existingProperties, _options.FoilPriceProperty, card.FoilPriceText, card.FoilPriceValue);
        AddTextOrNumberIfPresent(p, existingProperties, _options.ReverseFoilPriceProperty, card.ReverseFoilPriceText, card.ReverseFoilPriceValue);
        AddFilesIfPresent(p, existingProperties, _options.ImageProperty, card.ImageUrl);
        AddRichTextIfPresent(p, existingProperties, _options.TypeProperty, card.Type);
        AddRichTextIfPresent(p, existingProperties, _options.RarityProperty, card.Rarity);
        AddStatusIfPresent(p, existingProperties, _options.StatusProperty, _options.DoneStatusValue);

        return new { properties = p };
    }

    private static string[] GetPayloadPropertyNames(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("properties", out var properties))
        {
            return [];
        }

        return properties.EnumerateObject().Select(p => p.Name).ToArray();
    }

    private static void AddTitle(
        Dictionary<string, object?> properties,
        JsonElement existingProperties,
        string propertyName,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var titlePropertyName = HasPropertyType(existingProperties, propertyName, "title")
            ? propertyName
            : FindFirstPropertyNameByType(existingProperties, "title");

        if (titlePropertyName is not null)
        {
            properties[titlePropertyName] = Title(value);
        }
    }

    private static void AddRichTextIfPresent(
        Dictionary<string, object?> properties,
        JsonElement existingProperties,
        string propertyName,
        string? value)
    {
        if (HasPropertyType(existingProperties, propertyName, "rich_text"))
        {
            properties[propertyName] = RichText(value);
        }
    }

    private static void AddTextOrNumberIfPresent(
        Dictionary<string, object?> properties,
        JsonElement existingProperties,
        string propertyName,
        string? textValue,
        decimal? numberValue = null)
    {
        var propertyType = GetPropertyType(existingProperties, propertyName);
        if (propertyType is null || propertyType is "formula" or "rollup") return;

        if (propertyType == "number")
        {
            var parsed = numberValue ?? ParseFirstNumber(textValue);
            if (parsed.HasValue)
            {
                properties[propertyName] = Number(parsed.Value);
            }
            return;
        }

        if (propertyType == "rich_text")
        {
            properties[propertyName] = RichText(textValue);
        }
    }

    private static void AddFilesIfPresent(
        Dictionary<string, object?> properties,
        JsonElement existingProperties,
        string propertyName,
        string? value)
    {
        if (HasPropertyType(existingProperties, propertyName, "files"))
        {
            properties[propertyName] = Files(value);
        }
    }

    private static void AddStatusIfPresent(
        Dictionary<string, object?> properties,
        JsonElement existingProperties,
        string propertyName,
        string value)
    {
        if (HasPropertyType(existingProperties, propertyName, "status"))
        {
            properties[propertyName] = Status(value);
        }
    }

    private static bool HasPropertyType(JsonElement properties, string propertyName, string expectedType)
    {
        return string.Equals(GetPropertyType(properties, propertyName), expectedType, StringComparison.Ordinal);
    }

    private static string? GetPropertyType(JsonElement properties, string propertyName)
    {
        if (!properties.TryGetProperty(propertyName, out var prop)) return null;
        if (!prop.TryGetProperty("type", out var typeElement)) return null;
        return typeElement.GetString();
    }

    private static string? FindFirstPropertyNameByType(JsonElement properties, string expectedType)
    {
        foreach (var property in properties.EnumerateObject())
        {
            if (HasPropertyType(properties, property.Name, expectedType))
            {
                return property.Name;
            }
        }

        return null;
    }

    private static decimal? ParseFirstNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var digits = new string(value.TakeWhile(char.IsDigit).ToArray());
        return decimal.TryParse(digits, out var parsed) ? parsed : null;
    }

    private static object Title(string? value) => new
    {
        title = string.IsNullOrWhiteSpace(value)
            ? Array.Empty<object>()
            : new[] { new { text = new { content = value } } }
    };

    private static object RichText(string? value) => new
    {
        rich_text = string.IsNullOrWhiteSpace(value)
            ? Array.Empty<object>()
            : new[] { new { text = new { content = value } } }
    };

    private static object Number(decimal value) => new { number = value };
    private static object Status(string value) => new { status = new { name = value } };

    private static object Files(string? url) => new
    {
        files = string.IsNullOrWhiteSpace(url)
            ? Array.Empty<object>()
            : new[] { new { name = "card-image", external = new { url } } }
    };

    private static string? ReadNotionPlainText(JsonElement properties, string propertyName)
    {
        if (!properties.TryGetProperty(propertyName, out var prop)) return null;
        if (!prop.TryGetProperty("type", out var typeElement)) return null;
        var type = typeElement.GetString();

        return type switch
        {
            "url" => prop.TryGetProperty("url", out var url) ? url.GetString() : null,
            "title" => JoinText(prop.GetProperty("title")),
            "rich_text" => JoinText(prop.GetProperty("rich_text")),
            _ => null
        };
    }

    private static string? JoinText(JsonElement arr)
    {
        if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0) return null;
        var parts = new List<string>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.TryGetProperty("plain_text", out var text))
            {
                var value = text.GetString();
                if (!string.IsNullOrWhiteSpace(value)) parts.Add(value);
            }
        }

        return parts.Count == 0 ? null : string.Join(" ", parts);
    }
}
