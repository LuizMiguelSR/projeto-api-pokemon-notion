namespace PokemonNotionApi.Options;

public sealed class NotionOptions
{
    public const string SectionName = "Notion";
    public string Token { get; set; } = string.Empty;
    public string DatabaseId { get; set; } = string.Empty;
    public string LogDatabaseId { get; set; } = string.Empty;
    public string LogNameProperty { get; set; } = "Log";
    public string LogDateProperty { get; set; } = "Data";
    public string LogStatusProperty { get; set; } = "Status";
    public string NotionVersion { get; set; } = "2022-06-28";
    public string CardNameProperty { get; set; } = "Name";
    public string CardUrlProperty { get; set; } = "Link";
    public string PriceProperty { get; set; } = "Preço";
    public string FoilPriceProperty { get; set; } = "Valor Foil";
    public string ReverseFoilPriceProperty { get; set; } = "Valor Reverse Foil";
    public string NormalQuantityProperty { get; set; } = "Qtde Normal";
    public string FoilQuantityProperty { get; set; } = "Qtde. Foil";
    public string ReverseFoilQuantityProperty { get; set; } = "Qtde. Reverse Foil";
    public string ImageProperty { get; set; } = "Imagem";
    public string TypeProperty { get; set; } = "Tipo";
    public string RarityProperty { get; set; } = "Raridade";
    public string NumberProperty { get; set; } = "Número";
    public string StatusProperty { get; set; } = "Status";
    public string DoneStatusValue { get; set; } = "Concluído";
}
