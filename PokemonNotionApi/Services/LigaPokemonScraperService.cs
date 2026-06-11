using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using PokemonNotionApi.Models;
using PokemonNotionApi.Options;
using Microsoft.Extensions.Options;

namespace PokemonNotionApi.Services;

public sealed class LigaPokemonScraperService(HttpClient httpClient, IOptions<LigaPokemonOptions> options)
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

    public async Task<CardData?> GetCardAsync(string sourceUrl, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, sourceUrl);
        request.Headers.UserAgent.ParseAdd(_options.UserAgent);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;

        var html = await response.Content.ReadAsStringAsync(cancellationToken);

        var title = ExtractGroup(html, @"<h1[^>]*>\s*(?<value>[^<]+)\s*</h1>")
            ?? ExtractGroup(html, "property=\"og:title\" content=\"(?<value>[^\"]+)\"");
        var image = ExtractCardImage(html) ?? ExtractGroup(html, "property=\"og:image\" content=\"(?<value>[^\"]+)\"");
        var prices = ExtractPrices(html);
        var rarity = ExtractByLabel(html, "Raridade");
        var type = ExtractByLabel(html, "Tipo");
        var (name, number) = ParseNameAndNumber(title);
        number ??= ExtractEditionNumber(html);

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

    private static (decimal? Normal, decimal? Foil, decimal? ReverseFoil) ExtractPrices(string html)
    {
        var match = Regex.Match(html, @"var\s+cards_editions\s*=\s*(?<value>\[[\s\S]*?\]);", RegexOptions.IgnoreCase);
        if (!match.Success) return (null, null, null);

        try
        {
            using var doc = JsonDocument.Parse(match.Groups["value"].Value);
            var firstEdition = doc.RootElement.EnumerateArray().FirstOrDefault();
            if (firstEdition.ValueKind != JsonValueKind.Object ||
                !firstEdition.TryGetProperty("price", out var price))
            {
                return (null, null, null);
            }

            return (
                ReadAveragePrice(price, "0"),
                ReadAveragePrice(price, "2"),
                ReadAveragePrice(price, "3")
            );
        }
        catch (JsonException)
        {
            return (null, null, null);
        }
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

        if (!entry.TryGetProperty("m", out var average)) return null;
        return decimal.TryParse(average.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static string? ExtractEditionNumber(string html)
    {
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

    private static string? ExtractByLabel(string html, string label)
    {
        var pattern = $"{Regex.Escape(label)}[\\s\\S]*?<div[^>]*>\\s*(?<value>[^<]+)\\s*</div>";
        return CleanText(ExtractGroup(html, pattern));
    }

    private static string? ExtractCardImage(string html)
    {
        var patterns = new[]
        {
            @"(?:src|data-src)=[""'](?<value>[^""']*repositorio\.sbrauble\.com/arquivos/in/pokemon[^""']+\.(?:jpg|jpeg|png|webp))[""']",
            @"url\((?<value>[^)]*repositorio\.sbrauble\.com/arquivos/in/pokemon[^)]+\.(?:jpg|jpeg|png|webp))\)"
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
            if (!match.Success) continue;

            var imageUrl = WebUtility.HtmlDecode(match.Groups["value"].Value.Trim().Trim('\'', '"'));
            return NormalizeUrl(imageUrl);
        }

        return null;
    }

    private static string? ExtractGroup(string text, string pattern)
    {
        var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
        return match.Success ? NormalizeUrl(WebUtility.HtmlDecode(match.Groups["value"].Value.Trim())) : null;
    }

    private string? ExtractFirstCardUrl(string html)
    {
        var patterns = new[]
        {
            @"href=[""'](?<value>[^""']*view=cards%2Fcard[^""']+)[""']",
            @"href=[""'](?<value>[^""']*view=cards/card[^""']+)[""']"
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
            if (!match.Success) continue;

            var href = WebUtility.HtmlDecode(match.Groups["value"].Value);
            if (href.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                return href;
            }

            return $"{_options.BaseUrl.TrimEnd('/')}/{href.TrimStart('/')}";
        }

        return null;
    }

    private static string? NormalizeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.StartsWith("//", StringComparison.Ordinal)) return $"https:{value}";
        return value;
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

