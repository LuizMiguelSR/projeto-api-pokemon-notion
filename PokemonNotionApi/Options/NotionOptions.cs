namespace PokemonNotionApi.Options;

public sealed class NotionOptions
{
    public const string SectionName = "Notion";
    public string Token { get; set; } = string.Empty;
    public string DatabaseId { get; set; } = string.Empty;
    public string NotionVersion { get; set; } = "2022-06-28";
    public string CardNameProperty { get; set; } = "Name";
    public string CardUrlProperty { get; set; } = "Link";
    public string PriceProperty { get; set; } = "Preço";
    public string ImageProperty { get; set; } = "Imagem";
    public string TypeProperty { get; set; } = "Tipo";
    public string RarityProperty { get; set; } = "Raridade";
    public string NumberProperty { get; set; } = "Número";
    public string StatusProperty { get; set; } = "Status";
    public string DoneStatusValue { get; set; } = "Concluído";
}
