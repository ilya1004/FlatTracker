using System.Text.Json;
using FlatTracker.Tests.Fakes;
using OllamaSharp.Models;

namespace FlatTracker.Tests;

public class LlmRequestTests
{
    private const string Ad = "Сдаю 1-комнатную на Куйбышева 12, 3 этаж, 1200 BYN, собственник.";

    [Fact]
    public void BuildRequest_RequestsJsonFormatWithoutStopWords()
    {
        var service = TestLlm.Create(FakeOllamaHandler.RespondingWithResponse("{}"));

        var request = service.BuildRequest(Ad);

        Assert.Equal("json", request.Format);
        Assert.Equal(TestLlm.Model, request.Model);

        // Регрессия: Stop = ["}"] обрывал JSON раньше закрывающей скобки,
        // и ответ нельзя было разобрать.
        var stop = request.Options?.Stop;
        Assert.True(stop is null || stop.Length == 0);
    }

    [Fact]
    public void BuildRequest_UsesModelAndContextFromOptions()
    {
        var service = TestLlm.Create(
            FakeOllamaHandler.RespondingWithResponse("{}"),
            TestLlm.CreateOptions(o =>
            {
                o.Model = "llama3.1:8b";
                o.NumCtx = 8192;
                o.NumPredict = 256;
            }));

        var request = service.BuildRequest(Ad);

        Assert.Equal("llama3.1:8b", request.Model);

        var options = Assert.IsType<RequestOptions>(request.Options);
        Assert.Equal(8192, options.NumCtx);
        Assert.Equal(256, options.NumPredict);
    }

    [Fact]
    public void BuildPrompt_ContainsConfiguredCriteriaAndAdText()
    {
        var service = TestLlm.Create(
            FakeOllamaHandler.RespondingWithResponse("{}"),
            TestLlm.CreateOptions(o => o.MaxPriceByn = 1500));

        var prompt = service.BuildPrompt(Ad);

        Assert.Contains("1500 BYN", prompt);
        Assert.Contains("600 USD", prompt);
        Assert.Contains("\"is_relevant\"", prompt);
        Assert.Contains(Ad, prompt);
    }

    [Fact]
    public void BuildPrompt_ExplainsUsdSpellings()
    {
        var service = TestLlm.Create(FakeOllamaHandler.RespondingWithResponse("{}"));

        var prompt = service.BuildPrompt(Ad);

        Assert.Contains("у.е", prompt);
        Assert.Contains("условных единиц", prompt);
        Assert.Contains("$", prompt);
        Assert.Contains("usd", prompt.ToLowerInvariant());
    }

    [Fact]
    public void BuildPrompt_ContainsDistrictAndMetroDictionaries()
    {
        var service = TestLlm.Create(
            FakeOllamaHandler.RespondingWithResponse("{}"),
            TestLlm.CreateOptions(o =>
            {
                o.Districts = ["Фрунзенский", "Московский"];
                o.MetroStations = ["Немига", "Вокзальная"];
            }));

        var prompt = service.BuildPrompt(Ad);

        Assert.Contains("Допустимые районы", prompt);
        Assert.Contains("Фрунзенский, Московский", prompt);
        Assert.Contains("Допустимые станции метро", prompt);
        Assert.Contains("Немига, Вокзальная", prompt);
    }

    [Fact]
    public void BuildPrompt_OmitsDictionariesWhenNotConfigured()
    {
        var service = TestLlm.Create(FakeOllamaHandler.RespondingWithResponse("{}"));

        var prompt = service.BuildPrompt(Ad);

        Assert.DoesNotContain("Допустимые районы", prompt);
        Assert.DoesNotContain("Допустимые станции метро", prompt);
    }

    [Fact]
    public void BuildPrompt_KeepsRoomsFloorAndOwnerAsFixedCriteria()
    {
        // Эти критерии убраны из настроек, но остаются в промте как фиксированные правила.
        var service = TestLlm.Create(FakeOllamaHandler.RespondingWithResponse("{}"));

        var prompt = service.BuildPrompt(Ad);

        Assert.Contains("1 или 2 комнаты", prompt);
        Assert.Contains("Не первый и не последний этаж", prompt);
        Assert.Contains("От собственника", prompt);
    }

    [Fact]
    public async Task ParseAdAsync_PostsPromptToConfiguredEndpoint()
    {
        var handler = FakeOllamaHandler.RespondingWithResponse(
            """{"is_relevant":true,"price":1200,"currency":"BYN","rooms":1,"district":"Советский","metro":"Немига","address":"Куйбышева 12","description":"хорошая","reason":"ок"}""");
        var service = TestLlm.Create(handler);

        await service.ParseAdAsync(Ad, CancellationToken.None);

        var call = Assert.Single(handler.RequestUris);
        Assert.Contains("/api/generate", call);

        using var body = JsonDocument.Parse(handler.RequestBodies[0]);
        var prompt = body.RootElement.GetProperty("prompt").GetString();

        Assert.NotNull(prompt);
        Assert.Contains(Ad, prompt);
        Assert.Equal("json", body.RootElement.GetProperty("format").GetString());
        Assert.False(body.RootElement.GetProperty("think").GetBoolean());
        Assert.False(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.False(body.RootElement.TryGetProperty("stop", out _));
    }

    [Fact]
    public async Task ParseAdAsync_ReturnsParsedAdOnFirstAttempt()
    {
        var handler = FakeOllamaHandler.RespondingWithResponse(
            """{"is_relevant":true,"price":1200,"currency":"BYN","rooms":1,"district":"Советский","metro":"Немига","address":"Куйбышева 12","description":"свежий ремонт","reason":"подходит"}""");
        var service = TestLlm.Create(handler);

        var result = await service.ParseAdAsync(Ad, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.IsRelevant);
        Assert.Equal(1200, result.Price);
        Assert.Equal("BYN", result.Currency);
        Assert.Equal(1200, result.PriceByn);
        Assert.Equal(1, result.Rooms);
        Assert.Equal("Советский", result.District);
        Assert.Equal("Немига", result.Metro);
        Assert.Equal("Куйбышева 12", result.Address);
        Assert.Equal("свежий ремонт", result.Description);
        Assert.Single(handler.RequestBodies);
    }

    [Fact]
    public async Task ParseAdAsync_RejectsIrrelevantAd()
    {
        var handler = FakeOllamaHandler.RespondingWithResponse(
            """{"is_relevant":false,"price":2500,"currency":"BYN","rooms":3,"reason":"дорого и 3 комнаты"}""");
        var service = TestLlm.Create(handler);

        var result = await service.ParseAdAsync(Ad, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.IsRelevant);
        Assert.Equal(2500, result.Price);
    }

    [Theory]
    [InlineData("```json\n{\"is_relevant\":true,\"price\":1000,\"currency\":\"BYN\",\"rooms\":2,\"reason\":\"\"}\n```")]
    [InlineData("Вот результат: {\"is_relevant\":true,\"price\":1000,\"currency\":\"BYN\",\"rooms\":2,\"reason\":\"\"} — готово")]
    [InlineData("{\"is_relevant\":true,\"price\":1000,\"currency\":\"BYN\",\"rooms\":2,\"reason\":\"\"}")]
    public async Task ParseAdAsync_HandlesMessyResponses(string raw)
    {
        var service = TestLlm.Create(FakeOllamaHandler.RespondingWithResponse(raw));

        var result = await service.ParseAdAsync(Ad, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.IsRelevant);
    }

    [Fact]
    public async Task ParseAdAsync_ClosesUnterminatedJson()
    {
        // Обрезано на последней закрывающей скобке — самый частый случай с NumPredict.
        var truncated =
            "{\"is_relevant\":true,\"price\":900,\"currency\":\"BYN\",\"rooms\":1,\"reason\":\"подходит\"";
        var service = TestLlm.Create(FakeOllamaHandler.RespondingWithResponse(truncated));

        var result = await service.ParseAdAsync(Ad, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(900, result.Price);
    }

    [Fact]
    public async Task ParseAdAsync_RetriesGarbageAndReturnsNull()
    {
        var handler = FakeOllamaHandler.RespondingWithGenerate("не json вовсе", "всё ещё не json", "опять не json");
        var service = TestLlm.Create(handler, TestLlm.CreateOptions(o => o.MaxRetries = 2));

        var result = await service.ParseAdAsync(Ad, CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(3, handler.RequestBodies.Count);
    }

    [Fact]
    public async Task ParseAdAsync_DoesNotRetryWhenRetriesDisabled()
    {
        var handler = FakeOllamaHandler.RespondingWithGenerate("мусор");
        var service = TestLlm.Create(handler, TestLlm.CreateOptions(o => o.MaxRetries = 0));

        var result = await service.ParseAdAsync(Ad, CancellationToken.None);

        Assert.Null(result);
        Assert.Single(handler.RequestBodies);
    }

    [Fact]
    public async Task ParseAdAsync_TriesAgainAfterBadJson()
    {
        var handler = FakeOllamaHandler.RespondingWithGenerate(
            "сломанный ответ",
            """{"is_relevant":true,"price":1300,"currency":"BYN","rooms":1,"reason":"ок"}""");
        var service = TestLlm.Create(handler);

        var result = await service.ParseAdAsync(Ad, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(1300, result.Price);
        Assert.Equal(2, handler.RequestBodies.Count);
    }

    [Fact]
    public async Task EnsureAvailableAsync_ReturnsTrueWhenModelPresent()
    {
        var service = TestLlm.Create(FakeOllamaHandler.Healthy(TestLlm.Model));

        Assert.True(await service.EnsureAvailableAsync(CancellationToken.None));
    }

    [Fact]
    public async Task EnsureAvailableAsync_ReturnsFalseWhenModelMissing()
    {
        var service = TestLlm.Create(FakeOllamaHandler.Healthy("llama3.1:8b"));

        Assert.False(await service.EnsureAvailableAsync(CancellationToken.None));
    }

    [Fact]
    public async Task EnsureAvailableAsync_ReturnsFalseWhenOllamaDown()
    {
        var service = TestLlm.Create(FakeOllamaHandler.Unreachable());

        Assert.False(await service.EnsureAvailableAsync(CancellationToken.None));
    }
}
