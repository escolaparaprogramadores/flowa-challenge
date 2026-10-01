using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Flowa.Comum;

/// <summary>
/// Contrato D-33: o mesmo formato em sucesso e em erro, em todas as rotas de todas as APIs.
/// <c>{ "data": ..., "error": { "code", "message", "details": [ { "field", "message" } ] }, "meta": { "correlationId", "timestamp" } }</c>
/// </summary>
public sealed record Envelope(object? Data, ErroDoEnvelope? Error, MetaDoEnvelope Meta)
{
    /// <summary>Opções fixas do contrato: camelCase e campos nulos sempre escritos.</summary>
    public static readonly JsonSerializerOptions OpcoesJson = new(JsonSerializerDefaults.Web);

    public static Envelope Sucesso(object? data) => new(data, null, NovoMeta());

    public static Envelope Erro(string code, string message, IReadOnlyList<DetalheDoErro>? details = null) =>
        new(null, new ErroDoEnvelope(code, message, details ?? []), NovoMeta());

    private static MetaDoEnvelope NovoMeta() =>
        new(CorrelationId.Atual ?? CorrelationId.NormalizarOuGerar(null), DateTimeOffset.UtcNow);
}

public sealed record ErroDoEnvelope(string Code, string Message, IReadOnlyList<DetalheDoErro> Details);

public sealed record DetalheDoErro(string Field, string Message);

public sealed record MetaDoEnvelope(string CorrelationId, DateTimeOffset Timestamp);

/// <summary>Códigos e mensagens de erro do contrato, derivados do status HTTP.</summary>
public static class CodigosDeErro
{
    public const string Validacao = "VALIDATION_ERROR";
    public const string Interno = "INTERNAL_ERROR";

    public const string MensagemDeValidacao = "Um ou mais campos são inválidos.";
    public const string MensagemInterna = "Ocorreu um erro inesperado. Informe o correlationId ao suporte.";

    public static string PorStatus(int status) => status switch
    {
        400 => "BAD_REQUEST",
        401 => "UNAUTHORIZED",
        403 => "FORBIDDEN",
        404 => "NOT_FOUND",
        405 => "METHOD_NOT_ALLOWED",
        409 => "CONFLICT",
        422 => "UNPROCESSABLE_ENTITY",
        429 => "TOO_MANY_REQUESTS",
        503 => "SERVICE_UNAVAILABLE",
        >= 500 => Interno,
        _ => "REQUEST_ERROR",
    };

    public static string MensagemPorStatus(int status) => status switch
    {
        400 => "Requisição inválida.",
        401 => "Não autenticado.",
        403 => "Acesso negado.",
        404 => "Recurso não encontrado.",
        405 => "Método não permitido para esta rota.",
        409 => "Conflito com o estado atual do recurso.",
        422 => "A requisição não pôde ser processada.",
        429 => "Muitas requisições. Tente de novo em instantes.",
        503 => "Serviço indisponível no momento.",
        >= 500 => MensagemInterna,
        _ => "Não foi possível atender à requisição.",
    };
}

/// <summary>Embrulha no envelope o resultado de toda rota do grupo em que foi registrado.</summary>
public sealed class FiltroDeEnvelope : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext contexto, EndpointFilterDelegate proximo)
    {
        var resultado = await proximo(contexto);
        return ParaEnvelope(resultado, contexto.HttpContext);
    }

    private static object? ParaEnvelope(object? resultado, HttpContext contexto)
    {
        switch (resultado)
        {
            case null or EmptyHttpResult:
                return Escrever(Envelope.Sucesso(null), StatusPadrao(contexto));

            case Envelope envelope:
                return Escrever(envelope, StatusPadrao(contexto));

            case IValueHttpResult { Value: HttpValidationProblemDetails validacao }:
                return Escrever(ErroDeValidacao(validacao.Errors), StatusCodes.Status400BadRequest);

            case IValueHttpResult { Value: ProblemDetails problema }:
                var statusDoProblema = problema.Status ?? StatusCodes.Status500InternalServerError;
                var mensagem = statusDoProblema >= 500 ? null : problema.Detail ?? problema.Title;
                return Escrever(ErroPorStatus(statusDoProblema, mensagem), statusDoProblema);

            // Texto, arquivo, redirecionamento e desafio de autenticação seguem como estão.
            case IResult when resultado is not IStatusCodeHttpResult:
            case IContentTypeHttpResult when resultado is not IValueHttpResult:
                return resultado;

            case IStatusCodeHttpResult { StatusCode: >= 400 } comErro:
                var status = comErro.StatusCode!.Value;
                var texto = status >= 500 ? null : (resultado as IValueHttpResult)?.Value as string;
                return Escrever(ErroPorStatus(status, texto), status);

            case IStatusCodeHttpResult comSucesso:
                CopiarLocation(resultado, contexto);
                var valor = (resultado as IValueHttpResult)?.Value;
                var statusDeSucesso = comSucesso.StatusCode ?? StatusPadrao(contexto);
                // 204 não tem corpo; o contrato exige envelope, então vira 200 com data null.
                if (statusDeSucesso == StatusCodes.Status204NoContent)
                    statusDeSucesso = StatusCodes.Status200OK;
                return Escrever(valor as Envelope ?? Envelope.Sucesso(valor), statusDeSucesso);

            default:
                return Escrever(Envelope.Sucesso(resultado), StatusPadrao(contexto));
        }
    }

    private static int StatusPadrao(HttpContext contexto) =>
        contexto.Response.StatusCode is >= 200 and < 300 and not StatusCodes.Status204NoContent
            ? contexto.Response.StatusCode
            : StatusCodes.Status200OK;

    // Created/Accepted gravam o Location só quando executados; como o resultado é trocado, o header é copiado aqui.
    private static void CopiarLocation(object resultado, HttpContext contexto)
    {
        if (resultado.GetType().GetProperty("Location")?.GetValue(resultado) is string location && location.Length > 0)
            contexto.Response.Headers.Location = location;
    }

    internal static Envelope ErroDeValidacao(IDictionary<string, string[]> erros) =>
        Envelope.Erro(
            CodigosDeErro.Validacao,
            CodigosDeErro.MensagemDeValidacao,
            erros.SelectMany(campo => campo.Value.Select(mensagem => new DetalheDoErro(campo.Key, mensagem))).ToList());

    internal static Envelope ErroPorStatus(int status, string? mensagem = null) =>
        Envelope.Erro(CodigosDeErro.PorStatus(status), mensagem ?? CodigosDeErro.MensagemPorStatus(status));

    internal static IResult Escrever(Envelope envelope, int status) =>
        Results.Json(envelope, Envelope.OpcoesJson, statusCode: status);
}

/// <summary>Ponto único de erro: exceção não tratada vira envelope, sem stack trace ao cliente.</summary>
public sealed class TratadorDeExcecao(ILogger<TratadorDeExcecao> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext contexto, Exception excecao, CancellationToken cancelamento)
    {
        Envelope envelope;
        int status;

        if (excecao is BadHttpRequestException requisicaoInvalida)
        {
            // Corpo ilegível, parâmetro obrigatório ausente etc.: culpa da entrada, não do serviço.
            logger.LogWarning(excecao, "Requisição inválida");
            status = requisicaoInvalida.StatusCode;
            envelope = FiltroDeEnvelope.ErroPorStatus(status);
        }
        else
        {
            logger.LogError(excecao, "Erro inesperado");
            status = StatusCodes.Status500InternalServerError;
            envelope = Envelope.Erro(CodigosDeErro.Interno, CodigosDeErro.MensagemInterna);
        }

        await FiltroDeEnvelope.Escrever(envelope, status).ExecuteAsync(contexto);
        return true;
    }
}

/// <summary>Registro da lib num serviço ASP.NET Core.</summary>
public static class ExtensoesFlowaComum
{
    /// <summary>Tratador de exceção e id de correlação em toda chamada HTTP de saída.</summary>
    public static IServiceCollection AdicionarFlowaComum(this IServiceCollection servicos)
    {
        servicos.AddProblemDetails();
        servicos.AddExceptionHandler<TratadorDeExcecao>();
        servicos.AddTransient<HandlerDeCorrelationId>();
        servicos.ConfigureHttpClientDefaults(cliente => cliente.AddHttpMessageHandler<HandlerDeCorrelationId>());
        return servicos;
    }

    /// <summary>
    /// Nesta ordem: id de correlação (por fora, para o erro também sair com o id), exceção e status sem corpo
    /// (rota inexistente, 401 do desafio de autenticação) no envelope. Chame antes do roteamento.
    /// </summary>
    public static WebApplication UsarFlowaComum(this WebApplication app)
    {
        app.UseMiddleware<MiddlewareDeCorrelationId>();
        app.UseExceptionHandler();
        app.UseStatusCodePages(contexto =>
        {
            var status = contexto.HttpContext.Response.StatusCode;
            return FiltroDeEnvelope.Escrever(FiltroDeEnvelope.ErroPorStatus(status), status).ExecuteAsync(contexto.HttpContext);
        });
        return app;
    }

    /// <summary>Toda rota mapeada neste grupo responde no envelope.</summary>
    public static RouteGroupBuilder ComEnvelope(this RouteGroupBuilder grupo)
    {
        grupo.AddEndpointFilter<FiltroDeEnvelope>();
        return grupo;
    }
}
