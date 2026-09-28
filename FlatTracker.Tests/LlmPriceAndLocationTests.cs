using FlatTracker.Core.Configuration;
using FlatTracker.Tests.Fakes;
using FlatTracker.UI.Services;

namespace FlatTracker.Tests;

public class LlmPriceAndLocationTests
{
    private static LlmOptions Options(Action<LlmOptions>? configure = null)
    {
        var options = TestLlm.CreateOptions(o =>
        {
            o.UsdToBynRate = 3.1;
            o.Districts = ["Фрунзенский", "Московский", "Советский"];
            o.MetroStations = ["Немига", "Вокзальная", "Пушкинская"];
        });

        configure?.Invoke(options);
        return options;
    }

    [Theory]
    [InlineData("у.е.")]
    [InlineData("у.е")]
    [InlineData("у.е. в месяц")]
    [InlineData("условных единиц")]
    [InlineData("условные единицы")]
    [InlineData("$")]
    [InlineData("USD")]
    [InlineData("usd")]
    [InlineData("долларов")]
    public void DetectCurrencyFromText_TreatsUsdSpellings(string marker)
    {
        var detected = LlmResponseParser.DetectCurrencyFromText($"500 {marker}");

        Assert.Equal("USD", detected);
    }

    [Theory]
    [InlineData("BYN")]
    [InlineData("брн")]
    [InlineData("бел. руб.")]
    [InlineData("белорусских рублей")]
    [InlineData("руб.")]
    [InlineData("р.")]
    public void DetectCurrencyFromText_TreatsBynSpellings(string marker)
    {
        var detected = LlmResponseParser.DetectCurrencyFromText($"1200 {marker}");

        Assert.Equal("BYN", detected);
    }

    [Fact]
    public void DetectCurrencyFromText_ReturnsEmptyWithoutMarkers()
    {
        Assert.Equal(string.Empty, LlmResponseParser.DetectCurrencyFromText("1200 в месяц"));
        Assert.Equal(string.Empty, LlmResponseParser.DetectCurrencyFromText(null));
    }

    [Fact]
    public void TryParse_KeepsUsdPriceAndConvertsToByn()
    {
        var json = """
                  {"is_relevant":true,"price":500,"currency":"USD","price_text":"500 у.е.",
                   "rooms":1,"district":"Фрунзенский","metro":"Пушкинская","reason":"ок"}
                  """;

        var result = LlmResponseParser.TryParse(json, Options());

        Assert.NotNull(result);
        Assert.Equal(500, result.Price);
        Assert.Equal("USD", result.Currency);
        Assert.Equal(1550, result.PriceByn);
        Assert.Equal("500 USD (1 550 BYN)", result.PriceText);
    }

    [Fact]
    public void TryParse_KeepsBynPriceAsIs()
    {
        var json = """{"is_relevant":true,"price":1200,"currency":"BYN","rooms":1}""";

        var result = LlmResponseParser.TryParse(json, Options());

        Assert.NotNull(result);
        Assert.Equal("BYN", result.Currency);
        Assert.Equal(1200, result.PriceByn);
        Assert.Equal("1 200 BYN", result.PriceText);
    }

    [Fact]
    public void TryParse_FallsBackToPriceTextWhenCurrencyMissing()
    {
        var json = """{"is_relevant":true,"price":450,"currency":"","price_text":"450 условных единиц","rooms":1}""";

        var result = LlmResponseParser.TryParse(json, Options());

        Assert.NotNull(result);
        Assert.Equal("USD", result.Currency);
        Assert.Equal(1395, result.PriceByn);
    }

    [Fact]
    public void TryParse_MapsDistrictAndMetroToDictionary()
    {
        var json = """
                  {"is_relevant":true,"price":800,"currency":"USD","rooms":1,
                   "district":"фрунзенский","metro":"метро Пушкинская","reason":"ок"}
                  """;

        var result = LlmResponseParser.TryParse(json, Options());

        Assert.NotNull(result);
        Assert.Equal("Фрунзенский", result.District);
        Assert.Equal("Пушкинская", result.Metro);
    }

    [Fact]
    public void TryParse_KeepsUnknownLocationWhenDictionaryEmpty()
    {
        var json = """{"is_relevant":true,"price":800,"currency":"USD","rooms":1,"district":"Ждановичи","metro":"Вокзал"}""";
        var options = Options();
        options.Districts = [];
        options.MetroStations = [];

        var result = LlmResponseParser.TryParse(json, options);

        Assert.NotNull(result);
        Assert.Equal("Ждановичи", result.District);
        Assert.Equal("Вокзал", result.Metro);
    }

    [Fact]
    public void PriceText_HandlesMissingPrice()
    {
        var ad = new ParsedAd(true, 0, string.Empty, 0, 0, string.Empty, string.Empty, string.Empty, string.Empty, "нет цены");

        Assert.Equal("не указана", ad.PriceText);
    }

    [Fact]
    public void ConvertToByn_ZeroPriceStaysZero()
    {
        Assert.Equal(0, LlmResponseParser.ConvertToByn(0, "USD", 3.1));
    }
}
