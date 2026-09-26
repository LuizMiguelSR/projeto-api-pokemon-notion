using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using PokemonNotionApi.Models;
using PokemonNotionApi.Options;
using Microsoft.Extensions.Options;

namespace PokemonNotionApi.Services;

public sealed class LigaPokemonScraperService(
    HttpClient httpClient,
    ScraplingPageFetcher scrapling,
    IOptions<LigaPokemonOptions> options,
    ILogger<LigaPokemonScraperService> logger)
{
    private readonly LigaPokemonOptions _options = options.Value;

    public Task<CardData?> GetCardByNumberAndEditionAsync(
        string number,
        string editionCode,
        CancellationToken cancellationToken)
    {
        var searchUrl = BuildCardSearchUrl(number, editionCode);
        return string.IsNullOrWhiteSpace(searchUrl)
            ? Task.FromResult<CardData?>(null)
            : GetCardAsync(searchUrl, cancellationToken);
    }

    public Task<CardData?> GetCardByNameAndPrintedNumberAsync(
        string name,
        string number,
        string printedTotal,
        CancellationToken cancellationToken)
    {
        var searchUrl = BuildCardUrl(name, number, printedTotal);
        return string.IsNullOrWhiteSpace(searchUrl)
            ? Task.FromResult<CardData?>(null)
            : GetCardAsync(searchUrl, cancellationToken);
    }

    public string? GetCardUrlByNameAndPrintedNumber(string name, string number, string printedTotal)
    {
        return BuildCardUrl(name, number, printedTotal);
    }

    public async Task<LigaPokemonCardSearchResult?> SearchCardAsync(
        string name,
        string number,
        string printedTotal,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            string.IsNullOrWhiteSpace(number) ||
            string.IsNullOrWhiteSpace(printedTotal))
        {
            return null;
        }

        var normalizedNumber = NormalizeCardNumber(number);
        var normalizedTotal = NormalizeCardNumber(printedTotal);
        if (string.IsNullOrWhiteSpace(normalizedNumber) || string.IsNullOrWhiteSpace(normalizedTotal))
        {
            return null;
        }

        var query = $"{name.Trim()} ({normalizedNumber}/{normalizedTotal})";
        var searchUrl = $"https://www.clubedaliga.com.br/api/cardsearch?tcg=2&maxQuantity=8&maintype=1&query={Uri.EscapeDataString(query)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, searchUrl);
        request.Headers.UserAgent.ParseAdd(_options.UserAgent);
        request.Headers.Accept.ParseAdd("application/json,text/plain,*/*");
        request.Headers.AcceptLanguage.ParseAdd(_options.AcceptLanguage);
        AddCookieHeader(request);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array ||
            data.GetArrayLength() == 0)
        {
            return null;
        }

        foreach (var item in data.EnumerateArray())
        {
            var suggestionName = ReadString(item, "sNomeIdiomaPrincipal") ?? ReadString(item, "sNomeIdiomaSecundario");
            var (cardName, cardNumber) = ParseNameAndNumber(suggestionName);
            if (!IsSameCardNumber(cardNumber, normalizedNumber))
            {
                continue;
            }

            var imageUrl = NormalizeUrl(ReadString(item, "sPathImage"));
            var sourceUrl = BuildCardUrl(cardName ?? name, normalizedNumber, normalizedTotal);
            if (sourceUrl is null)
            {
                continue;
            }

            return new LigaPokemonCardSearchResult(
                Query: query,
                Name: cardName ?? suggestionName ?? name,
                Number: cardNumber ?? normalizedNumber,
                PrintedTotal: normalizedTotal,
                ImageUrl: imageUrl,
                SourceUrl: sourceUrl,
                SearchUrl: searchUrl,
                Key: ReadString(item, "__key"));
        }

        return null;
    }

    public async Task<CardData?> GetCardAsync(string sourceUrl, CancellationToken cancellationToken)
    {
        var (statusCode, html) = await scrapling.FetchAsync(sourceUrl, cancellationToken);
        var reasonPhrase = ((HttpStatusCode)statusCode).ToString();

        if (statusCode is < 200 or >= 300)
        {
            logger.LogWarning(
                "Liga Pokemon returned HTTP {StatusCode} for {SourceUrl}. Reason={ReasonPhrase}. Preview={Preview}",
                statusCode,
                sourceUrl,
                reasonPhrase,
                BuildPreview(html));

            throw new LigaPokemonScraperException(
                IsCloudflareChallenge(html)
                    ? "Liga Pokemon blocked or rejected the request."
                    : "Liga Pokemon returned a non-success response.",
                statusCode,
                IsCloudflareChallenge(html)
                    ? "Cloudflare or anti-bot page returned instead of card page"
                    : reasonPhrase ?? "HTTP request failed",
                sourceUrl,
                BuildPreview(html));
        }

        var document = await ParseHtmlAsync(html, cancellationToken);
        var prices = ExtractPrices(document);

        // Successful card pages may still include Cloudflare scripts.
        // Only classify these markers as a challenge when no prices were loaded.
        if (!HasAnyPrice(prices) && IsCloudflareChallenge(html))
        {
            logger.LogWarning(
                "Liga Pokemon Cloudflare challenge detected for {SourceUrl}. StatusCode={StatusCode}. Preview={Preview}",
                sourceUrl,
                statusCode,
                BuildPreview(html));

            throw new LigaPokemonScraperException(
                "Liga Pokemon blocked or rejected the request.",
                statusCode,
                "Cloudflare or anti-bot page returned instead of card page",
                sourceUrl,
                BuildPreview(html));
        }

        var title = ExtractTitle(document);
        var image = ExtractCardImage(document) ?? ExtractMetaContent(document, "og:image");
        var rarity = ExtractByLabel(document, "Raridade");
        var type = ExtractByLabel(document, "Tipo");

        if (!HasAnyPrice(prices))
        {
            logger.LogWarning(
                "Liga Pokemon page without price data for {SourceUrl}. Title={Title}. Preview={Preview}",
                sourceUrl,
                title,
                BuildPreview(html));

            throw new LigaPokemonScraperException(
                "Liga Pokemon returned a page, but no price data could be extracted.",
                statusCode,
                "Card page HTML did not contain price data",
                sourceUrl,
                BuildPreview(html));
        }

        var (name, number) = ParseNameAndNumber(title);
        number ??= ExtractEditionNumber(document);
        logger.LogInformation(
            "Liga Pokemon prices extracted url={SourceUrl} name={Name} number={Number} normal={Normal} foil={Foil} reverse={Reverse}",
            sourceUrl,
            name ?? title,
            number,
            prices.Normal,
            prices.Foil,
            prices.ReverseFoil);

        return new CardData
        {
            Name = name ?? title,
            Number = number,
            PriceText = prices.Normal.HasValue ? FormatPrice(prices.Normal.Value) : null,
            PriceValue = prices.Normal,
            FoilPriceText = prices.Foil.HasValue ? FormatPrice(prices.Foil.Value) : null,
            FoilPriceValue = prices.Foil,
            ReverseFoilPriceText = prices.ReverseFoil.HasValue ? FormatPrice(prices.ReverseFoil.Value) : null,
            ReverseFoilPriceValue = prices.ReverseFoil,
            ImageUrl = image,
            Type = type,
            Rarity = rarity,
            SourceUrl = sourceUrl
        };
    }

    private string? BuildCardSearchUrl(string number, string editionCode)
    {
        if (string.IsNullOrWhiteSpace(number) || string.IsNullOrWhiteSpace(editionCode))
        {
            return null;
        }

        var normalizedNumber = NormalizeCardNumber(number);
        if (string.IsNullOrWhiteSpace(normalizedNumber))
        {
            return null;
        }

        var query = Uri.EscapeDataString($"{normalizedNumber} ed={editionCode.Trim()}");
        return $"{_options.BaseUrl.TrimEnd('/')}/?view=cards%2Fsearch&tipo=1&card={query}&searchprod=0";
    }

    private string? BuildCardUrl(string name, string number, string printedTotal)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            string.IsNullOrWhiteSpace(number) ||
            string.IsNullOrWhiteSpace(printedTotal))
        {
            return null;
        }

        var normalizedNumber = NormalizeCardNumber(number);
        var normalizedTotal = NormalizeCardNumber(printedTotal);
        if (string.IsNullOrWhiteSpace(normalizedNumber) || string.IsNullOrWhiteSpace(normalizedTotal))
        {
            return null;
        }

        var query = Uri.EscapeDataString($"{name.Trim()} ({normalizedNumber}/{normalizedTotal})");
        return $"{_options.BaseUrl.TrimEnd('/')}/?view=cards%2Fcard&tipo=1&card={query}";
    }

    private static string? NormalizeCardNumber(string number)
    {
        var cleanNumber = CleanText(number);
        if (string.IsNullOrWhiteSpace(cleanNumber))
        {
            return null;
        }

        var slashIndex = cleanNumber.IndexOf('/');
        if (slashIndex >= 0)
        {
            cleanNumber = cleanNumber[..slashIndex];
        }

        var digits = new string(cleanNumber.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var parsed)
            ? parsed.ToString("000", CultureInfo.InvariantCulture)
            : cleanNumber;
    }

    private static bool HasAnyPrice((decimal? Normal, decimal? Foil, decimal? ReverseFoil) prices)
    {
        return prices.Normal.HasValue || prices.Foil.HasValue || prices.ReverseFoil.HasValue;
    }

    private static async Task<IHtmlDocument> ParseHtmlAsync(string html, CancellationToken cancellationToken)
    {
        var parser = new HtmlParser();
        return await parser.ParseDocumentAsync(html, cancellationToken);
    }

    private static (decimal? Normal, decimal? Foil, decimal? ReverseFoil) ExtractPrices(IHtmlDocument document)
    {
        var runtimePrices = ExtractPricesFromRuntimeData(document);
        if (HasAnyPrice(runtimePrices))
        {
            return runtimePrices;
        }

        var html = document.DocumentElement.OuterHtml;
        var match = Regex.Match(html, @"var\s+cards_editions\s*=\s*(?<value>\[[\s\S]*?\]);", RegexOptions.IgnoreCase);
        if (!match.Success) return (null, null, null);

        try
        {
            using var doc = JsonDocument.Parse(match.Groups["value"].Value);
            return ReadPricesFromCardsEditions(doc.RootElement);
        }
        catch (JsonException)
        {
            return (null, null, null);
        }
    }

    private static (decimal? Normal, decimal? Foil, decimal? ReverseFoil) ExtractPricesFromRuntimeData(IHtmlDocument document)
    {
        var script = document.QuerySelector("#liga-pokemon-runtime-data");
        if (script?.TextContent is not { Length: > 0 } scriptText)
        {
            return (null, null, null);
        }

        try
        {
            var json = WebUtility.HtmlDecode(scriptText);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("cards_editions", out var cardsEditions) &&
                cardsEditions.ValueKind == JsonValueKind.Array)
            {
                var prices = ReadPricesFromCardsEditions(cardsEditions);
                if (HasAnyPrice(prices))
                {
                    return prices;
                }
            }

            if (doc.RootElement.TryGetProperty("globals", out var globals) &&
                globals.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in globals.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    var prices = ReadPricesFromCardsEditions(property.Value);
                    if (HasAnyPrice(prices))
                    {
                        return prices;
                    }
                }
            }
        }
        catch (JsonException)
        {
            return (null, null, null);
        }

        return (null, null, null);
    }

    private static (decimal? Normal, decimal? Foil, decimal? ReverseFoil) ReadPricesFromCardsEditions(JsonElement cardsEditions)
    {
        if (cardsEditions.ValueKind != JsonValueKind.Array)
        {
            return (null, null, null);
        }

        foreach (var edition in cardsEditions.EnumerateArray())
        {
            if (edition.ValueKind != JsonValueKind.Object ||
                !edition.TryGetProperty("price", out var price))
            {
                continue;
            }

            var prices = (
                ReadAveragePrice(price, "0"),
                ReadAveragePrice(price, "2"),
                ReadAveragePrice(price, "3")
            );
            if (HasAnyPrice(prices))
            {
                return prices;
            }
        }

        return (null, null, null);
    }

    private static decimal? ReadAveragePrice(JsonElement price, string key)
    {
        if (price.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!price.TryGetProperty(key, out var entry)) return null;
        if (entry.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!entry.TryGetProperty("m", out var average) &&
            !entry.TryGetProperty("avg", out average) &&
            !entry.TryGetProperty("average", out average) &&
            !entry.TryGetProperty("preco", out average) &&
            !entry.TryGetProperty("price", out average))
        {
            return null;
        }

        return ReadDecimal(average);
    }

    private static decimal? ReadDecimal(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
            JsonValueKind.String when decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var invariant) => invariant,
            JsonValueKind.String when decimal.TryParse(value.GetString(), NumberStyles.Number, new CultureInfo("pt-BR"), out var ptBr) => ptBr,
            _ => null
        };
    }

    private static string? ExtractEditionNumber(IHtmlDocument document)
    {
        var html = document.DocumentElement.OuterHtml;
        var match = Regex.Match(html, @"var\s+cards_editions\s*=\s*(?<value>\[[\s\S]*?\]);", RegexOptions.IgnoreCase);
        if (!match.Success) return null;

        try
        {
            using var doc = JsonDocument.Parse(match.Groups["value"].Value);
            var firstEdition = doc.RootElement.EnumerateArray().FirstOrDefault();
            if (firstEdition.ValueKind != JsonValueKind.Object ||
                !firstEdition.TryGetProperty("num", out var number))
            {
                return null;
            }

            return CleanText(number.GetString());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string FormatPrice(decimal value)
    {
        return $"R$ {value.ToString("N2", new CultureInfo("pt-BR"))}";
    }

    private static string? ExtractTitle(IHtmlDocument document)
    {
        return CleanText(document.QuerySelector("h1")?.TextContent)
            ?? ExtractMetaContent(document, "og:title");
    }

    private static string? ExtractMetaContent(IHtmlDocument document, string property)
    {
        var selector = $"meta[property='{property}'],meta[name='{property}']";
        return NormalizeUrl(CleanText(document.QuerySelector(selector)?.GetAttribute("content")));
    }

    private static string? ExtractByLabel(IHtmlDocument document, string label)
    {
        foreach (var element in document.All)
        {
            if (!string.Equals(CleanText(element.TextContent), label, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = FindNextTextValue(element);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string? FindNextTextValue(IElement element)
    {
        for (var sibling = element.NextElementSibling; sibling is not null; sibling = sibling.NextElementSibling)
        {
            var text = CleanText(sibling.TextContent);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        var parent = element.ParentElement;
        if (parent is null)
        {
            return null;
        }

        for (var sibling = parent.NextElementSibling; sibling is not null; sibling = sibling.NextElementSibling)
        {
            var text = CleanText(sibling.TextContent);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }

    private static string? ExtractCardImage(IHtmlDocument document)
    {
        var attributes = new[] { "src", "data-src", "href", "data-bg" };
        foreach (var element in document.All)
        {
            foreach (var attribute in attributes)
            {
                var imageUrl = NormalizeCardImageUrl(element.GetAttribute(attribute));
                if (!string.IsNullOrWhiteSpace(imageUrl))
                {
                    return imageUrl;
                }
            }

            var styleUrl = ExtractCardImageFromStyle(element.GetAttribute("style"));
            if (!string.IsNullOrWhiteSpace(styleUrl))
            {
                return styleUrl;
            }
        }

        return null;
    }

    private static string? ExtractCardImageFromStyle(string? style)
    {
        if (string.IsNullOrWhiteSpace(style))
        {
            return null;
        }

        var match = Regex.Match(style, @"url\((?<value>[^)]+)\)", RegexOptions.IgnoreCase);
        return match.Success
            ? NormalizeCardImageUrl(match.Groups["value"].Value.Trim().Trim('\'', '"'))
            : null;
    }

    private static string? NormalizeCardImageUrl(string? value)
    {
        var imageUrl = NormalizeUrl(CleanText(WebUtility.HtmlDecode(value)));
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            return null;
        }

        return imageUrl.Contains("repositorio.sbrauble.com/arquivos/in/pokemon", StringComparison.OrdinalIgnoreCase) &&
            Regex.IsMatch(imageUrl, @"\.(?:jpg|jpeg|png|webp)(?:\?|$)", RegexOptions.IgnoreCase)
            ? imageUrl
            : null;
    }

    private static string? NormalizeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.StartsWith("//", StringComparison.Ordinal)) return $"https:{value}";
        return value;
    }

    private static string? ReadString(JsonElement item, string propertyName)
    {
        return item.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? CleanText(value.GetString())
            : null;
    }

    private static bool IsSameCardNumber(string? actual, string expected)
    {
        if (string.IsNullOrWhiteSpace(actual)) return false;
        return string.Equals(NormalizeCardNumber(actual), expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCloudflareChallenge(string html)
    {
        return html.Contains("<title>Just a moment...</title>", StringComparison.OrdinalIgnoreCase) ||
            html.Contains("<title>Um momento", StringComparison.OrdinalIgnoreCase) ||
            html.Contains("Um momento", StringComparison.OrdinalIgnoreCase) ||
            html.Contains("cdn-cgi/challenge-platform", StringComparison.OrdinalIgnoreCase) ||
            html.Contains("cf-browser-verification", StringComparison.OrdinalIgnoreCase) ||
            html.Contains("cf-turnstile-response", StringComparison.OrdinalIgnoreCase) ||
            html.Contains("challenges.cloudflare.com", StringComparison.OrdinalIgnoreCase) ||
            html.Contains("__cf_chl_", StringComparison.OrdinalIgnoreCase) ||
            html.Contains("Executando verificação de segurança", StringComparison.OrdinalIgnoreCase) ||
            html.Contains("verifica se você não é um bot", StringComparison.OrdinalIgnoreCase) ||
            html.Contains("Enable JavaScript and cookies to continue", StringComparison.OrdinalIgnoreCase);
    }

    private void AddCookieHeader(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(_options.Cookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", _options.Cookie);
        }
    }

    private static string BuildPreview(string html)
    {
        var preview = CleanText(Regex.Replace(html, "<[^>]+>", " "));
        if (string.IsNullOrWhiteSpace(preview))
        {
            preview = html;
        }

        return preview.Length <= 500 ? preview : preview[..500];
    }

    private static (string? Name, string? Number) ParseNameAndNumber(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return (null, null);
        var pipeIndex = fullName.IndexOf('|');
        if (pipeIndex >= 0)
        {
            fullName = fullName[..pipeIndex].Trim();
        }

        var match = Regex.Match(fullName, @"^(?<name>.+?)\s*\((?<num>[^)]+)\)\s*$");
        if (!match.Success) return (CleanText(fullName), null);

        var number = CleanText(match.Groups["num"].Value);
        var slashIndex = number?.IndexOf('/') ?? -1;
        if (slashIndex >= 0)
        {
            number = number![..slashIndex];
        }

        return (CleanText(match.Groups["name"].Value), number);
    }

    private static string? CleanText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return Regex.Replace(value, @"\s+", " ").Trim();
    }
}

public sealed class LigaPokemonScraperException(
    string message,
    int statusCode,
    string reasonPhrase,
    string sourceUrl,
    string responsePreview) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string ReasonPhrase { get; } = reasonPhrase;
    public string SourceUrl { get; } = sourceUrl;
    public string ResponsePreview { get; } = responsePreview;
}

public sealed record LigaPokemonCardSearchResult(
    string Query,
    string Name,
    string Number,
    string PrintedTotal,
    string? ImageUrl,
    string SourceUrl,
    string SearchUrl,
    string? Key);

