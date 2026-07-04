namespace PokemonNotionApi.Options;

public sealed class LigaPokemonOptions
{
    public const string SectionName = "LigaPokemon";
    public string BaseUrl { get; set; } = "https://www.ligapokemon.com.br";
    public string UserAgent { get; set; } = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) PokemonNotionApi/1.0";
    public string AcceptLanguage { get; set; } = "pt-BR,pt;q=0.9,en-US;q=0.8,en;q=0.7";
    public string? Cookie { get; set; }
    public bool UsePuppeteerFallback { get; set; } = true;
    public string PuppeteerScriptPath { get; set; } = "Tools/liga-pokemon-puppeteer.js";
    public int PuppeteerTimeoutMs { get; set; } = 15000;
    public bool PuppeteerHeadless { get; set; } = true;
}
