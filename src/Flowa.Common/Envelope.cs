using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Flowa.Common;

/// <summary>
/// Contrato D-33: o mesmo formato em sucesso e em erro, em todas as rotas de todas as APIs.
/// <c>{ "data": ..., "error": { "code", "message", "details": [ { "field", "message" } ] }, "meta": { "correlationId", "timestamp" } }</c>
/// </summary>
public sealed record Envelope(object? Data, EnvelopeError? Error, EnvelopeMeta Meta)
{
    /// <summary>Opções fixas do contrato: camelCase e campos nulos sempre escritos.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static Envelope Success(object? data) => new(data, null, NewMeta());

    public static Envelope Failure(string code, string message, IReadOnlyList<ErrorDetail>? details = null) =>
        new(null, new EnvelopeError(code, message, details ?? []), NewMeta());

    private static EnvelopeMeta NewMeta() =>
        new(CorrelationId.Current ?? CorrelationId.NormalizeOrCreate(null), DateTimeOffset.UtcNow);
}

public sealed record EnvelopeError(string Code, string Message, IReadOnlyList<ErrorDetail> Details);

public sealed record ErrorDetail(string Field, string Message);

public sealed record EnvelopeMeta(string CorrelationId, DateTimeOffset Timestamp);

/// <summary>Códigos de erro do contrato e a mensagem (em português, vai para a tela) de cada status HTTP.</summary>
public static class ErrorCodes
{
    public const string Validation = "VALIDATION_ERROR";
    public const string Internal = "INTERNAL_ERROR";

    public const string ValidationMessage = "Um ou mais campos são inválidos.";
    public const string InternalMessage = "Ocorreu um erro inesperado. Informe o correlationId ao suporte.";

    public static string FromStatus(int status) => status switch
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
        >= 500 => Internal,
        _ => "REQUEST_ERROR",
    };

    public static string MessageFromStatus(int status) => status switch
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
        >= 500 => InternalMessage,
        _ => "Não foi possível atender à requisição.",
    };
}

/// <summary>Embrulha no envelope o resultado de toda rota do grupo em que foi registrado.</summary>
public sealed class EnvelopeFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var result = await next(context);
        return ToEnvelope(result, context.HttpContext);
    }

    private static object? ToEnvelope(object? result, HttpContext context)
    {
        switch (result)
        {
            case null or EmptyHttpResult:
                return Write(Envelope.Success(null), DefaultStatus(context));

            case Envelope envelope:
                return Write(envelope, DefaultStatus(context));

            case IValueHttpResult { Value: HttpValidationProblemDetails validation }:
                return Write(ValidationError(validation.Errors), StatusCodes.Status400BadRequest);

            case IValueHttpResult { Value: ProblemDetails problem }:
                var problemStatus = problem.Status ?? StatusCodes.Status500InternalServerError;
                return Write(ErrorFromStatus(problemStatus, problemStatus >= 500 ? null : problem.Detail ?? problem.Title), problemStatus);

            case ContentHttpResult content:
                var contentStatus = content.StatusCode ?? DefaultStatus(context);
                return contentStatus >= 400
                    ? Write(ErrorFromStatus(contentStatus, contentStatus >= 500 ? null : content.ResponseContent), contentStatus)
                    : Write(Envelope.Success(content.ResponseContent), contentStatus);

            // Arquivo, redirecionamento e desafio de autenticação não têm status próprio e seguem como estão;
            // o 401/403 sem corpo do desafio vira envelope no UseStatusCodePages.
            case IResult when result is not IStatusCodeHttpResult:
            // 1xx e 304 não podem ter corpo.
            case IStatusCodeHttpResult { StatusCode: < 200 or StatusCodes.Status304NotModified }:
                return result;

            case IStatusCodeHttpResult { StatusCode: >= 400 } failed:
                var errorStatus = failed.StatusCode!.Value;
                var text = errorStatus >= 500 ? null : (result as IValueHttpResult)?.Value as string;
                return Write(ErrorFromStatus(errorStatus, text), errorStatus);

            case IStatusCodeHttpResult succeeded:
                CopyLocation(result, context);
                var value = (result as IValueHttpResult)?.Value;
                var successStatus = succeeded.StatusCode ?? DefaultStatus(context);
                // 204 não tem corpo; o contrato exige envelope, então vira 200 com data null.
                if (successStatus == StatusCodes.Status204NoContent)
                    successStatus = StatusCodes.Status200OK;
                return Write(value as Envelope ?? Envelope.Success(value), successStatus);

            default:
                return Write(Envelope.Success(result), DefaultStatus(context));
        }
    }

    private static int DefaultStatus(HttpContext context) =>
        context.Response.StatusCode is >= 200 and < 300 and not StatusCodes.Status204NoContent
            ? context.Response.StatusCode
            : StatusCodes.Status200OK;

    // Created/Accepted gravam o Location só quando executados; como o resultado é trocado, o header é copiado aqui.
    private static void CopyLocation(object result, HttpContext context)
    {
        if (result.GetType().GetProperty("Location")?.GetValue(result) is string location && location.Length > 0)
            context.Response.Headers.Location = location;
    }

    internal static Envelope ValidationError(IDictionary<string, string[]> errors) =>
        Envelope.Failure(
            ErrorCodes.Validation,
            ErrorCodes.ValidationMessage,
            errors.SelectMany(field => field.Value.Select(message => new ErrorDetail(field.Key, message))).ToList());

    internal static Envelope ErrorFromStatus(int status, string? message = null) =>
        Envelope.Failure(ErrorCodes.FromStatus(status), message ?? ErrorCodes.MessageFromStatus(status));

    internal static IResult Write(Envelope envelope, int status) =>
        Results.Json(envelope, Envelope.JsonOptions, statusCode: status);
}

/// <summary>Ponto único de erro: exceção não tratada vira envelope, sem stack trace ao cliente.</summary>
public sealed class EnvelopeExceptionHandler(ILogger<EnvelopeExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        Envelope envelope;
        int status;

        if (exception is BadHttpRequestException badRequest)
        {
            // Corpo ilegível, parâmetro obrigatório ausente etc.: culpa da entrada, não do serviço.
            logger.LogWarning(exception, "Invalid request");
            status = badRequest.StatusCode;
            envelope = EnvelopeFilter.ErrorFromStatus(status);
        }
        else
        {
            logger.LogError(exception, "Unexpected error");
            status = StatusCodes.Status500InternalServerError;
            envelope = Envelope.Failure(ErrorCodes.Internal, ErrorCodes.InternalMessage);
        }

        await EnvelopeFilter.Write(envelope, status).ExecuteAsync(context);
        return true;
    }
}

/// <summary>Registro da lib num serviço ASP.NET Core.</summary>
public static class FlowaCommonExtensions
{
    /// <summary>Tratador de exceção e id de correlação em toda chamada HTTP de saída.</summary>
    public static IServiceCollection AddFlowaCommon(this IServiceCollection services)
    {
        services.AddProblemDetails();
        services.AddExceptionHandler<EnvelopeExceptionHandler>();
        services.AddTransient<CorrelationIdHandler>();
        services.ConfigureHttpClientDefaults(client => client.AddHttpMessageHandler<CorrelationIdHandler>());
        return services;
    }

    /// <summary>
    /// Nesta ordem: id de correlação (por fora, para o erro também sair com o id), exceção e status sem corpo
    /// (rota inexistente, 401 do desafio de autenticação) no envelope. Chame antes de mapear as rotas.
    /// </summary>
    public static WebApplication UseFlowaCommon(this WebApplication app)
    {
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseExceptionHandler();
        app.UseStatusCodePages(statusContext =>
        {
            var status = statusContext.HttpContext.Response.StatusCode;
            return EnvelopeFilter.Write(EnvelopeFilter.ErrorFromStatus(status), status).ExecuteAsync(statusContext.HttpContext);
        });
        return app;
    }

    /// <summary>Toda rota mapeada neste grupo responde no envelope. Mapeie todas as rotas do serviço num grupo assim.</summary>
    public static RouteGroupBuilder WithEnvelope(this RouteGroupBuilder group)
    {
        group.AddEndpointFilter<EnvelopeFilter>();
        return group;
    }
}
