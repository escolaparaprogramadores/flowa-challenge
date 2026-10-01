using System.Net;

namespace Flowa.Common.Tests;

/// <summary>Contrato D-34 (CA-29): X-Correlation-Id da entrada à resposta, ao log e à chamada de saída.</summary>
public sealed class CorrelationIdTests : IAsyncLifetime
{
    private FlowaTestServer server = null!;

    public async Task InitializeAsync() => server = await FlowaTestServer.StartAsync();

    public async Task DisposeAsync() => await server.DisposeAsync();

    [Fact]
    public void HeaderNameIsXCorrelationId()
    {
        Assert.Equal("X-Correlation-Id", CorrelationId.HeaderName);
    }

    [Fact]
    public void LogScopeKeyIsCorrelationId()
    {
        Assert.Equal("CorrelationId", CorrelationId.LogScopeKey);
    }

    [Fact]
    public async Task ReceivedIdComesBackInHeaderAndMeta()
    {
        // Act
        var (response, body) = await server.SendAsync(HttpMethod.Get, "/api/object", "order-123_abc.XYZ");

        // Assert
        Assert.Equal("order-123_abc.XYZ", Assert.Single(response.Headers.GetValues(CorrelationId.HeaderName)));
        Assert.Equal("order-123_abc.XYZ", body.GetProperty("meta").GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task MissingHeaderCreatesNewGuidEqualInHeaderAndMeta()
    {
        // Act
        var (response, body) = await server.SendAsync(HttpMethod.Get, "/api/object");

        // Assert
        var inHeader = Assert.Single(response.Headers.GetValues(CorrelationId.HeaderName));
        Assert.True(Guid.TryParseExact(inHeader, "D", out _), $"id gerado não é GUID no formato D: {inHeader}");
        Assert.Equal(inHeader, body.GetProperty("meta").GetProperty("correlationId").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("with space")]
    [InlineData("<script>")]
    [InlineData("a;b")]
    [InlineData("12345678901234567890123456789012345678901234567890123456789012345")]
    public async Task InvalidIdIsDiscardedAndAnotherCreated(string invalid)
    {
        // Act
        var (response, body) = await server.SendAsync(HttpMethod.Get, "/api/object", invalid);

        // Assert
        var inHeader = Assert.Single(response.Headers.GetValues(CorrelationId.HeaderName));
        Assert.NotEqual(invalid, inHeader);
        Assert.True(Guid.TryParseExact(inHeader, "D", out _), $"id gerado não é GUID no formato D: {inHeader}");
        Assert.Equal(inHeader, body.GetProperty("meta").GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task IdComesBackInHeaderAlsoOn500()
    {
        var (response, body) = await server.SendAsync(HttpMethod.Get, "/api/throw", "id-of-500");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("id-of-500", Assert.Single(response.Headers.GetValues(CorrelationId.HeaderName)));
        Assert.Equal("id-of-500", body.GetProperty("meta").GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task IdComesBackInHeaderAndMetaAlsoOnUnknownRoute()
    {
        var (response, body) = await server.SendAsync(HttpMethod.Get, "/api/does-not-exist", "id-of-404");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("id-of-404", Assert.Single(response.Headers.GetValues(CorrelationId.HeaderName)));
        Assert.Equal("id-of-404", body.GetProperty("meta").GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task RequestLogCarriesIdInScope()
    {
        // Act
        await server.SendAsync(HttpMethod.Get, "/api/log", "log-id");

        // Assert
        var entry = Assert.Single(server.Logs.Entries, log => log.Message == "processing order");
        Assert.Equal("log-id", entry.Scope[CorrelationId.LogScopeKey]);
    }

    [Fact]
    public async Task OutgoingHttpCallCarriesRequestId()
    {
        // Act
        var (response, _) = await server.SendAsync(HttpMethod.Get, "/api/call-external", "outgoing-id");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("outgoing-id", Assert.Single(server.OutgoingRequests.ReceivedHeaders));
    }

    [Fact]
    public async Task OutgoingHttpCallWithoutIncomingHeaderCarriesCreatedId()
    {
        var (response, _) = await server.SendAsync(HttpMethod.Get, "/api/call-external");

        var created = Assert.Single(response.Headers.GetValues(CorrelationId.HeaderName));
        Assert.Equal(created, Assert.Single(server.OutgoingRequests.ReceivedHeaders));
    }

    [Fact]
    public async Task OutgoingHttpCallDoesNotOverwriteIdSetByCaller()
    {
        await server.SendAsync(HttpMethod.Get, "/api/call-external-with-own-id", "incoming-id");

        Assert.Equal("set-by-caller", Assert.Single(server.OutgoingRequests.ReceivedHeaders));
    }

    [Fact]
    public async Task HandlerOutsideCorrelatedFlowAddsNoHeader()
    {
        // Arrange
        var recorder = new OutgoingRequestRecorder();
        using var client = new HttpClient(new CorrelationIdHandler { InnerHandler = new FakeOutgoingHandler(recorder) });

        // Act
        await Task.Run(() => client.GetAsync("http://external.test/quote"));

        // Assert
        Assert.Null(Assert.Single(recorder.ReceivedHeaders));
    }

    [Fact]
    public async Task SetMarksTheFlowForQueueAndFix()
    {
        // Arrange
        var recorder = new OutgoingRequestRecorder();
        using var client = new HttpClient(new CorrelationIdHandler { InnerHandler = new FakeOutgoingHandler(recorder) });

        // Act
        await Task.Run(async () =>
        {
            CorrelationId.Set("sqs-message-id");
            Assert.Equal("sqs-message-id", CorrelationId.Current);
            await client.GetAsync("http://external.test/quote");
        });

        // Assert
        Assert.Equal("sqs-message-id", Assert.Single(recorder.ReceivedHeaders));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("A-b_c.1")]
    [InlineData("0f8fad5b-d9cb-469f-a165-70867728950e")]
    [InlineData("1234567890123456789012345678901234567890123456789012345678901234")]
    public void NormalizeKeepsValidId(string valid)
    {
        Assert.Equal(valid, CorrelationId.NormalizeOrCreate(valid));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("line\nbreak")]
    [InlineData("12345678901234567890123456789012345678901234567890123456789012345")]
    public void NormalizeCreatesGuidWhenInvalid(string? invalid)
    {
        var result = CorrelationId.NormalizeOrCreate(invalid);

        Assert.NotEqual(invalid, result);
        Assert.True(Guid.TryParseExact(result, "D", out _), $"id gerado não é GUID no formato D: {result}");
    }

    [Fact]
    public void CreatedIdsAreDifferent()
    {
        Assert.NotEqual(CorrelationId.NormalizeOrCreate(null), CorrelationId.NormalizeOrCreate(null));
    }
}
