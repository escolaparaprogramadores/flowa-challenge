using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Flowa.Common;

/// <summary>
/// Contrato D-34: o id de correlação entra pelo header <see cref="HeaderName"/>, volta na resposta,
/// vai em toda chamada HTTP de saída e em todo log. Fila e FIX usam <see cref="Current"/> e <see cref="Set"/>.
/// </summary>
public static partial class CorrelationId
{
    public const string HeaderName = "X-Correlation-Id";

    /// <summary>Chave do escopo de log que carrega o id.</summary>
    public const string LogScopeKey = "CorrelationId";

    private static readonly AsyncLocal<string?> current = new();

    /// <summary>Id do fluxo em execução, ou null fora de um fluxo correlacionado.</summary>
    public static string? Current => current.Value;

    /// <summary>Marca o fluxo em execução (e o que ele chamar depois) com o id informado.</summary>
    public static void Set(string id) => current.Value = id;

    /// <summary>Devolve o id recebido quando é válido; senão gera um novo.</summary>
    public static string NormalizeOrCreate(string? received) =>
        received is not null && ValidFormat().IsMatch(received)
            ? received
            : Guid.NewGuid().ToString();

    // Letras, dígitos, ponto, hífen e sublinhado, até 64: bloqueia quebra de linha e lixo em header e log.
    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex ValidFormat();
}

/// <summary>Lê ou gera o id da requisição, devolve no header da resposta e abre o escopo de log.</summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var id = CorrelationId.NormalizeOrCreate(context.Request.Headers[CorrelationId.HeaderName].FirstOrDefault());
        CorrelationId.Set(id);

        // Grava no último instante: o tratador de exceção limpa os headers antes de escrever o erro.
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[CorrelationId.HeaderName] = id;
            return Task.CompletedTask;
        });

        using (logger.BeginScope(new Dictionary<string, object> { [CorrelationId.LogScopeKey] = id }))
        {
            await next(context);
        }
    }
}

/// <summary>Põe o id do fluxo atual no header de toda chamada HTTP de saída, sem trocar um valor já definido.</summary>
public sealed class CorrelationIdHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var id = CorrelationId.Current;
        if (id is not null && !request.Headers.Contains(CorrelationId.HeaderName))
            request.Headers.Add(CorrelationId.HeaderName, id);

        return base.SendAsync(request, cancellationToken);
    }
}
