using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
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
        var endpointResult = await next(context);
        return ToEnvelope(endpointResult, context.HttpContext);
    }

    private static object? ToEnvelope(object? endpointResult, HttpContext context)
    {
        // Arquivo, redirecionamento e desafio de autenticação não têm status próprio (o 401/403 sem corpo do
        // desafio vira envelope no UseStatusCodePages); 1xx e 304 não podem ter corpo. Seguem como estão.
        if (endpointResult is IResult and not IStatusCodeHttpResult and not EmptyHttpResult
            || endpointResult is IStatusCodeHttpResult { StatusCode: < 200 or StatusCodes.Status304NotModified })
            return endpointResult;

        // Sem status no resultado vale o da resposta: falha de binding (corpo ou query ausente/inválido) chega
        // como EmptyHttpResult com 400 já gravado, e a rota pode gravar o status à mão.
        var status = (endpointResult as IStatusCodeHttpResult)?.StatusCode ?? context.Response.StatusCode;
        // 204 não tem corpo; o contrato exige envelope, então vira 200 com data null.
        if (status is < 200 or StatusCodes.Status204NoContent)
            status = StatusCodes.Status200OK;

        var returnedData = endpointResult switch
        {
            IValueHttpResult valueResult => valueResult.Value,
            ContentHttpResult textResult => textResult.ResponseContent,
            IResult => null,
            _ => endpointResult,
        };

        if (status < 400)
        {
            CopyLocation(endpointResult, context);
            return ToEnvelopeJsonResult(returnedData as Envelope ?? Envelope.Success(returnedData), status);
        }

        var errorEnvelope = returnedData switch
        {
            HttpValidationProblemDetails validation => ValidationError(validation.Errors),
            _ when status >= 500 => ErrorFromStatus(status),
            Envelope routeEnvelope => routeEnvelope,
            ProblemDetails problem => ErrorFromStatus(status, RouteWrittenText(problem, status)),
            string errorText => ErrorFromStatus(status, errorText),
            _ => ErrorFromStatus(status),
        };
        return ToEnvelopeJsonResult(errorEnvelope, status);
    }

    // Created/Accepted gravam o Location só quando executados; como o resultado é trocado, o header é copiado aqui.
    // Só de IResult: um DTO devolvido pela rota pode ter um campo Location que não é header.
    private static void CopyLocation(object? endpointResult, HttpContext context)
    {
        if (endpointResult is IResult && endpointResult.GetType().GetProperty("Location")?.GetValue(endpointResult) is string location && location.Length > 0)
            context.Response.Headers.Location = location;
    }

    // O framework preenche Title com a frase padrão do status, em inglês ("Not Found"); essa não vai para a tela.
    internal static string? RouteWrittenText(ProblemDetails problem, int status) =>
        problem.Detail ?? (problem.Title == ReasonPhrases.GetReasonPhrase(status) ? null : problem.Title);

    internal static Envelope ValidationError(IDictionary<string, string[]> errors) =>
        Envelope.Failure(
            ErrorCodes.Validation,
            ErrorCodes.ValidationMessage,
            errors.SelectMany(field => field.Value.Select(message => new ErrorDetail(ToJsonFieldName(field.Key), message))).ToList());

    // A validação nativa devolve o nome da propriedade C# ("Order.OfferCode"); o cliente conhece o do JSON ("order.offerCode").
    private static string ToJsonFieldName(string propertyPath) =>
        string.Join('.', propertyPath.Split('.').Select(segment => JsonNamingPolicy.CamelCase.ConvertName(segment)));

    internal static Envelope ErrorFromStatus(int status, string? message = null) =>
        Envelope.Failure(ErrorCodes.FromStatus(status), message ?? ErrorCodes.MessageFromStatus(status));

    internal static IResult ToEnvelopeJsonResult(Envelope envelope, int status) =>
        Results.Json(envelope, Envelope.JsonOptions, statusCode: status);
}

/// <summary>
/// ProblemDetails escrito pelo próprio framework (validação nativa do <c>AddValidation</c>, ProblemHttpResult
/// fora do grupo) também sai no envelope.
/// </summary>
public sealed class EnvelopeProblemDetailsWriter : IProblemDetailsWriter
{
    public bool CanWrite(ProblemDetailsContext context) => true;

    public ValueTask WriteAsync(ProblemDetailsContext context)
    {
        var status = context.ProblemDetails.Status ?? context.HttpContext.Response.StatusCode;
        var errorEnvelope = context.ProblemDetails switch
        {
            HttpValidationProblemDetails validation => EnvelopeFilter.ValidationError(validation.Errors),
            _ when status >= 500 => EnvelopeFilter.ErrorFromStatus(status),
            var problem => EnvelopeFilter.ErrorFromStatus(status, EnvelopeFilter.RouteWrittenText(problem, status)),
        };
        return new ValueTask(EnvelopeFilter.ToEnvelopeJsonResult(errorEnvelope, status).ExecuteAsync(context.HttpContext));
    }
}

/// <summary>Ponto único de erro: exceção não tratada vira envelope, sem stack trace ao cliente.</summary>
public sealed class EnvelopeExceptionHandler(ILogger<EnvelopeExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        Envelope errorEnvelope;
        int status;

        if (exception is BadHttpRequestException badRequest)
        {
            // Corpo ilegível, parâmetro obrigatório ausente etc.: culpa da entrada, não do serviço.
            logger.LogWarning(exception, "Invalid request");
            status = badRequest.StatusCode;
            errorEnvelope = EnvelopeFilter.ErrorFromStatus(status);
        }
        else
        {
            logger.LogError(exception, "Unexpected error");
            status = StatusCodes.Status500InternalServerError;
            errorEnvelope = Envelope.Failure(ErrorCodes.Internal, ErrorCodes.InternalMessage);
        }

        await EnvelopeFilter.ToEnvelopeJsonResult(errorEnvelope, status).ExecuteAsync(context);
        return true;
    }
}

/// <summary>Registro da lib num serviço ASP.NET Core.</summary>
public static class FlowaCommonExtensions
{
    /// <summary>Tratador de exceção, ProblemDetails no envelope e id de correlação em toda chamada HTTP de saída.</summary>
    public static IServiceCollection AddFlowaCommon(this IServiceCollection services)
    {
        services.AddProblemDetails();
        // Primeiro da lista, para ser o escritor consultado mesmo se o serviço já chamou AddProblemDetails antes.
        services.Insert(0, ServiceDescriptor.Singleton<IProblemDetailsWriter, EnvelopeProblemDetailsWriter>());
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
            return EnvelopeFilter.ToEnvelopeJsonResult(EnvelopeFilter.ErrorFromStatus(status), status)
                .ExecuteAsync(statusContext.HttpContext);
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
