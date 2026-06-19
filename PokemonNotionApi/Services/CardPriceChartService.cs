using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using PokemonNotionApi.Models;
using PokemonNotionApi.Options;

namespace PokemonNotionApi.Services;

public sealed class CardPriceChartService(
    CardPriceHistoryRepository repository,
    NotionClientService notionClientService,
    IOptions<NotionOptions> options)
{
    private readonly NotionOptions _options = options.Value;

    public async Task<object> GetChartDataAsync(string pageId, int limit, CancellationToken cancellationToken)
    {
        var history = await repository.GetHistoryAsync(pageId, limit, cancellationToken);
        if (history.Count > 0)
        {
            return new
            {
                pageId,
                latest = history.LastOrDefault(),
                history
            };
        }

        var notionSnapshot = await GetCurrentNotionSnapshotAsync(pageId, cancellationToken);
        var fallbackHistory = notionSnapshot is null
            ? Array.Empty<CardPriceSnapshot>()
            : [notionSnapshot];

        return new
        {
            pageId,
            latest = notionSnapshot,
            history = fallbackHistory
        };
    }

    private async Task<CardPriceSnapshot?> GetCurrentNotionSnapshotAsync(string pageId, CancellationToken cancellationToken)
    {
        var page = await notionClientService.GetPageAsync(pageId, cancellationToken);
        if (page is null || !page.Value.TryGetProperty("properties", out var properties))
        {
            return null;
        }

        var normalPrice = ReadPrice(properties, _options.PriceProperty);
        var foilPrice = ReadPrice(properties, _options.FoilPriceProperty);
        var reverseFoilPrice = ReadPrice(properties, _options.ReverseFoilPriceProperty);
        if (!normalPrice.HasValue && !foilPrice.HasValue && !reverseFoilPrice.HasValue)
        {
            return null;
        }

        return new CardPriceSnapshot
        {
            PageId = pageId,
            CardName = ReadNotionPlainText(properties, _options.CardNameProperty),
            CardNumber = ReadNotionPlainText(properties, _options.NumberProperty),
            SourceUrl = ReadNotionPlainText(properties, _options.CardUrlProperty) ?? string.Empty,
            ImageUrl = ReadImageUrl(properties, _options.ImageProperty),
            NormalPrice = normalPrice,
            FoilPrice = foilPrice,
            ReverseFoilPrice = reverseFoilPrice,
            CapturedAt = DateTimeOffset.UtcNow
        };
    }

    private static decimal? ReadPrice(JsonElement properties, string propertyName)
    {
        return ParsePrice(ReadNotionPlainText(properties, propertyName));
    }

    private static decimal? ParsePrice(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var match = Regex.Match(value, @"\d+(?:[.,]\d+)?");
        if (!match.Success) return null;

        var normalized = NormalizePriceNumber(match.Value);

        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static string NormalizePriceNumber(string value)
    {
        if (value.Contains(',', StringComparison.Ordinal))
        {
            return value.Replace(".", string.Empty).Replace(',', '.');
        }

        return value;
    }

    private static string? ReadImageUrl(JsonElement properties, string propertyName)
    {
        if (!TryGetPropertyByName(properties, propertyName, out var prop)) return null;
        if (!prop.TryGetProperty("type", out var typeElement)) return null;

        if (typeElement.GetString() == "files" &&
            prop.TryGetProperty("files", out var files) &&
            files.ValueKind == JsonValueKind.Array &&
            files.GetArrayLength() > 0)
        {
            var first = files.EnumerateArray().First();
            if (first.TryGetProperty("external", out var external) &&
                external.TryGetProperty("url", out var externalUrl))
            {
                return externalUrl.GetString();
            }

            if (first.TryGetProperty("file", out var file) &&
                file.TryGetProperty("url", out var fileUrl))
            {
                return fileUrl.GetString();
            }
        }

        return ReadNotionPlainText(properties, propertyName);
    }

    private static string? ReadNotionPlainText(JsonElement properties, string propertyName)
    {
        if (!TryGetPropertyByName(properties, propertyName, out var prop)) return null;
        if (!prop.TryGetProperty("type", out var typeElement)) return null;
        var type = typeElement.GetString();

        return type switch
        {
            "url" => prop.TryGetProperty("url", out var url) ? url.GetString() : null,
            "number" => prop.TryGetProperty("number", out var number) ? number.ToString() : null,
            "title" => prop.TryGetProperty("title", out var title) ? JoinText(title) : null,
            "rich_text" => prop.TryGetProperty("rich_text", out var richText) ? JoinText(richText) : null,
            "formula" => ReadNotionFormulaValue(prop),
            "rollup" => ReadNotionRollupValue(prop),
            _ => null
        };
    }

    private static string? ReadNotionFormulaValue(JsonElement prop)
    {
        if (!prop.TryGetProperty("formula", out var formula) ||
            !formula.TryGetProperty("type", out var typeElement))
        {
            return null;
        }

        return typeElement.GetString() switch
        {
            "number" => formula.TryGetProperty("number", out var number) ? number.ToString() : null,
            "string" => formula.TryGetProperty("string", out var text) ? text.GetString() : null,
            _ => null
        };
    }

    private static string? ReadNotionRollupValue(JsonElement prop)
    {
        if (!prop.TryGetProperty("rollup", out var rollup) ||
            !rollup.TryGetProperty("type", out var typeElement))
        {
            return null;
        }

        if (typeElement.GetString() == "number" &&
            rollup.TryGetProperty("number", out var number))
        {
            return number.ToString();
        }

        return null;
    }

    private static bool TryGetPropertyByName(JsonElement properties, string propertyName, out JsonElement property)
    {
        if (properties.TryGetProperty(propertyName, out property))
        {
            return true;
        }

        var normalizedPropertyName = NormalizePropertyName(propertyName);
        foreach (var candidate in properties.EnumerateObject())
        {
            if (NormalizePropertyName(candidate.Name) == normalizedPropertyName)
            {
                property = candidate.Value;
                return true;
            }
        }

        property = default;
        return false;
    }

    private static string NormalizePropertyName(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
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
