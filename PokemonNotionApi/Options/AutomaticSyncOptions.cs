namespace PokemonNotionApi.Options;

public sealed class AutomaticSyncOptions
{
    public const string SectionName = "AutomaticSync";
    public bool Enabled { get; set; } = true;
    public double IntervalHours { get; set; } = 24;
}
