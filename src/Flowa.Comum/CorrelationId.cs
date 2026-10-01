using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Flowa.Comum;

/// <summary>
/// Contrato D-34: o id de correlação entra pelo header <see cref="NomeDoHeader"/>, volta na resposta,
/// vai em toda chamada HTTP de saída e em todo log. Fila e FIX usam <see cref="Atual"/> e <see cref="Definir"/>.
/// </summary>
public static partial class CorrelationId
{
    public const string NomeDoHeader = "X-Correlation-Id";

    /// <summary>Chave do escopo de log que carrega o id.</summary>
    public const string ChaveDoLog = "CorrelationId";

    private static readonly AsyncLocal<string?> idAtual = new();

    /// <summary>Id do fluxo em execução, ou null fora de um fluxo correlacionado.</summary>
    public static string? Atual => idAtual.Value;

    /// <summary>Marca o fluxo em execução (e o que ele chamar depois) com o id informado.</summary>
    public static void Definir(string id) => idAtual.Value = id;

    /// <summary>Devolve o id recebido quando é válido; senão gera um novo.</summary>
    public static string NormalizarOuGerar(string? recebido) =>
        recebido is not null && FormatoValido().IsMatch(recebido)
            ? recebido
            : Guid.NewGuid().ToString();

    // Letras, dígitos, ponto, hífen e sublinhado, até 64: bloqueia quebra de linha e lixo em header e log.
    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex FormatoValido();
}

/// <summary>Lê ou gera o id da requisição, devolve no header da resposta e abre o escopo de log.</summary>
public sealed class MiddlewareDeCorrelationId(RequestDelegate proximo, ILogger<MiddlewareDeCorrelationId> logger)
{
    public async Task InvokeAsync(HttpContext contexto)
    {
        var id = CorrelationId.NormalizarOuGerar(contexto.Request.Headers[CorrelationId.NomeDoHeader].FirstOrDefault());
        CorrelationId.Definir(id);

        // Grava no último instante: o tratador de exceção limpa os headers antes de escrever o erro.
        contexto.Response.OnStarting(() =>
        {
            contexto.Response.Headers[CorrelationId.NomeDoHeader] = id;
            return Task.CompletedTask;
        });

        using (logger.BeginScope(new Dictionary<string, object> { [CorrelationId.ChaveDoLog] = id }))
        {
            await proximo(contexto);
        }
    }
}

/// <summary>Põe o id do fluxo atual no header de toda chamada HTTP de saída, sem trocar um valor já definido.</summary>
public sealed class HandlerDeCorrelationId : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage requisicao, CancellationToken cancelamento)
    {
        var id = CorrelationId.Atual;
        if (id is not null && !requisicao.Headers.Contains(CorrelationId.NomeDoHeader))
            requisicao.Headers.Add(CorrelationId.NomeDoHeader, id);

        return base.SendAsync(requisicao, cancelamento);
    }
}
