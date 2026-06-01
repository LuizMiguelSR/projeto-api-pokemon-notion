using System.Text.Json;
using PokemonNotionApi.Models;
using PokemonNotionApi.Options;
using Microsoft.Extensions.Options;

namespace PokemonNotionApi.Services;

public sealed class SyncService(
    NotionClientService notionClientService,
    LigaPokemonScraperService scraperService,
    IOptions<NotionOptions> options)
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
        foreach (var page in results.EnumerateArray())
        {
            processed++;
            var pageId = page.GetProperty("id").GetString();
            if (string.IsNullOrWhiteSpace(pageId)) continue;

            var result = await SyncPageInternalAsync(page, cancellationToken);
            if (result is not null) updated++;
        }

        return new { processed, updated };
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

        var updatePayload = BuildUpdatePayload(card);
        var pageId = page.GetProperty("id").GetString()!;
        await notionClientService.UpdatePageAsync(pageId, updatePayload, cancellationToken);

        return new
        {
            pageId,
            card.Name,
            card.Number,
            card.PriceText,
            card.ImageUrl
        };
    }

    private object BuildUpdatePayload(CardData card)
    {
        var p = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            [_options.CardNameProperty] = Title(card.Name),
            [_options.NumberProperty] = RichText(card.Number),
            [_options.PriceProperty] = card.PriceValue.HasValue ? Number(card.PriceValue.Value) : RichText(card.PriceText),
            [_options.ImageProperty] = Files(card.ImageUrl),
            [_options.TypeProperty] = RichText(card.Type),
            [_options.RarityProperty] = RichText(card.Rarity),
            [_options.StatusProperty] = Status(_options.DoneStatusValue)
        };

        return new { properties = p };
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
