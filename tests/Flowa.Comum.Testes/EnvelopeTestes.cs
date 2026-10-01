using System.Net;
using System.Text;
using System.Text.Json;

namespace Flowa.Comum.Testes;

/// <summary>Contrato D-33 (CA-28): o mesmo envelope em sucesso e em erro.</summary>
public sealed class EnvelopeTestes : IAsyncLifetime
{
    private HostDeTeste host = null!;

    public async Task InitializeAsync() => host = await HostDeTeste.IniciarAsync();

    public async Task DisposeAsync() => await host.DisposeAsync();

    [Fact]
    public async Task SucessoTemExatamenteDataErrorMetaComCamposDoContrato()
    {
        // Act
        var (resposta, corpo) = await host.EnviarAsync(HttpMethod.Get, "/api/objeto", "id-sucesso");

        // Assert
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal("application/json", resposta.Content.Headers.ContentType?.MediaType);
        Assert.Equal(["data", "error", "meta"], NomesDasPropriedades(corpo));
        Assert.Equal(JsonValueKind.Null, corpo.GetProperty("error").ValueKind);
        Assert.Equal("flowa", corpo.GetProperty("data").GetProperty("nome").GetString());
        Assert.Equal(3, corpo.GetProperty("data").GetProperty("quantidade").GetInt32());
        Assert.Equal(["correlationId", "timestamp"], NomesDasPropriedades(corpo.GetProperty("meta")));
        Assert.Equal("id-sucesso", corpo.GetProperty("meta").GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task TimestampEhIso8601EmUtcDoMomentoDaResposta()
    {
        // Arrange
        var antes = DateTimeOffset.UtcNow;

        // Act
        var (_, corpo) = await host.EnviarAsync(HttpMethod.Get, "/api/objeto");
        var depois = DateTimeOffset.UtcNow;

        // Assert
        var texto = corpo.GetProperty("meta").GetProperty("timestamp").GetString()!;
        var timestamp = DateTimeOffset.Parse(texto);
        Assert.EndsWith("+00:00", texto);
        Assert.InRange(timestamp, antes, depois);
    }

    [Fact]
    public async Task OkTipadoVaiNoDataComStatus200()
    {
        var (resposta, corpo) = await host.EnviarAsync(HttpMethod.Get, "/api/ok");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal("PETR4", corpo.GetProperty("data").GetProperty("codigo").GetString());
        Assert.Equal(10, corpo.GetProperty("data").GetProperty("quantidade").GetInt32());
        Assert.Equal(JsonValueKind.Null, corpo.GetProperty("error").ValueKind);
    }

    [Fact]
    public async Task CreatedMantem201ELocation()
    {
        var (resposta, corpo) = await host.EnviarAsync(HttpMethod.Post, "/api/criado");

        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        Assert.Equal("/api/itens/7", resposta.Headers.Location?.OriginalString);
        Assert.Equal("VALE3", corpo.GetProperty("data").GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task NoContentViraOkComDataNull()
    {
        var (resposta, corpo) = await host.EnviarAsync(HttpMethod.Delete, "/api/sem-conteudo");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal(JsonValueKind.Null, corpo.GetProperty("data").ValueKind);
        Assert.Equal(JsonValueKind.Null, corpo.GetProperty("error").ValueKind);
    }

    [Fact]
    public async Task RotaSemRetornoDevolveEnvelopeComDataNull()
    {
        var (resposta, corpo) = await host.EnviarAsync(HttpMethod.Get, "/api/void");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal(JsonValueKind.Null, corpo.GetProperty("data").ValueKind);
        Assert.Equal(["data", "error", "meta"], NomesDasPropriedades(corpo));
    }

    [Fact]
    public async Task EnvelopeDevolvidoPelaRotaNaoEhEmbrulhadoDeNovo()
    {
        var (_, corpo) = await host.EnviarAsync(HttpMethod.Get, "/api/envelope-pronto");

        Assert.Equal("VIIA4", corpo.GetProperty("data").GetProperty("codigo").GetString());
        Assert.False(corpo.GetProperty("data").TryGetProperty("data", out _));
    }

    [Fact]
    public async Task RotaDeSaudeTambemDevolveEnvelope()
    {
        var (resposta, corpo) = await host.EnviarAsync(HttpMethod.Get, "/api/teste/health");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal("ok", corpo.GetProperty("data").GetProperty("status").GetString());
        Assert.Equal(["data", "error", "meta"], NomesDasPropriedades(corpo));
    }

    [Fact]
    public async Task TextoPuroSegueSemEnvelope()
    {
        var (resposta, _) = await EnviarTextoAsync("/api/texto");

        Assert.Equal("text/plain", resposta.Content.Headers.ContentType?.MediaType);
        Assert.Equal("ola", await resposta.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ErroDeValidacaoDevolve400ComUmDetalhePorMensagemDeCampo()
    {
        // Act
        var (resposta, corpo) = await host.EnviarAsync(HttpMethod.Post, "/api/validacao", "id-validacao");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.Equal(["data", "error", "meta"], NomesDasPropriedades(corpo));
        Assert.Equal(JsonValueKind.Null, corpo.GetProperty("data").ValueKind);
        var erro = corpo.GetProperty("error");
        Assert.Equal(["code", "message", "details"], NomesDasPropriedades(erro));
        Assert.Equal("VALIDATION_ERROR", erro.GetProperty("code").GetString());
        Assert.Equal("Um ou mais campos são inválidos.", erro.GetProperty("message").GetString());
        var detalhes = erro.GetProperty("details").EnumerateArray()
            .Select(detalhe => (detalhe.GetProperty("field").GetString(), detalhe.GetProperty("message").GetString()))
            .ToList();
        Assert.Equal(
            [("quantidade", "Deve ser maior que zero."), ("quantidade", "Deve ser menor que 100000."), ("codigo", "Obrigatório.")],
            detalhes);
        Assert.Equal("id-validacao", corpo.GetProperty("meta").GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task CorpoIlegivelDevolve400BadRequestNoEnvelope()
    {
        var conteudo = new StringContent("{ isto nao e json", Encoding.UTF8, "application/json");

        var (resposta, corpo) = await host.EnviarAsync(HttpMethod.Post, "/api/corpo", conteudo: conteudo);

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.Equal("BAD_REQUEST", corpo.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Requisição inválida.", corpo.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal(JsonValueKind.Null, corpo.GetProperty("data").ValueKind);
    }

    [Fact]
    public async Task ExcecaoNaoTratadaDevolve500GenericoSemStackTrace()
    {
        // Act
        var (resposta, corpo) = await host.EnviarAsync(HttpMethod.Get, "/api/erro", "id-erro");
        var texto = corpo.GetRawText();

        // Assert
        Assert.Equal(HttpStatusCode.InternalServerError, resposta.StatusCode);
        Assert.Equal(["data", "error", "meta"], NomesDasPropriedades(corpo));
        Assert.Equal(JsonValueKind.Null, corpo.GetProperty("data").ValueKind);
        Assert.Equal("INTERNAL_ERROR", corpo.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(
            "Ocorreu um erro inesperado. Informe o correlationId ao suporte.",
            corpo.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal(0, corpo.GetProperty("error").GetProperty("details").GetArrayLength());
        Assert.Equal("id-erro", corpo.GetProperty("meta").GetProperty("correlationId").GetString());
        Assert.DoesNotContain("detalhe-interno-do-servidor", texto);
        Assert.DoesNotContain("InvalidOperationException", texto);
        Assert.DoesNotContain(" at ", texto);
        Assert.DoesNotContain(".cs:line", texto);
    }

    [Fact]
    public async Task ExcecaoNaoTratadaEhLogadaComoErroComAExcecao()
    {
        await host.EnviarAsync(HttpMethod.Get, "/api/erro", "id-log-erro");

        var registro = Assert.Single(host.Logs.Registros, log => log.Nivel == Microsoft.Extensions.Logging.LogLevel.Error);
        Assert.Equal("Erro inesperado", registro.Mensagem);
        Assert.IsType<InvalidOperationException>(registro.Excecao);
        Assert.Equal("id-log-erro", registro.Escopo[CorrelationId.ChaveDoLog]);
    }

    [Fact]
    public async Task NotFoundDaRotaViraEnvelopeNotFound()
    {
        var (resposta, corpo) = await host.EnviarAsync(HttpMethod.Get, "/api/nao-encontrado");

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
        Assert.Equal("NOT_FOUND", corpo.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Recurso não encontrado.", corpo.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task RotaInexistenteViraEnvelopeNotFound()
    {
        var (resposta, corpo) = await host.EnviarAsync(HttpMethod.Get, "/api/nao-existe", "id-inexistente");

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
        Assert.Equal(["data", "error", "meta"], NomesDasPropriedades(corpo));
        Assert.Equal("NOT_FOUND", corpo.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("id-inexistente", corpo.GetProperty("meta").GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task ConflitoComTextoUsaOTextoComoMensagem()
    {
        var (resposta, corpo) = await host.EnviarAsync(HttpMethod.Get, "/api/conflito");

        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        Assert.Equal("CONFLICT", corpo.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Estoque insuficiente.", corpo.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task ProblemaDeNegocioUsaStatusEDetalhe()
    {
        var (resposta, corpo) = await host.EnviarAsync(HttpMethod.Get, "/api/problema");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resposta.StatusCode);
        Assert.Equal("UNPROCESSABLE_ENTITY", corpo.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Oferta encerrada.", corpo.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task Problema500NaoVazaODetalheInterno()
    {
        var (resposta, corpo) = await host.EnviarAsync(HttpMethod.Get, "/api/problema-500");

        Assert.Equal(HttpStatusCode.InternalServerError, resposta.StatusCode);
        Assert.Equal("INTERNAL_ERROR", corpo.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(
            "Ocorreu um erro inesperado. Informe o correlationId ao suporte.",
            corpo.GetProperty("error").GetProperty("message").GetString());
        Assert.DoesNotContain("detalhe-interno-do-servidor", corpo.GetRawText());
    }

    [Fact]
    public void FabricaDeErroMontaCodigoMensagemEDetalhes()
    {
        // Act
        var envelope = Envelope.Erro("CONFLICT", "Estoque insuficiente.", [new DetalheDoErro("quantidade", "Acima do disponível.")]);

        // Assert
        Assert.Null(envelope.Data);
        Assert.Equal("CONFLICT", envelope.Error!.Code);
        Assert.Equal("Estoque insuficiente.", envelope.Error.Message);
        Assert.Equal(new DetalheDoErro("quantidade", "Acima do disponível."), Assert.Single(envelope.Error.Details));
    }

    [Theory]
    [InlineData(400, "BAD_REQUEST")]
    [InlineData(401, "UNAUTHORIZED")]
    [InlineData(403, "FORBIDDEN")]
    [InlineData(404, "NOT_FOUND")]
    [InlineData(405, "METHOD_NOT_ALLOWED")]
    [InlineData(409, "CONFLICT")]
    [InlineData(422, "UNPROCESSABLE_ENTITY")]
    [InlineData(429, "TOO_MANY_REQUESTS")]
    [InlineData(418, "REQUEST_ERROR")]
    [InlineData(500, "INTERNAL_ERROR")]
    [InlineData(502, "INTERNAL_ERROR")]
    [InlineData(503, "SERVICE_UNAVAILABLE")]
    public void CodigoDeErroPorStatus(int status, string codigoEsperado)
    {
        Assert.Equal(codigoEsperado, CodigosDeErro.PorStatus(status));
    }

    private async Task<(HttpResponseMessage, string)> EnviarTextoAsync(string caminho)
    {
        var resposta = await host.Cliente.GetAsync(caminho);
        return (resposta, await resposta.Content.ReadAsStringAsync());
    }

    private static string[] NomesDasPropriedades(JsonElement objeto) =>
        objeto.EnumerateObject().Select(propriedade => propriedade.Name).ToArray();
}
