namespace FlatTracker.Core.Configuration;

public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    public string Endpoint { get; set; } = "http://localhost:11434";

    public string Model { get; set; } = "qwen3.5:9b";

    public int MaxPriceByn { get; set; } = 2000;

    public int MaxPriceUsd { get; set; } = 600;

    /// <summary>Курс для приведения USD к BYN, чтобы проверять лимит в одной валюте.</summary>
    public double UsdToBynRate { get; set; } = 3.1;

    public int MaxRetries { get; set; } = 2;

    public int TimeoutSeconds { get; set; } = 120;

    public int NumCtx { get; set; } = 4096;

    public int NumPredict { get; set; } = 500;

    /// <summary>Список районов Минска. Пустой список отключает фильтр по районам.</summary>
    public string[] Districts { get; set; } = [];

    /// <summary>Список станций метро. Пустой список отключает фильтр по метро.</summary>
    public string[] MetroStations { get; set; } = [];
}
