using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using PokemonNotionApi.Models;
using PokemonNotionApi.Options;
using Microsoft.Extensions.Options;

namespace PokemonNotionApi.Services;

public sealed class LigaPokemonScraperService(HttpClient httpClient, IOptions<LigaPokemonOptions> options)
{
    private readonly LigaPokemonOptions _options = options.Value;

    public async Task<CardData?> GetCardAsync(string sourceUrl, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, sourceUrl);
        request.Headers.UserAgent.ParseAdd(_options.UserAgent);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;

        var html = await response.Content.ReadAsStringAsync(cancellationToken);

        var title = ExtractGroup(html, @"<h1[^>]*>\s*(?<value>[^<]+)\s*</h1>");
        var image = ExtractGroup(html, "property=\"og:image\" content=\"(?<value>[^\"]+)\"");
        var priceText = ExtractGroup(html, @"Preço Médio de Venda no Marketplace[\s\S]*?R\$\s*(?<value>[0-9\.,]+)");
        var rarity = ExtractByLabel(html, "Raridade");
        var type = ExtractByLabel(html, "Tipo");

        decimal? priceValue = null;
        if (!string.IsNullOrWhiteSpace(priceText))
        {
            var normalized = priceText.Replace(".", "").Replace(",", ".");
            if (decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
            {
                priceValue = parsed;
            }
        }

        var (name, number) = ParseNameAndNumber(title);

        return new CardData
        {
            Name = name ?? title,
            Number = number,
            PriceText = string.IsNullOrWhiteSpace(priceText) ? null : $"R$ {priceText}",
            PriceValue = priceValue,
            ImageUrl = image,
            Type = type,
            Rarity = rarity,
            SourceUrl = sourceUrl
        };
    }

    private static string? ExtractByLabel(string html, string label)
    {
        var pattern = $"{Regex.Escape(label)}[\\s\\S]*?<div[^>]*>\\s*(?<value>[^<]+)\\s*</div>";
        return CleanText(ExtractGroup(html, pattern));
    }

    private static string? ExtractGroup(string text, string pattern)
    {
        var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
        return match.Success ? WebUtility.HtmlDecode(match.Groups["value"].Value.Trim()) : null;
    }

    private static (string? Name, string? Number) ParseNameAndNumber(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return (null, null);
        var match = Regex.Match(fullName, @"^(?<name>.+?)\s*\((?<num>[^)]+)\)\s*$");
        return match.Success
            ? (CleanText(match.Groups["name"].Value), CleanText(match.Groups["num"].Value))
            : (CleanText(fullName), null);
    }

    private static string? CleanText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return Regex.Replace(value, @"\s+", " ").Trim();
    }
}
