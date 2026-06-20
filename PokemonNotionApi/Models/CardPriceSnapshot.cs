namespace PokemonNotionApi.Models;

public sealed class CardPriceSnapshot
{
    public string PageId { get; init; } = string.Empty;
    public string? CardName { get; init; }
    public string? CardNumber { get; init; }
    public string SourceUrl { get; init; } = string.Empty;
    public string? ImageUrl { get; init; }
    public decimal? NormalPrice { get; init; }
    public decimal? FoilPrice { get; init; }
    public decimal? ReverseFoilPrice { get; init; }
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
}
