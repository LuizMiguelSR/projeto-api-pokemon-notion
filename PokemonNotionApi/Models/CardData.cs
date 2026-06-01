namespace PokemonNotionApi.Models;

public sealed class CardData
{
    public string? Name { get; init; }
    public string? Number { get; init; }
    public string? PriceText { get; init; }
    public decimal? PriceValue { get; init; }
    public string? ImageUrl { get; init; }
    public string? Type { get; init; }
    public string? Rarity { get; init; }
    public string SourceUrl { get; init; } = string.Empty;
}
