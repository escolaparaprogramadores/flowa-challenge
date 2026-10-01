using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Flowa.Common.Tests;

public sealed record OfferQuantity(string Code, int Quantity);

/// <summary>DTO com um campo chamado Location que é dado, não header.</summary>
public sealed record BranchOffice(string Name, string Location);

/// <summary>Pedido com regras de DataAnnotations, validado pela validação nativa do .NET 10 (AddValidation).</summary>
public sealed class ReservationRequest
{
    [Required(ErrorMessage = "Informe a oferta.")]
    public string? OfferCode { get; set; }

    [Range(1, 100000, ErrorMessage = "A quantidade deve estar entre 1 e 100000.")]
    public int Quantity { get; set; }
}

/// <summary>Serviço mínimo em memória, registrado como um serviço do flowa registraria a lib.</summary>
public sealed class FlowaTestServer : IAsyncDisposable
{
    public const string InternalDetail = "detalhe-interno-do-servidor tabela reservas_internas";

    private readonly WebApplication app;

    public HttpClient Client { get; }
    public InMemoryLoggerProvider Logs { get; }
    public OutgoingRequestRecorder OutgoingRequests { get; }

    private FlowaTestServer(WebApplication app, InMemoryLoggerProvider logs, OutgoingRequestRecorder outgoing)
    {
        this.app = app;
        Logs = logs;
        OutgoingRequests = outgoing;
        Client = app.GetTestClient();
    }

    /// <param name="problemDetailsRegisteredFirst">Simula um serviço que chama AddProblemDetails antes de AddFlowaCommon.</param>
    public static async Task<FlowaTestServer> StartAsync(bool problemDetailsRegisteredFirst = false)
    {
        var logs = new InMemoryLoggerProvider();
        var outgoing = new OutgoingRequestRecorder();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        if (problemDetailsRegisteredFirst)
            builder.Services.AddProblemDetails();
        builder.Services.AddFlowaCommon();
        builder.Services.AddValidation();
        builder.Services.AddSingleton(outgoing);
        builder.Services.AddHttpClient("external", client => client.BaseAddress = new Uri("http://external.test"))
            .ConfigurePrimaryHttpMessageHandler(services => new FakeOutgoingHandler(services.GetRequiredService<OutgoingRequestRecorder>()));

        var app = builder.Build();
        app.UseFlowaCommon();
        MapRoutes(app.MapGroup("/api").WithEnvelope());
        // Fora do grupo: ProblemDetails escrito pelo próprio resultado passa pelo EnvelopeProblemDetailsWriter.
        app.MapGet("/outside/problem-422", () => TypedResults.Problem(detail: "Oferta encerrada.", statusCode: 422));
        app.MapGet("/outside/problem-500", () => TypedResults.Problem(detail: InternalDetail, statusCode: 500));

        await app.StartAsync();
        return new FlowaTestServer(app, logs, outgoing);
    }

    private static void MapRoutes(RouteGroupBuilder api)
    {
        api.MapGet("/object", () => new { name = "flowa", quantity = 3 });
        api.MapGet("/ok", () => TypedResults.Ok(new OfferQuantity("PETR4", 10)));
        api.MapPost("/created", () => TypedResults.Created("/api/items/7", new OfferQuantity("VALE3", 5)));
        api.MapPost("/accepted", () => TypedResults.Accepted("/api/items/8", new OfferQuantity("BLOG11", 2)));
        api.MapGet("/custom-status", (HttpContext context) =>
        {
            context.Response.StatusCode = StatusCodes.Status202Accepted;
            return new OfferQuantity("BSCI11", 4);
        });
        api.MapDelete("/no-content", () => TypedResults.NoContent());
        api.MapGet("/void", () => { });
        api.MapGet("/ready-envelope", () => Envelope.Success(new OfferQuantity("VIIA4", 1)));
        api.MapGet("/text", () => TypedResults.Text("ola"));
        api.MapGet("/text-conflict", () => TypedResults.Text("Oferta sem estoque.", statusCode: 409));
        api.MapGet("/text-500", () => TypedResults.Text(InternalDetail, statusCode: 500));
        api.MapGet("/not-modified", () => TypedResults.StatusCode(StatusCodes.Status304NotModified));
        api.MapGet("/test/health", () => new { status = "ok" });

        api.MapPost("/validation", () => TypedResults.ValidationProblem(new Dictionary<string, string[]>
        {
            ["quantity"] = ["Deve ser maior que zero.", "Deve ser menor que 100000."],
            ["code"] = ["Obrigatório."],
        }));
        api.MapPost("/validation-nested", () => TypedResults.ValidationProblem(new Dictionary<string, string[]>
        {
            ["Order.OfferCode"] = ["Informe a oferta."],
        }));
        api.MapPost("/body", (OfferQuantity offerQuantity) => offerQuantity);
        api.MapGet("/quote", (int quantity) => new { quantity });
        api.MapPost("/conflict-status", (HttpContext context) => { context.Response.StatusCode = StatusCodes.Status409Conflict; });
        api.MapPost("/validated", (ReservationRequest reservation) => reservation);
        api.MapGet("/not-found", () => TypedResults.NotFound());
        api.MapGet("/conflict", () => TypedResults.Conflict("Estoque insuficiente."));
        api.MapGet("/problem", () => TypedResults.Problem(detail: "Oferta encerrada.", statusCode: 422));
        api.MapGet("/problem-404", () => TypedResults.Problem(statusCode: 404));
        api.MapGet("/dto-with-location", () => new BranchOffice("Centro", "São Paulo"));
        api.MapGet("/problem-title", () => TypedResults.Problem(title: "Prospecto não aceito.", statusCode: 400));
        api.MapGet("/problem-500", () => TypedResults.Problem(detail: InternalDetail, statusCode: 500));
        api.MapGet("/internal-error-text", () => TypedResults.InternalServerError(InternalDetail));
        api.MapGet("/throw", Results<Ok, NotFound> () => throw new InvalidOperationException(InternalDetail));
        api.MapGet("/throw-413", Results<Ok, NotFound> () => throw new BadHttpRequestException("corpo grande demais", 413));

        api.MapGet("/log", (ILogger<FlowaTestServer> logger) =>
        {
            logger.LogInformation("processing order");
            return new { logged = true };
        });
        api.MapGet("/call-external", async (IHttpClientFactory factory) =>
        {
            var response = await factory.CreateClient("external").GetAsync("/quote");
            return new { status = (int)response.StatusCode };
        });
        api.MapGet("/call-external-with-own-id", async (IHttpClientFactory factory) =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/quote");
            request.Headers.Add(CorrelationId.HeaderName, "set-by-caller");
            var response = await factory.CreateClient("external").SendAsync(request);
            return new { status = (int)response.StatusCode };
        });
    }

    public async Task<(HttpResponseMessage Response, JsonElement Body)> SendAsync(
        HttpMethod method, string path, string? correlationId = null, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        if (correlationId is not null)
            request.Headers.TryAddWithoutValidation(CorrelationId.HeaderName, correlationId);

        var response = await Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        var body = text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
        return (response, body);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await app.DisposeAsync();
    }
}

/// <summary>Grava o header de correlação de cada chamada HTTP de saída.</summary>
public sealed class OutgoingRequestRecorder
{
    public List<string?> ReceivedHeaders { get; } = [];
}

public sealed class FakeOutgoingHandler(OutgoingRequestRecorder recorder) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.TryGetValues(CorrelationId.HeaderName, out var values);
        lock (recorder.ReceivedHeaders)
            recorder.ReceivedHeaders.Add(values?.Single());
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}
