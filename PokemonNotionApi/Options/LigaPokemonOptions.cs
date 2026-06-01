namespace PokemonNotionApi.Options;

public sealed class LigaPokemonOptions
{
    public const string SectionName = "LigaPokemon";
    public string BaseUrl { get; set; } = "https://www.ligapokemon.com.br";
    public string UserAgent { get; set; } = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) PokemonNotionApi/1.0";
}
