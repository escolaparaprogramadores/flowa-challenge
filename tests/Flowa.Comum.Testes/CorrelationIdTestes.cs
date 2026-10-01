using System.Net;

namespace Flowa.Comum.Testes;

/// <summary>Contrato D-34 (CA-29): X-Correlation-Id da entrada à resposta, ao log e à chamada de saída.</summary>
public sealed class CorrelationIdTestes : IAsyncLifetime
{
    private HostDeTeste host = null!;

    public async Task InitializeAsync() => host = await HostDeTeste.IniciarAsync();

    public async Task DisposeAsync() => await host.DisposeAsync();

    [Fact]
    public void NomeDoHeaderEhXCorrelationId()
    {
        Assert.Equal("X-Correlation-Id", CorrelationId.NomeDoHeader);
    }

    [Fact]
    public async Task IdRecebidoVoltaNoHeaderENoMeta()
    {
        // Act
        var (resposta, corpo) = await host.EnviarAsync(HttpMethod.Get, "/api/objeto", "pedido-123_abc.XYZ");

        // Assert
        Assert.Equal("pedido-123_abc.XYZ", Assert.Single(resposta.Headers.GetValues(CorrelationId.NomeDoHeader)));
        Assert.Equal("pedido-123_abc.XYZ", corpo.GetProperty("meta").GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task SemHeaderGeraGuidNovoIgualNoHeaderENoMeta()
    {
        // Act
        var (resposta, corpo) = await host.EnviarAsync(HttpMethod.Get, "/api/objeto");

        // Assert
        var noHeader = Assert.Single(resposta.Headers.GetValues(CorrelationId.NomeDoHeader));
        Assert.True(Guid.TryParse(noHeader, out _), $"id gerado não é GUID: {noHeader}");
        Assert.Equal(noHeader, corpo.GetProperty("meta").GetProperty("correlationId").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("com espaco")]
    [InlineData("<script>")]
    [InlineData("a;b")]
    [InlineData("12345678901234567890123456789012345678901234567890123456789012345")]
    public async Task IdInvalidoEhDescartadoEGeradoOutro(string invalido)
    {
        // Act
        var (resposta, corpo) = await host.EnviarAsync(HttpMethod.Get, "/api/objeto", invalido);

        // Assert
        var noHeader = Assert.Single(resposta.Headers.GetValues(CorrelationId.NomeDoHeader));
        Assert.NotEqual(invalido, noHeader);
        Assert.True(Guid.TryParse(noHeader, out _), $"id gerado não é GUID: {noHeader}");
        Assert.Equal(noHeader, corpo.GetProperty("meta").GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task IdVoltaNoHeaderTambemEmErro500()
    {
        var (resposta, corpo) = await host.EnviarAsync(HttpMethod.Get, "/api/erro", "id-do-500");

        Assert.Equal(HttpStatusCode.InternalServerError, resposta.StatusCode);
        Assert.Equal("id-do-500", Assert.Single(resposta.Headers.GetValues(CorrelationId.NomeDoHeader)));
        Assert.Equal("id-do-500", corpo.GetProperty("meta").GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task IdVoltaNoHeaderTambemEmRotaInexistente()
    {
        var (resposta, _) = await host.EnviarAsync(HttpMethod.Get, "/api/nao-existe", "id-do-404");

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
        Assert.Equal("id-do-404", Assert.Single(resposta.Headers.GetValues(CorrelationId.NomeDoHeader)));
    }

    [Fact]
    public async Task LogDaRequisicaoCarregaOIdNoEscopo()
    {
        // Act
        await host.EnviarAsync(HttpMethod.Get, "/api/loga", "id-do-log");

        // Assert
        var registro = Assert.Single(host.Logs.Registros, log => log.Mensagem == "processando pedido");
        Assert.Equal("id-do-log", registro.Escopo[CorrelationId.ChaveDoLog]);
    }

    [Fact]
    public async Task ChamadaHttpDeSaidaLevaOIdDaRequisicao()
    {
        // Act
        var (resposta, _) = await host.EnviarAsync(HttpMethod.Get, "/api/chama-externo", "id-de-saida");

        // Assert
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal("id-de-saida", Assert.Single(host.Saida.HeadersRecebidos));
    }

    [Fact]
    public async Task ChamadaHttpDeSaidaGeradaSemHeaderLevaOIdGerado()
    {
        var (resposta, _) = await host.EnviarAsync(HttpMethod.Get, "/api/chama-externo");

        var gerado = Assert.Single(resposta.Headers.GetValues(CorrelationId.NomeDoHeader));
        Assert.Equal(gerado, Assert.Single(host.Saida.HeadersRecebidos));
    }

    [Fact]
    public async Task ChamadaHttpDeSaidaNaoSobrescreveIdDefinidoPeloChamador()
    {
        await host.EnviarAsync(HttpMethod.Get, "/api/chama-externo-com-id-proprio", "id-da-entrada");

        Assert.Equal("definido-pelo-chamador", Assert.Single(host.Saida.HeadersRecebidos));
    }

    [Fact]
    public async Task HandlerForaDeFluxoCorrelacionadoNaoPoeHeader()
    {
        // Arrange
        var registro = new RegistroDeSaida();
        using var cliente = new HttpClient(new HandlerDeCorrelationId { InnerHandler = new HandlerFalsoDeSaida(registro) });

        // Act
        await Task.Run(() => cliente.GetAsync("http://externo.teste/cotacao"));

        // Assert
        Assert.Null(Assert.Single(registro.HeadersRecebidos));
    }

    [Fact]
    public async Task DefinirMarcaOFluxoParaFilaEFix()
    {
        // Arrange
        var registro = new RegistroDeSaida();
        using var cliente = new HttpClient(new HandlerDeCorrelationId { InnerHandler = new HandlerFalsoDeSaida(registro) });

        // Act
        await Task.Run(async () =>
        {
            CorrelationId.Definir("id-da-mensagem-sqs");
            Assert.Equal("id-da-mensagem-sqs", CorrelationId.Atual);
            await cliente.GetAsync("http://externo.teste/cotacao");
        });

        // Assert
        Assert.Equal("id-da-mensagem-sqs", Assert.Single(registro.HeadersRecebidos));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("A-b_c.1")]
    [InlineData("0f8fad5b-d9cb-469f-a165-70867728950e")]
    [InlineData("1234567890123456789012345678901234567890123456789012345678901234")]
    public void NormalizarMantemIdValido(string valido)
    {
        Assert.Equal(valido, CorrelationId.NormalizarOuGerar(valido));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("quebra\nlinha")]
    [InlineData("12345678901234567890123456789012345678901234567890123456789012345")]
    public void NormalizarGeraGuidQuandoInvalido(string? invalido)
    {
        var resultado = CorrelationId.NormalizarOuGerar(invalido);

        Assert.NotEqual(invalido, resultado);
        Assert.True(Guid.TryParse(resultado, out _), $"id gerado não é GUID: {resultado}");
    }

    [Fact]
    public void IdsGeradosSaoDiferentes()
    {
        Assert.NotEqual(CorrelationId.NormalizarOuGerar(null), CorrelationId.NormalizarOuGerar(null));
    }

    [Fact]
    public void ChaveDoEscopoDeLogEhCorrelationId()
    {
        Assert.Equal("CorrelationId", CorrelationId.ChaveDoLog);
    }
}
