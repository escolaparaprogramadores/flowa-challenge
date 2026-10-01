using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Flowa.Comum.Testes;

public sealed record Item(string Codigo, int Quantidade);

/// <summary>Serviço mínimo em memória, registrado como um serviço do flowa registraria a lib.</summary>
public sealed class HostDeTeste : IAsyncDisposable
{
    public const string MensagemSecreta = "detalhe-interno-do-servidor tabela reservas_internas";

    private readonly WebApplication app;

    public HttpClient Cliente { get; }
    public LogDeTeste Logs { get; }
    public RegistroDeSaida Saida { get; }

    private HostDeTeste(WebApplication app, LogDeTeste logs, RegistroDeSaida saida)
    {
        this.app = app;
        Logs = logs;
        Saida = saida;
        Cliente = app.GetTestClient();
    }

    public static async Task<HostDeTeste> IniciarAsync()
    {
        var logs = new LogDeTeste();
        var saida = new RegistroDeSaida();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Services.AdicionarFlowaComum();
        builder.Services.AddSingleton(saida);
        builder.Services.AddHttpClient("externo", cliente => cliente.BaseAddress = new Uri("http://externo.teste"))
            .ConfigurePrimaryHttpMessageHandler(servicos => new HandlerFalsoDeSaida(servicos.GetRequiredService<RegistroDeSaida>()));

        var app = builder.Build();
        app.UsarFlowaComum();
        MapearRotas(app.MapGroup("/api").ComEnvelope());

        await app.StartAsync();
        return new HostDeTeste(app, logs, saida);
    }

    private static void MapearRotas(RouteGroupBuilder api)
    {
        api.MapGet("/objeto", () => new { nome = "flowa", quantidade = 3 });
        api.MapGet("/ok", () => TypedResults.Ok(new Item("PETR4", 10)));
        api.MapPost("/criado", () => TypedResults.Created("/api/itens/7", new Item("VALE3", 5)));
        api.MapDelete("/sem-conteudo", () => TypedResults.NoContent());
        api.MapGet("/void", () => { });
        api.MapGet("/envelope-pronto", () => Envelope.Sucesso(new Item("VIIA4", 1)));
        api.MapGet("/texto", () => TypedResults.Text("ola"));
        api.MapGet("/teste/health", () => new { status = "ok" });

        api.MapPost("/validacao", () => TypedResults.ValidationProblem(new Dictionary<string, string[]>
        {
            ["quantidade"] = ["Deve ser maior que zero.", "Deve ser menor que 100000."],
            ["codigo"] = ["Obrigatório."],
        }));
        api.MapPost("/corpo", (Item item) => item);
        api.MapGet("/nao-encontrado", () => TypedResults.NotFound());
        api.MapGet("/conflito", () => TypedResults.Conflict("Estoque insuficiente."));
        api.MapGet("/problema", () => TypedResults.Problem(detail: "Oferta encerrada.", statusCode: 422));
        api.MapGet("/problema-500", () => TypedResults.Problem(detail: MensagemSecreta, statusCode: 500));
        api.MapGet("/erro", Results<Ok, NotFound> () => throw new InvalidOperationException(MensagemSecreta));

        api.MapGet("/loga", (ILogger<HostDeTeste> logger) =>
        {
            logger.LogInformation("processando pedido");
            return new { logado = true };
        });
        api.MapGet("/chama-externo", async (IHttpClientFactory fabrica) =>
        {
            var resposta = await fabrica.CreateClient("externo").GetAsync("/cotacao");
            return new { status = (int)resposta.StatusCode };
        });
        api.MapGet("/chama-externo-com-id-proprio", async (IHttpClientFactory fabrica) =>
        {
            using var requisicao = new HttpRequestMessage(HttpMethod.Get, "/cotacao");
            requisicao.Headers.Add(CorrelationId.NomeDoHeader, "definido-pelo-chamador");
            var resposta = await fabrica.CreateClient("externo").SendAsync(requisicao);
            return new { status = (int)resposta.StatusCode };
        });
    }

    public async Task<(HttpResponseMessage Resposta, JsonElement Corpo)> EnviarAsync(
        HttpMethod metodo, string caminho, string? correlationId = null, HttpContent? conteudo = null)
    {
        using var requisicao = new HttpRequestMessage(metodo, caminho) { Content = conteudo };
        if (correlationId is not null)
            requisicao.Headers.TryAddWithoutValidation(CorrelationId.NomeDoHeader, correlationId);

        var resposta = await Cliente.SendAsync(requisicao);
        var texto = await resposta.Content.ReadAsStringAsync();
        var corpo = texto.Length == 0 ? default : JsonDocument.Parse(texto).RootElement.Clone();
        return (resposta, corpo);
    }

    public async ValueTask DisposeAsync()
    {
        Cliente.Dispose();
        await app.DisposeAsync();
    }
}

/// <summary>Grava o header de correlação de cada chamada HTTP de saída.</summary>
public sealed class RegistroDeSaida
{
    public List<string?> HeadersRecebidos { get; } = [];
}

public sealed class HandlerFalsoDeSaida(RegistroDeSaida registro) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage requisicao, CancellationToken cancelamento)
    {
        requisicao.Headers.TryGetValues(CorrelationId.NomeDoHeader, out var valores);
        lock (registro.HeadersRecebidos)
            registro.HeadersRecebidos.Add(valores?.Single());
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}
