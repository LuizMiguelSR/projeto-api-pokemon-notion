using System.Globalization;
using System.Net;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using PokemonNotionApi.Models;
using PokemonNotionApi.Options;
using Microsoft.Extensions.Options;

namespace PokemonNotionApi.Services;

public sealed class LigaPokemonScraperService(
    HttpClient httpClient,
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
        var fetchedWithPuppeteer = false;

        async Task<string?> FetchWithPuppeteerOnceAsync()
        {
            if (fetchedWithPuppeteer)
            {
                return null;
            }

            fetchedWithPuppeteer = true;
            return await TryFetchHtmlWithPuppeteerAsync(sourceUrl, cancellationToken);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, sourceUrl);
        request.Headers.UserAgent.ParseAdd(_options.UserAgent);
        request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        request.Headers.AcceptLanguage.ParseAdd(_options.AcceptLanguage);
        request.Headers.Referrer = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        AddCookieHeader(request);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var html = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "Liga Pokemon returned HTTP {StatusCode} for {SourceUrl}. Reason={ReasonPhrase}. Preview={Preview}",
                (int)response.StatusCode,
                sourceUrl,
                response.ReasonPhrase,
                BuildPreview(html));

            var puppeteerHtml = await FetchWithPuppeteerOnceAsync();
            if (string.IsNullOrWhiteSpace(puppeteerHtml) || IsCloudflareChallenge(puppeteerHtml))
            {
                var preview = BuildPreview(string.IsNullOrWhiteSpace(puppeteerHtml) ? html : puppeteerHtml);
                var isBlocked = IsCloudflareChallenge(string.IsNullOrWhiteSpace(puppeteerHtml) ? html : puppeteerHtml);
                throw new LigaPokemonScraperException(
                    isBlocked
                        ? "Liga Pokemon blocked or rejected the request."
                        : "Liga Pokemon returned a non-success response.",
                    (int)response.StatusCode,
                    isBlocked
                        ? "Cloudflare or anti-bot page returned instead of card page"
                        : response.ReasonPhrase ?? "HTTP request failed",
                    sourceUrl,
                    preview);
            }

            html = puppeteerHtml;
        }

        if (IsCloudflareChallenge(html))
        {
            logger.LogWarning(
                "Liga Pokemon Cloudflare challenge detected for {SourceUrl}. StatusCode={StatusCode}. Preview={Preview}",
                sourceUrl,
                (int)response.StatusCode,
                BuildPreview(html));

            html = await FetchWithPuppeteerOnceAsync() ?? html;
            if (IsCloudflareChallenge(html))
            {
                throw new LigaPokemonScraperException(
                    "Liga Pokemon blocked or rejected the request.",
                    (int)response.StatusCode,
                    "Cloudflare or anti-bot page returned instead of card page",
                    sourceUrl,
                    BuildPreview(html));
            }
        }

        var title = ExtractGroup(html, @"<h1[^>]*>\s*(?<value>[^<]+)\s*</h1>")
            ?? ExtractGroup(html, "property=\"og:title\" content=\"(?<value>[^\"]+)\"");
        var image = ExtractCardImage(html) ?? ExtractGroup(html, "property=\"og:image\" content=\"(?<value>[^\"]+)\"");
        var prices = ExtractPrices(html);
        var rarity = ExtractByLabel(html, "Raridade");
        var type = ExtractByLabel(html, "Tipo");
        if (!HasAnyPrice(prices))
        {
            var puppeteerHtml = await FetchWithPuppeteerOnceAsync();
            if (!string.IsNullOrWhiteSpace(puppeteerHtml) && !IsCloudflareChallenge(puppeteerHtml))
            {
                html = puppeteerHtml;
                title = ExtractGroup(html, @"<h1[^>]*>\s*(?<value>[^<]+)\s*</h1>")
                    ?? ExtractGroup(html, "property=\"og:title\" content=\"(?<value>[^\"]+)\"");
                image = ExtractCardImage(html) ?? ExtractGroup(html, "property=\"og:image\" content=\"(?<value>[^\"]+)\"");
                prices = ExtractPrices(html);
                rarity = ExtractByLabel(html, "Raridade");
                type = ExtractByLabel(html, "Tipo");
            }
        }

        if (!HasAnyPrice(prices))
        {
            logger.LogWarning(
                "Liga Pokemon page without price data for {SourceUrl}. Title={Title}. Preview={Preview}",
                sourceUrl,
                title,
                BuildPreview(html));

            throw new LigaPokemonScraperException(
                "Liga Pokemon returned a page, but no price data could be extracted.",
                (int)response.StatusCode,
                "Card page HTML did not contain price data",
                sourceUrl,
                BuildPreview(html));
        }

        var (name, number) = ParseNameAndNumber(title);
        number ??= ExtractEditionNumber(html);
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

    private static (decimal? Normal, decimal? Foil, decimal? ReverseFoil) ExtractPrices(string html)
    {
        var runtimePrices = ExtractPricesFromRuntimeData(html);
        if (HasAnyPrice(runtimePrices))
        {
            return runtimePrices;
        }

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

    private static (decimal? Normal, decimal? Foil, decimal? ReverseFoil) ExtractPricesFromRuntimeData(string html)
    {
        var match = Regex.Match(
            html,
            @"<script[^>]*id=[""']liga-pokemon-runtime-data[""'][^>]*>(?<value>[\s\S]*?)</script>",
            RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return (null, null, null);
        }

        try
        {
            var json = WebUtility.HtmlDecode(match.Groups["value"].Value);
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

    private static bool IsInteractiveCloudflareChallenge(string html)
    {
        return html.Contains("cf-turnstile-response", StringComparison.OrdinalIgnoreCase) ||
            html.Contains("turnstile", StringComparison.OrdinalIgnoreCase) ||
            html.Contains("Executando verificação de segurança", StringComparison.OrdinalIgnoreCase) ||
            html.Contains("verifica se você não é um bot", StringComparison.OrdinalIgnoreCase);
    }

    private void AddCookieHeader(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(_options.Cookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", _options.Cookie);
        }
    }

    private async Task<string?> TryFetchHtmlWithPuppeteerAsync(string sourceUrl, CancellationToken cancellationToken)
    {
        if (!_options.UsePuppeteerFallback)
        {
            return null;
        }

        var scriptPath = ResolvePuppeteerScriptPath();
        if (!File.Exists(scriptPath))
        {
            throw new LigaPokemonScraperException(
                "Liga Pokemon Puppeteer fallback is enabled, but the script was not found.",
                0,
                $"Missing Puppeteer script: {scriptPath}",
                sourceUrl,
                string.Empty);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(_options.PuppeteerTimeoutMs + 5000, 10000)));

        var startInfo = new ProcessStartInfo
        {
            FileName = "node",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add(sourceUrl);
        startInfo.ArgumentList.Add(_options.UserAgent);
        startInfo.ArgumentList.Add(_options.AcceptLanguage);
        startInfo.ArgumentList.Add(_options.PuppeteerTimeoutMs.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(_options.PuppeteerHeadless ? "true" : "false");
        startInfo.ArgumentList.Add(_options.Cookie ?? string.Empty);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start Node.js process for Puppeteer fallback.");

        string html;
        string stderr;
        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);

            html = await outputTask;
            stderr = await errorTask;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKillProcessTree(process);
            throw new LigaPokemonScraperException(
                "Liga Pokemon Puppeteer fallback timed out.",
                0,
                $"Puppeteer timed out after {_options.PuppeteerTimeoutMs}ms",
                sourceUrl,
                string.Empty);
        }

        if (process.ExitCode != 0)
        {
            throw new LigaPokemonScraperException(
                "Liga Pokemon Puppeteer fallback failed.",
                0,
                CleanText(stderr) ?? $"Node process exited with code {process.ExitCode}",
                sourceUrl,
                BuildPreview(html));
        }

        return string.IsNullOrWhiteSpace(html) ? null : html;
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort cleanup after a Puppeteer timeout.
        }
    }

    private string ResolvePuppeteerScriptPath()
    {
        if (Path.IsPathRooted(_options.PuppeteerScriptPath))
        {
            return _options.PuppeteerScriptPath;
        }

        var workingDirectoryPath = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, _options.PuppeteerScriptPath));
        if (File.Exists(workingDirectoryPath))
        {
            return workingDirectoryPath;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, _options.PuppeteerScriptPath));
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

