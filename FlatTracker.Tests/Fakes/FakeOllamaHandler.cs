using System.Net;
using System.Text;
using System.Text.Json;
using FlatTracker.Core.Configuration;
using FlatTracker.UI.Services;
using Microsoft.Extensions.Logging.Abstractions;
using OllamaSharp;

namespace FlatTracker.Tests.Fakes;

/// <summary>
/// Подменяет HTTP-слой Ollama: записывает запросы и отдаёт заготовленные ответы.
/// </summary>
internal sealed class FakeOllamaHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, string, HttpResponseMessage> _responder;

    public List<string> RequestBodies { get; } = [];
    public List<string> RequestUris { get; } = [];

    public FakeOllamaHandler(Func<HttpRequestMessage, string, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    public static FakeOllamaHandler RespondingWithResponse(string response) =>
        new((_, _) => Json(HttpStatusCode.OK,
            $$"""{"model":"qwen3.5:9b","response":{{JsonSerializer.Serialize(response)}},"done":true,"done_reason":"stop"}"""));

    public static FakeOllamaHandler RespondingWithGenerate(params string[] responses)
    {
        var index = 0;
        return new FakeOllamaHandler((_, _) =>
        {
            var current = Math.Min(index++, responses.Length - 1);
            return Json(HttpStatusCode.OK,
                $$"""{"model":"qwen3.5:9b","response":{{JsonSerializer.Serialize(responses[current])}},"done":true,"done_reason":"stop"}""");
        });
    }

    public static FakeOllamaHandler Healthy(string model) =>
        new((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/api/version" => Json(HttpStatusCode.OK, """{"version":"0.13.0"}"""),
            "/api/tags" => Json(HttpStatusCode.OK,
                $$"""{"models":[{"name":{{JsonSerializer.Serialize(model)}},"size":1}]}"""),
            _ => Json(HttpStatusCode.NotFound, "{}")
        });

    public static FakeOllamaHandler Unreachable() =>
        new((_, _) => throw new HttpRequestException("connection refused"));

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);

        RequestBodies.Add(body);
        RequestUris.Add(request.RequestUri!.ToString());

        return _responder(request, body);
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
}

internal static class TestLlm
{
    public const string Model = "qwen3.5:9b";

    public static LlmOptions CreateOptions(Action<LlmOptions>? configure = null)
    {
        var options = new LlmOptions
        {
            Endpoint = "http://localhost:11434",
            Model = Model,
            MaxRetries = 2,
            TimeoutSeconds = 30
        };

        configure?.Invoke(options);
        return options;
    }

    public static LlmParsingService Create(FakeOllamaHandler handler, LlmOptions? options = null)
    {
        var opt = options ?? CreateOptions();
        var http = new HttpClient(handler) { BaseAddress = new Uri(opt.Endpoint) };
        var client = new OllamaApiClient(http, opt.Model);

        return new LlmParsingService(
            Microsoft.Extensions.Options.Options.Create(opt),
            NullLogger<LlmParsingService>.Instance,
            client);
    }
}
