using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PokemonNotionApi.Models;
using PokemonNotionApi.Options;
using Microsoft.Extensions.Options;

namespace PokemonNotionApi.Services;

public sealed class SyncService(
    NotionClientService notionClientService,
    LigaPokemonScraperService scraperService,
    CardPriceHistoryRepository priceHistoryRepository,
    IOptions<NotionOptions> options,
    IOptions<AppOptions> appOptions,
    ILogger<SyncService> logger)
{
    private readonly NotionOptions _options = options.Value;
    private readonly AppOptions _appOptions = appOptions.Value;

    public async Task<object> SyncDatabaseAsync(CancellationToken cancellationToken)
    {
        var db = await notionClientService.QueryDatabaseAsync(cancellationToken);
        if (db is null || !db.Value.TryGetProperty("results", out var results))
        {
            var unreadableResponse = new { processed = 0, updated = 0, failed = 1, errors = new[] { new { error = "notion_database_not_found_or_unreadable" } } };
            var unreadableDetails = JsonSerializer.Serialize(unreadableResponse, new JsonSerializerOptions { WriteIndented = true });
            var unreadableSyncLog = await TryCreateSyncLogAsync("Erro", unreadableDetails, cancellationToken);
            return new { unreadableResponse.processed, unreadableResponse.updated, unreadableResponse.failed, unreadableResponse.errors, syncLog = unreadableSyncLog };
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
                var result = await SyncPageInternalAsync(page, updateMetadata: false, appendImage: false, cancellationToken);
                if (result is not null) updated++;
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add(BuildError(pageId, ex));
            }
        }

        var response = new { processed, updated, failed, errors };
        var status = failed == 0 ? "Sucesso" : "Erro";
        var details = JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
        var syncLog = await TryCreateSyncLogAsync(status, details, cancellationToken);

        return new { processed, updated, failed, errors, syncLog };
    }

    public async Task<object> SyncDatabaseByLigaSearchAsync(CancellationToken cancellationToken)
    {
        var db = await notionClientService.QueryDatabaseAsync(cancellationToken);
        if (db is null || !db.Value.TryGetProperty("results", out var results))
        {
            var unreadableResponse = new { processed = 0, updated = 0, skipped = 0, failed = 1, errors = new[] { new { error = "notion_database_not_found_or_unreadable" } }, results = Array.Empty<object>() };
            var unreadableDetails = JsonSerializer.Serialize(unreadableResponse, new JsonSerializerOptions { WriteIndented = true });
            var unreadableSyncLog = await TryCreateSyncLogAsync("Erro", unreadableDetails, cancellationToken);
            return new { unreadableResponse.processed, unreadableResponse.updated, unreadableResponse.skipped, unreadableResponse.failed, unreadableResponse.errors, unreadableResponse.results, syncLog = unreadableSyncLog };
        }

        var processed = 0;
        var updated = 0;
        var skipped = 0;
        var failed = 0;
        var errors = new List<object>();
        var pageResults = new List<object>();
        foreach (var page in results.EnumerateArray())
        {
            var pageId = page.GetProperty("id").GetString();
            if (string.IsNullOrWhiteSpace(pageId)) continue;

            try
            {
                if (!HasStatus(page, _options.NotStartedStatusValue))
                {
                    skipped++;
                    continue;
                }

                processed++;
                var result = await SyncPageFromLigaSearchInternalAsync(page, cancellationToken);
                if (result is not null)
                {
                    pageResults.Add(result);
                    if (result.Updated) updated++;
                }
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add(BuildError(pageId, ex));
            }
        }

        var response = new { processed, updated, skipped, failed, errors, results = pageResults };
        var status = failed == 0 ? "Sucesso" : "Erro";
        var details = JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
        var syncLog = await TryCreateSyncLogAsync(status, details, cancellationToken);

        return new { processed, updated, skipped, failed, errors, results = pageResults, syncLog };
    }

    public async Task<object> SyncPageByIdAsync(string pageId, CancellationToken cancellationToken)
    {
        var db = await notionClientService.QueryDatabaseAsync(cancellationToken);
        if (db is null || !db.Value.TryGetProperty("results", out var results))
        {
            return new { pageId, updated = false, reason = "database_not_found" };
        }

        foreach (var page in results.EnumerateArray())
        {
            var candidatePageId = page.GetProperty("id").GetString();
            if (!string.Equals(candidatePageId, pageId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var result = await SyncPageInternalAsync(page, updateMetadata: false, appendImage: false, cancellationToken);
            return result is null
                ? new { pageId, updated = false, reason = "page_without_liga_url" }
                : new { pageId, updated = true, result };
        }

        return new { pageId, updated = false, reason = "page_not_found" };
    }

    public Task<SyncLogResult> CreateErrorLogAsync(string operation, Exception exception, CancellationToken cancellationToken)
    {
        var details = JsonSerializer.Serialize(new
        {
            operation,
            status = "Erro",
            error = new
            {
                type = exception.GetType().Name,
                message = exception.Message,
                stackTrace = exception.StackTrace
            }
        }, new JsonSerializerOptions { WriteIndented = true });

        return TryCreateSyncLogAsync("Erro", details, cancellationToken);
    }

    private async Task<object?> SyncPageInternalAsync(
        JsonElement page,
        bool updateMetadata,
        bool appendImage,
        CancellationToken cancellationToken)
    {
        var properties = page.GetProperty("properties");
        var url = ReadNotionPlainText(properties, _options.CardUrlProperty);
        if (string.IsNullOrWhiteSpace(url)) return null;

        var pageId = page.GetProperty("id").GetString()!;
        var card = await scraperService.GetCardAsync(url, cancellationToken);
        if (card is null)
        {
            var savedSnapshot = await SavePriceHistoryFromNotionPropertiesAsync(pageId, properties, url, cancellationToken);
            var chartPayload = BuildChartUrlPayload(properties, pageId);
            if (chartPayload is null)
            {
                return savedSnapshot
                    ? new
                    {
                        pageId,
                        reason = "liga_card_not_found",
                        savedSnapshot
                    }
                    : null;
            }

            await notionClientService.UpdatePageAsync(pageId, chartPayload, cancellationToken);
            return new
            {
                pageId,
                reason = "liga_card_not_found",
                savedSnapshot,
                updatedProperties = GetPayloadPropertyNames(chartPayload)
            };
        }

        var updatePayload = BuildUpdatePayload(card, properties, updateMetadata, pageId);
        await SavePriceHistoryAsync(pageId, card, cancellationToken);
        await notionClientService.UpdatePageAsync(pageId, updatePayload, cancellationToken);
        if (appendImage && !string.IsNullOrWhiteSpace(card.ImageUrl))
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

    private async Task<SearchSyncPageResult?> SyncPageFromLigaSearchInternalAsync(JsonElement page, CancellationToken cancellationToken)
    {
        var properties = page.GetProperty("properties");
        var name = ReadNotionPlainText(properties, _options.CardNameProperty);
        var number = ReadNotionPlainText(properties, _options.NumberProperty);
        var printedTotal = ReadNotionPlainText(properties, _options.PrintedTotalProperty);
        if (string.IsNullOrWhiteSpace(name) ||
            string.IsNullOrWhiteSpace(number) ||
            string.IsNullOrWhiteSpace(printedTotal))
        {
            return new SearchSyncPageResult(
                Updated: false,
                Partial: false,
                Reason: "missing_required_fields",
                PageId: page.GetProperty("id").GetString(),
                SourceUrl: null,
                SourceName: name,
                SourceNumber: number,
                SourcePrintedTotal: printedTotal);
        }

        var pageId = page.GetProperty("id").GetString()!;
        var searchResult = await scraperService.SearchCardAsync(name, number, printedTotal, cancellationToken);
        if (searchResult is null)
        {
            return new SearchSyncPageResult(
                Updated: false,
                Partial: false,
                Reason: "cardsearch_not_found",
                PageId: pageId,
                SourceUrl: null,
                SourceName: name,
                SourceNumber: number,
                SourcePrintedTotal: printedTotal);
        }

        var sourceUrl = searchResult.SourceUrl;
        var searchPayload = BuildSearchResultPayload(searchResult, properties, pageId);
        await notionClientService.UpdatePageAsync(pageId, searchPayload, cancellationToken);

        CardData? card;
        string? partialReason = null;
        try
        {
            card = await scraperService.GetCardAsync(sourceUrl, cancellationToken);
        }
        catch (LigaPokemonScraperException ex)
        {
            card = null;
            partialReason = ex.ReasonPhrase;
        }

        if (card is null)
        {
            if (!string.IsNullOrWhiteSpace(searchResult.ImageUrl))
            {
                await notionClientService.AppendImageBlockIfMissingAsync(pageId, searchResult.ImageUrl, cancellationToken);
            }

            return new SearchSyncPageResult(
                Updated: true,
                Partial: true,
                Reason: partialReason ?? "liga_card_not_found",
                PageId: pageId,
                SourceUrl: sourceUrl,
                SourceName: name,
                SourceNumber: number,
                SourcePrintedTotal: printedTotal,
                CardName: searchResult.Name,
                CardNumber: searchResult.Number,
                ImageUrl: searchResult.ImageUrl,
                UpdatedProperties: GetPayloadPropertyNames(searchPayload));
        }

        var updatePayload = BuildUpdatePayload(card, properties, updateMetadata: true, pageId);
        await SavePriceHistoryAsync(pageId, card, cancellationToken);
        await notionClientService.UpdatePageAsync(pageId, updatePayload, cancellationToken);
        if (!string.IsNullOrWhiteSpace(card.ImageUrl))
        {
            await notionClientService.AppendImageBlockIfMissingAsync(pageId, card.ImageUrl, cancellationToken);
        }

        return new SearchSyncPageResult(
            Updated: true,
            Partial: false,
            Reason: null,
            PageId: pageId,
            SourceUrl: sourceUrl,
            SourceName: name,
            SourceNumber: number,
            SourcePrintedTotal: printedTotal,
            CardName: card.Name,
            CardNumber: card.Number,
            PriceText: card.PriceText,
            FoilPriceText: card.FoilPriceText,
            ReverseFoilPriceText: card.ReverseFoilPriceText,
            ImageUrl: card.ImageUrl,
            UpdatedProperties: GetPayloadPropertyNames(updatePayload));
    }

    private bool HasStatus(JsonElement page, string expectedStatus)
    {
        if (string.IsNullOrWhiteSpace(expectedStatus) ||
            !page.TryGetProperty("properties", out var properties))
        {
            return false;
        }

        var status = ReadNotionPlainText(properties, _options.StatusProperty);
        return string.Equals(status, expectedStatus, StringComparison.OrdinalIgnoreCase);
    }

    private static object BuildError(string? pageId, Exception exception)
    {
        if (exception is LigaPokemonScraperException scraperException)
        {
            return new
            {
                pageId,
                type = exception.GetType().Name,
                message = exception.Message,
                statusCode = scraperException.StatusCode,
                reasonPhrase = scraperException.ReasonPhrase,
                sourceUrl = scraperException.SourceUrl,
                responsePreview = scraperException.ResponsePreview
            };
        }

        return new
        {
            pageId,
            type = exception.GetType().Name,
            message = exception.Message
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

    private object BuildUpdatePayload(CardData card, JsonElement existingProperties, bool updateMetadata, string pageId)
    {
        var p = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        AddTextOrNumberIfPresent(p, existingProperties, _options.PriceProperty, card.PriceText, card.PriceValue);
        AddTextOrNumberIfPresent(p, existingProperties, _options.FoilPriceProperty, card.FoilPriceText, card.FoilPriceValue);
        AddTextOrNumberIfPresent(p, existingProperties, _options.ReverseFoilPriceProperty, card.ReverseFoilPriceText, card.ReverseFoilPriceValue);
        AddUrlIfPresent(p, existingProperties, _options.ChartUrlProperty, GetChartUrl(pageId));

        if (updateMetadata)
        {
            AddTitle(p, existingProperties, _options.CardNameProperty, card.Name);
            AddTextOrNumberIfPresent(p, existingProperties, _options.NumberProperty, card.Number);
            AddFilesIfPresent(p, existingProperties, _options.ImageProperty, card.ImageUrl);
            AddRichTextIfPresent(p, existingProperties, _options.TypeProperty, card.Type);
            AddRichTextIfPresent(p, existingProperties, _options.RarityProperty, card.Rarity);
            AddUrlIfPresent(p, existingProperties, _options.CardUrlProperty, card.SourceUrl);
            AddStatusIfPresent(p, existingProperties, _options.StatusProperty, _options.DoneStatusValue);
        }

        return new { properties = p };
    }

    private object BuildLigaUrlPayload(string sourceUrl, JsonElement existingProperties)
    {
        var p = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        AddUrlIfPresent(p, existingProperties, _options.CardUrlProperty, sourceUrl);
        return new { properties = p };
    }

    private object BuildLigaUrlPayload(string sourceUrl, JsonElement existingProperties, string pageId)
    {
        var p = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        AddUrlIfPresent(p, existingProperties, _options.CardUrlProperty, sourceUrl);
        AddUrlIfPresent(p, existingProperties, _options.ChartUrlProperty, GetChartUrl(pageId));
        return new { properties = p };
    }

    private object BuildSearchResultPayload(LigaPokemonCardSearchResult searchResult, JsonElement existingProperties, string pageId)
    {
        var p = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        AddTitle(p, existingProperties, _options.CardNameProperty, searchResult.Name);
        AddTextOrNumberIfPresent(p, existingProperties, _options.NumberProperty, searchResult.Number);
        AddFilesIfPresent(p, existingProperties, _options.ImageProperty, searchResult.ImageUrl);
        AddUrlIfPresent(p, existingProperties, _options.CardUrlProperty, searchResult.SourceUrl);
        AddUrlIfPresent(p, existingProperties, _options.ChartUrlProperty, GetChartUrl(pageId));
        AddStatusIfPresent(p, existingProperties, _options.StatusProperty, _options.DoneStatusValue);
        return new { properties = p };
    }

    private object? BuildChartUrlPayload(JsonElement existingProperties, string pageId)
    {
        var p = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        AddUrlIfPresent(p, existingProperties, _options.ChartUrlProperty, GetChartUrl(pageId));
        return p.Count == 0 ? null : new { properties = p };
    }

    private async Task SavePriceHistoryAsync(string pageId, CardData card, CancellationToken cancellationToken)
    {
        if (!card.PriceValue.HasValue &&
            !card.FoilPriceValue.HasValue &&
            !card.ReverseFoilPriceValue.HasValue)
        {
            return;
        }

        await priceHistoryRepository.SaveAsync(new CardPriceSnapshot
        {
            PageId = pageId,
            CardName = card.Name,
            CardNumber = card.Number,
            SourceUrl = card.SourceUrl,
            ImageUrl = card.ImageUrl,
            NormalPrice = card.PriceValue,
            FoilPrice = card.FoilPriceValue,
            ReverseFoilPrice = card.ReverseFoilPriceValue,
            CapturedAt = DateTimeOffset.UtcNow
        }, cancellationToken);
    }

    private async Task<bool> SavePriceHistoryFromNotionPropertiesAsync(
        string pageId,
        JsonElement properties,
        string sourceUrl,
        CancellationToken cancellationToken)
    {
        var normalPrice = ParsePrice(ReadNotionPlainText(properties, _options.PriceProperty));
        var foilPrice = ParsePrice(ReadNotionPlainText(properties, _options.FoilPriceProperty));
        var reverseFoilPrice = ParsePrice(ReadNotionPlainText(properties, _options.ReverseFoilPriceProperty));
        if (!normalPrice.HasValue &&
            !foilPrice.HasValue &&
            !reverseFoilPrice.HasValue)
        {
            return false;
        }

        return await priceHistoryRepository.SaveAsync(new CardPriceSnapshot
        {
            PageId = pageId,
            CardName = ReadNotionPlainText(properties, _options.CardNameProperty),
            CardNumber = ReadNotionPlainText(properties, _options.NumberProperty),
            SourceUrl = sourceUrl,
            ImageUrl = ReadNotionImageUrl(properties, _options.ImageProperty),
            NormalPrice = normalPrice,
            FoilPrice = foilPrice,
            ReverseFoilPrice = reverseFoilPrice,
            CapturedAt = DateTimeOffset.UtcNow
        }, cancellationToken);
    }

    private string GetChartUrl(string pageId)
    {
        var baseUrl = string.IsNullOrWhiteSpace(_appOptions.PublicBaseUrl)
            ? "http://localhost:8090"
            : _appOptions.PublicBaseUrl.TrimEnd('/');

        return $"{baseUrl}/cards/{Uri.EscapeDataString(pageId)}/prices";
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

    private static void AddUrlIfPresent(
        Dictionary<string, object?> properties,
        JsonElement existingProperties,
        string propertyName,
        string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var propertyType = GetPropertyType(existingProperties, propertyName);
        if (propertyType == "url")
        {
            properties[propertyName] = new { url = value };
        }
        else if (propertyType == "rich_text")
        {
            properties[propertyName] = RichText(value);
        }
    }

    private static bool HasPropertyType(JsonElement properties, string propertyName, string expectedType)
    {
        return string.Equals(GetPropertyType(properties, propertyName), expectedType, StringComparison.Ordinal);
    }

    private static string? GetPropertyType(JsonElement properties, string propertyName)
    {
        if (!TryGetPropertyByName(properties, propertyName, out var prop)) return null;
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

    private static decimal? ParsePrice(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var match = Regex.Match(value, @"\d+(?:[.,]\d+)?");
        if (!match.Success) return null;

        var normalized = match.Value.Contains(',', StringComparison.Ordinal)
            ? match.Value.Replace(".", string.Empty).Replace(',', '.')
            : match.Value;

        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static string? ReadNotionImageUrl(JsonElement properties, string propertyName)
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
        if (!TryGetPropertyByName(properties, propertyName, out var prop)) return null;
        if (!prop.TryGetProperty("type", out var typeElement)) return null;
        var type = typeElement.GetString();

        return type switch
        {
            "url" => prop.TryGetProperty("url", out var url) ? url.GetString() : null,
            "number" => prop.TryGetProperty("number", out var number) ? number.ToString() : null,
            "title" => JoinText(prop.GetProperty("title")),
            "rich_text" => JoinText(prop.GetProperty("rich_text")),
            "formula" => ReadNotionFormulaValue(prop),
            "rollup" => ReadNotionRollupValue(prop),
            "unique_id" => ReadNotionUniqueIdValue(prop),
            "select" => ReadNotionSelectValue(prop),
            "status" => ReadNotionStatusValue(prop),
            _ => null
        };
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

    private static string? ReadNotionStatusValue(JsonElement prop)
    {
        if (!prop.TryGetProperty("status", out var status) ||
            status.ValueKind != JsonValueKind.Object ||
            !status.TryGetProperty("name", out var name))
        {
            return null;
        }

        return name.GetString();
    }

    private static string? ReadNotionSelectValue(JsonElement prop)
    {
        if (!prop.TryGetProperty("select", out var select) ||
            select.ValueKind != JsonValueKind.Object ||
            !select.TryGetProperty("name", out var name))
        {
            return null;
        }

        return name.GetString();
    }

    private static string? ReadNotionFormulaValue(JsonElement prop)
    {
        if (!prop.TryGetProperty("formula", out var formula) ||
            !formula.TryGetProperty("type", out var typeElement))
        {
            return null;
        }

        var type = typeElement.GetString();
        return type switch
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

        var type = typeElement.GetString();
        return type switch
        {
            "number" => rollup.TryGetProperty("number", out var number) ? number.ToString() : null,
            "array" => ReadFirstRollupArrayValue(rollup),
            _ => null
        };
    }

    private static string? ReadFirstRollupArrayValue(JsonElement rollup)
    {
        if (!rollup.TryGetProperty("array", out var array) ||
            array.ValueKind != JsonValueKind.Array ||
            array.GetArrayLength() == 0)
        {
            return null;
        }

        var first = array.EnumerateArray().First();
        if (!first.TryGetProperty("type", out var typeElement))
        {
            return null;
        }

        return typeElement.GetString() switch
        {
            "number" => first.TryGetProperty("number", out var number) ? number.ToString() : null,
            "title" => first.TryGetProperty("title", out var title) ? JoinText(title) : null,
            "rich_text" => first.TryGetProperty("rich_text", out var richText) ? JoinText(richText) : null,
            "formula" => ReadNotionFormulaValue(first),
            _ => null
        };
    }

    private static string? ReadNotionUniqueIdValue(JsonElement prop)
    {
        if (!prop.TryGetProperty("unique_id", out var uniqueId) ||
            !uniqueId.TryGetProperty("number", out var number))
        {
            return null;
        }

        return number.ToString();
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

    private sealed record SearchSyncPageResult(
        bool Updated,
        bool Partial,
        string? Reason,
        string? PageId,
        string? SourceUrl,
        string? SourceName,
        string? SourceNumber,
        string? SourcePrintedTotal,
        string? CardName = null,
        string? CardNumber = null,
        string? PriceText = null,
        string? FoilPriceText = null,
        string? ReverseFoilPriceText = null,
        string? ImageUrl = null,
        string[]? UpdatedProperties = null);

}


