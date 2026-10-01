using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Flowa.Common.Tests;

/// <summary>Contrato D-33 (CA-28): o mesmo envelope em sucesso e em erro.</summary>
public sealed class EnvelopeTests : IAsyncLifetime
{
    private const string GenericInternalMessage = "Ocorreu um erro inesperado. Informe o correlationId ao suporte.";

    private FlowaTestServer server = null!;

    public async Task InitializeAsync() => server = await FlowaTestServer.StartAsync();

    public async Task DisposeAsync() => await server.DisposeAsync();

    [Fact]
    public async Task SuccessHasExactlyDataErrorMetaWithContractFields()
    {
        // Act
        var (response, body) = await server.SendAsync(HttpMethod.Get, "/api/object", "success-id");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(["data", "error", "meta"], PropertyNames(body));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("error").ValueKind);
        Assert.Equal("flowa", body.GetProperty("data").GetProperty("name").GetString());
        Assert.Equal(3, body.GetProperty("data").GetProperty("quantity").GetInt32());
        Assert.Equal(["correlationId", "timestamp"], PropertyNames(body.GetProperty("meta")));
        Assert.Equal("success-id", body.GetProperty("meta").GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task TimestampIsIso8601UtcAtResponseTime()
    {
        // Arrange
        var before = DateTimeOffset.UtcNow;

        // Act
        var (_, body) = await server.SendAsync(HttpMethod.Get, "/api/object");
        var after = DateTimeOffset.UtcNow;

        // Assert
        var text = body.GetProperty("meta").GetProperty("timestamp").GetString()!;
        Assert.EndsWith("+00:00", text);
        Assert.InRange(DateTimeOffset.Parse(text), before, after);
    }

    [Fact]
    public async Task TypedOkGoesIntoDataWithStatus200()
    {
        var (response, body) = await server.SendAsync(HttpMethod.Get, "/api/ok");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("PETR4", body.GetProperty("data").GetProperty("code").GetString());
        Assert.Equal(10, body.GetProperty("data").GetProperty("quantity").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("error").ValueKind);
    }

    [Fact]
    public async Task CreatedKeeps201AndLocation()
    {
        var (response, body) = await server.SendAsync(HttpMethod.Post, "/api/created");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("/api/items/7", response.Headers.Location?.OriginalString);
        Assert.Equal("VALE3", body.GetProperty("data").GetProperty("code").GetString());
    }

    [Fact]
    public async Task AcceptedKeeps202AndLocation()
    {
        var (response, body) = await server.SendAsync(HttpMethod.Post, "/api/accepted");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("/api/items/8", response.Headers.Location?.OriginalString);
        Assert.Equal("BLOG11", body.GetProperty("data").GetProperty("code").GetString());
    }

    [Fact]
    public async Task StatusSetByRouteOnResponseIsKept()
    {
        var (response, body) = await server.SendAsync(HttpMethod.Get, "/api/custom-status");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("BSCI11", body.GetProperty("data").GetProperty("code").GetString());
    }

    [Fact]
    public async Task NoContentBecomesOkWithNullData()
    {
        var (response, body) = await server.SendAsync(HttpMethod.Delete, "/api/no-content");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("data").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("error").ValueKind);
    }

    [Fact]
    public async Task VoidRouteReturnsEnvelopeWithNullData()
    {
        var (response, body) = await server.SendAsync(HttpMethod.Get, "/api/void");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("data").ValueKind);
        Assert.Equal(["data", "error", "meta"], PropertyNames(body));
    }

    [Fact]
    public async Task EnvelopeReturnedByRouteIsNotWrappedAgain()
    {
        var (_, body) = await server.SendAsync(HttpMethod.Get, "/api/ready-envelope");

        Assert.Equal("VIIA4", body.GetProperty("data").GetProperty("code").GetString());
        Assert.False(body.GetProperty("data").TryGetProperty("data", out _));
    }

    [Fact]
    public async Task HealthRouteAlsoReturnsEnvelope()
    {
        var (response, body) = await server.SendAsync(HttpMethod.Get, "/api/test/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", body.GetProperty("data").GetProperty("status").GetString());
        Assert.Equal(["data", "error", "meta"], PropertyNames(body));
    }

    [Fact]
    public async Task PlainTextGoesIntoDataOfEnvelope()
    {
        var (response, body) = await server.SendAsync(HttpMethod.Get, "/api/text");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("ola", body.GetProperty("data").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("error").ValueKind);
    }

    [Fact]
    public async Task TextWith4xxBecomesErrorWithTextAsMessage()
    {
        var (response, body) = await server.SendAsync(HttpMethod.Get, "/api/text-conflict");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("data").ValueKind);
        Assert.Equal("CONFLICT", body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Oferta sem estoque.", body.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task TextWith5xxDoesNotLeakTheText()
    {
        var (response, body) = await server.SendAsync(HttpMethod.Get, "/api/text-500");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("INTERNAL_ERROR", body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(GenericInternalMessage, body.GetProperty("error").GetProperty("message").GetString());
        Assert.DoesNotContain("detalhe-interno-do-servidor", body.GetRawText());
    }

    [Fact]
    public async Task NotModifiedKeeps304WithoutBody()
    {
        var response = await server.Client.GetAsync("/api/not-modified");

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ValidationErrorReturns400WithOneDetailPerFieldMessage()
    {
        // Act
        var (response, body) = await server.SendAsync(HttpMethod.Post, "/api/validation", "validation-id");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["data", "error", "meta"], PropertyNames(body));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("data").ValueKind);
        var error = body.GetProperty("error");
        Assert.Equal(["code", "message", "details"], PropertyNames(error));
        Assert.Equal("VALIDATION_ERROR", error.GetProperty("code").GetString());
        Assert.Equal("Um ou mais campos são inválidos.", error.GetProperty("message").GetString());
        var details = error.GetProperty("details").EnumerateArray()
            .Select(detail => (detail.GetProperty("field").GetString(), detail.GetProperty("message").GetString()))
            .ToList();
        Assert.Equal(
            [("quantity", "Deve ser maior que zero."), ("quantity", "Deve ser menor que 100000."), ("code", "Obrigatório.")],
            details);
        Assert.Equal("validation-id", body.GetProperty("meta").GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task UnreadableBodyReturns400BadRequestWithoutErrorLog()
    {
        // Arrange
        var unreadableJson = new StringContent("{ isto nao e json", Encoding.UTF8, "application/json");

        // Act
        var (response, body) = await server.SendAsync(HttpMethod.Post, "/api/body", "bad-body-id", unreadableJson);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("BAD_REQUEST", body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Requisição inválida.", body.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("data").ValueKind);
        Assert.DoesNotContain(server.Logs.Entries, entry => entry.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task MissingBodyReturns400BadRequestNotSuccess()
    {
        var (response, body) = await server.SendAsync(HttpMethod.Post, "/api/body", "missing-body-id");

        AssertBadRequestEnvelope(response, body, "missing-body-id");
    }

    [Theory]
    [InlineData("/api/quote")]
    [InlineData("/api/quote?quantity=abc")]
    public async Task MissingOrInvalidQueryReturns400BadRequestNotSuccess(string path)
    {
        var (response, body) = await server.SendAsync(HttpMethod.Get, path, "bad-query-id");

        AssertBadRequestEnvelope(response, body, "bad-query-id");
    }

    [Fact]
    public async Task StatusWrittenByRouteWithoutBodyBecomesErrorEnvelope()
    {
        var (response, body) = await server.SendAsync(HttpMethod.Post, "/api/conflict-status", "conflict-status-id");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("data").ValueKind);
        Assert.Equal("CONFLICT", body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Conflito com o estado atual do recurso.", body.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal("conflict-status-id", body.GetProperty("meta").GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task NativeValidationReturnsValidationErrorEnvelope()
    {
        // Arrange
        var invalidReservation = new StringContent("""{ "quantity": 0 }""", Encoding.UTF8, "application/json");

        // Act
        var (response, body) = await server.SendAsync(HttpMethod.Post, "/api/validated", "native-validation-id", invalidReservation);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(["data", "error", "meta"], PropertyNames(body));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("data").ValueKind);
        Assert.Equal("VALIDATION_ERROR", body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Um ou mais campos são inválidos.", body.GetProperty("error").GetProperty("message").GetString());
        var details = body.GetProperty("error").GetProperty("details").EnumerateArray()
            .Select(detail => (detail.GetProperty("field").GetString(), detail.GetProperty("message").GetString()))
            .OrderBy(detail => detail.Item1)
            .ToList();
        Assert.Equal(
            [("offerCode", "Informe a oferta."), ("quantity", "A quantidade deve estar entre 1 e 100000.")],
            details);
        Assert.Equal("native-validation-id", body.GetProperty("meta").GetProperty("correlationId").GetString());
        Assert.Equal("native-validation-id", Assert.Single(response.Headers.GetValues(CorrelationId.HeaderName)));
    }

    [Fact]
    public async Task ValidationFieldPathIsConvertedToJsonCamelCase()
    {
        var (response, body) = await server.SendAsync(HttpMethod.Post, "/api/validation-nested");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("VALIDATION_ERROR", body.GetProperty("error").GetProperty("code").GetString());
        var detail = Assert.Single(body.GetProperty("error").GetProperty("details").EnumerateArray());
        Assert.Equal("order.offerCode", detail.GetProperty("field").GetString());
        Assert.Equal("Informe a oferta.", detail.GetProperty("message").GetString());
    }

    [Fact]
    public async Task NativeValidationLetsValidRequestThrough()
    {
        var validReservation = new StringContent("""{ "offerCode": "BLOG11", "quantity": 10 }""", Encoding.UTF8, "application/json");

        var (response, body) = await server.SendAsync(HttpMethod.Post, "/api/validated", content: validReservation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("BLOG11", body.GetProperty("data").GetProperty("offerCode").GetString());
        Assert.Equal(10, body.GetProperty("data").GetProperty("quantity").GetInt32());
    }

    [Fact]
    public async Task BadHttpRequestUsesExceptionStatusAndLogsWarning()
    {
        var (response, body) = await server.SendAsync(HttpMethod.Get, "/api/throw-413", "id-413");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("REQUEST_ERROR", body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Não foi possível atender à requisição.", body.GetProperty("error").GetProperty("message").GetString());
        var entry = Assert.Single(server.Logs.Entries, log => log.Message == "Invalid request");
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.IsType<BadHttpRequestException>(entry.Exception);
        Assert.Equal("id-413", entry.Scope[CorrelationId.LogScopeKey]);
    }

    [Fact]
    public async Task UnhandledExceptionReturnsGeneric500WithoutStackTrace()
    {
        // Act
        var (response, body) = await server.SendAsync(HttpMethod.Get, "/api/throw", "error-id");
        var envelopeText = body.GetRawText();

        // Assert
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(["data", "error", "meta"], PropertyNames(body));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("data").ValueKind);
        Assert.Equal("INTERNAL_ERROR", body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(GenericInternalMessage, body.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal(0, body.GetProperty("error").GetProperty("details").GetArrayLength());
        Assert.Equal("error-id", body.GetProperty("meta").GetProperty("correlationId").GetString());
        Assert.DoesNotContain("detalhe-interno-do-servidor", envelopeText);
        Assert.DoesNotContain("InvalidOperationException", envelopeText);
        Assert.DoesNotContain(" at ", envelopeText);
        Assert.DoesNotContain(".cs:line", envelopeText);
    }

    [Fact]
    public async Task UnhandledExceptionIsLoggedOnceAsErrorWithTheException()
    {
        await server.SendAsync(HttpMethod.Get, "/api/throw", "log-error-id");

        var entry = Assert.Single(server.Logs.Entries, log => log.Level == LogLevel.Error);
        Assert.Equal("Unexpected error", entry.Message);
        Assert.IsType<InvalidOperationException>(entry.Exception);
        Assert.Equal("log-error-id", entry.Scope[CorrelationId.LogScopeKey]);
    }

    [Fact]
    public async Task NotFoundFromRouteBecomesNotFoundEnvelope()
    {
        var (response, body) = await server.SendAsync(HttpMethod.Get, "/api/not-found");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("NOT_FOUND", body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Recurso não encontrado.", body.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task UnknownRouteBecomesNotFoundEnvelope()
    {
        var (response, body) = await server.SendAsync(HttpMethod.Get, "/api/does-not-exist", "unknown-route-id");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(["data", "error", "meta"], PropertyNames(body));
        Assert.Equal("NOT_FOUND", body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Recurso não encontrado.", body.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal("unknown-route-id", body.GetProperty("meta").GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task ConflictWithTextUsesTextAsMessage()
    {
        var (response, body) = await server.SendAsync(HttpMethod.Get, "/api/conflict");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("CONFLICT", body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Estoque insuficiente.", body.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task BusinessProblemUsesStatusAndDetail()
    {
        var (response, body) = await server.SendAsync(HttpMethod.Get, "/api/problem");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("UNPROCESSABLE_ENTITY", body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Oferta encerrada.", body.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task ErrorEnvelopeBuiltByRouteKeepsItsCodeAndMessage()
    {
        var (response, body) = await server.SendAsync(HttpMethod.Get, "/api/route-error-envelope");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("data").ValueKind);
        Assert.Equal("OFFER_CLOSED", body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Oferta encerrada.", body.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task SuccessEnvelopeWithErrorStatusBecomesErrorEnvelope()
    {
        var (response, body) = await server.SendAsync(HttpMethod.Get, "/api/success-envelope-with-409");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("data").ValueKind);
        Assert.Equal("CONFLICT", body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Conflito com o estado atual do recurso.", body.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task InformationalStatusWrittenByRouteBecomes200Envelope()
    {
        var (response, body) = await server.SendAsync(HttpMethod.Get, "/api/informational-status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("CPAB07", body.GetProperty("data").GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("error").ValueKind);
    }

    [Fact]
    public async Task DtoFieldNamedLocationIsDataNotHeader()
    {
        var (response, body) = await server.SendAsync(HttpMethod.Get, "/api/dto-with-location");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal("São Paulo", body.GetProperty("data").GetProperty("location").GetString());
    }

    [Fact]
    public async Task ProblemWithFrameworkDefaultTitleUsesPortugueseMessage()
    {
        var (response, body) = await server.SendAsync(HttpMethod.Get, "/api/problem-404");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("NOT_FOUND", body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Recurso não encontrado.", body.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task ProblemOutsideGroupWith4xxBecomesEnvelopeWithDetail()
    {
        var (response, body) = await server.SendAsync(HttpMethod.Get, "/outside/problem-422", "outside-422-id");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(["data", "error", "meta"], PropertyNames(body));
        Assert.Equal("UNPROCESSABLE_ENTITY", body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Oferta encerrada.", body.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal("outside-422-id", body.GetProperty("meta").GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task ProblemOutsideGroupWith5xxIsGenericWithoutDetail()
    {
        var (response, body) = await server.SendAsync(HttpMethod.Get, "/outside/problem-500");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("INTERNAL_ERROR", body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(GenericInternalMessage, body.GetProperty("error").GetProperty("message").GetString());
        Assert.DoesNotContain("detalhe-interno-do-servidor", body.GetRawText());
    }

    [Fact]
    public async Task NativeValidationStaysInEnvelopeWhenProblemDetailsWasRegisteredFirst()
    {
        // Arrange
        await using var serverWithEarlyProblemDetails = await FlowaTestServer.StartAsync(problemDetailsRegisteredFirst: true);
        var invalidReservation = new StringContent("""{ "quantity": 0 }""", Encoding.UTF8, "application/json");

        // Act
        var (response, body) = await serverWithEarlyProblemDetails.SendAsync(HttpMethod.Post, "/api/validated", "early-pd-id", invalidReservation);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("VALIDATION_ERROR", body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(2, body.GetProperty("error").GetProperty("details").GetArrayLength());
        Assert.Equal("early-pd-id", body.GetProperty("meta").GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task ProblemWithoutDetailUsesTitle()
    {
        var (response, body) = await server.SendAsync(HttpMethod.Get, "/api/problem-title");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("BAD_REQUEST", body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Prospecto não aceito.", body.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task Problem500DoesNotLeakInternalDetail()
    {
        var (response, body) = await server.SendAsync(HttpMethod.Get, "/api/problem-500");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("INTERNAL_ERROR", body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(GenericInternalMessage, body.GetProperty("error").GetProperty("message").GetString());
        Assert.DoesNotContain("detalhe-interno-do-servidor", body.GetRawText());
    }

    [Fact]
    public async Task InternalServerErrorWithTextDoesNotLeakTheText()
    {
        var (response, body) = await server.SendAsync(HttpMethod.Get, "/api/internal-error-text");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("INTERNAL_ERROR", body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(GenericInternalMessage, body.GetProperty("error").GetProperty("message").GetString());
        Assert.DoesNotContain("detalhe-interno-do-servidor", body.GetRawText());
    }

    [Fact]
    public void FailureFactoryBuildsCodeMessageAndDetails()
    {
        // Act
        var envelope = Envelope.Failure("CONFLICT", "Estoque insuficiente.", [new ErrorDetail("quantity", "Acima do disponível.")]);

        // Assert
        Assert.Null(envelope.Data);
        Assert.Equal("CONFLICT", envelope.Error!.Code);
        Assert.Equal("Estoque insuficiente.", envelope.Error.Message);
        Assert.Equal(new ErrorDetail("quantity", "Acima do disponível."), Assert.Single(envelope.Error.Details));
    }

    [Theory]
    [InlineData(400, "BAD_REQUEST", "Requisição inválida.")]
    [InlineData(401, "UNAUTHORIZED", "Não autenticado.")]
    [InlineData(403, "FORBIDDEN", "Acesso negado.")]
    [InlineData(404, "NOT_FOUND", "Recurso não encontrado.")]
    [InlineData(405, "METHOD_NOT_ALLOWED", "Método não permitido para esta rota.")]
    [InlineData(409, "CONFLICT", "Conflito com o estado atual do recurso.")]
    [InlineData(422, "UNPROCESSABLE_ENTITY", "A requisição não pôde ser processada.")]
    [InlineData(429, "TOO_MANY_REQUESTS", "Muitas requisições. Tente de novo em instantes.")]
    [InlineData(418, "REQUEST_ERROR", "Não foi possível atender à requisição.")]
    [InlineData(500, "INTERNAL_ERROR", GenericInternalMessage)]
    [InlineData(502, "INTERNAL_ERROR", GenericInternalMessage)]
    [InlineData(503, "SERVICE_UNAVAILABLE", "Serviço indisponível no momento.")]
    public void ErrorCodeAndMessageByStatus(int status, string expectedCode, string expectedMessage)
    {
        Assert.Equal(expectedCode, ErrorCodes.FromStatus(status));
        Assert.Equal(expectedMessage, ErrorCodes.MessageFromStatus(status));
    }

    private static void AssertBadRequestEnvelope(HttpResponseMessage response, JsonElement body, string expectedCorrelationId)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["data", "error", "meta"], PropertyNames(body));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("data").ValueKind);
        Assert.Equal("BAD_REQUEST", body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Requisição inválida.", body.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal(expectedCorrelationId, body.GetProperty("meta").GetProperty("correlationId").GetString());
    }

    private static string[] PropertyNames(JsonElement element) =>
        element.EnumerateObject().Select(property => property.Name).ToArray();
}
