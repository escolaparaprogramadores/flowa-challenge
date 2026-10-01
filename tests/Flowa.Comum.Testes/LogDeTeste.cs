using Microsoft.Extensions.Logging;

namespace Flowa.Comum.Testes;

public sealed record RegistroDeLog(LogLevel Nivel, string Mensagem, Exception? Excecao, IReadOnlyDictionary<string, object?> Escopo);

/// <summary>Provedor de log em memória que guarda, junto de cada mensagem, os valores dos escopos abertos.</summary>
public sealed class LogDeTeste : ILoggerProvider, ISupportExternalScope
{
    private readonly List<RegistroDeLog> registros = [];
    private IExternalScopeProvider escopos = new LoggerExternalScopeProvider();

    public IReadOnlyList<RegistroDeLog> Registros
    {
        get { lock (registros) return registros.ToList(); }
    }

    public ILogger CreateLogger(string categoria) => new Logger(this);

    public void SetScopeProvider(IExternalScopeProvider provedor) => escopos = provedor;

    public void Dispose() { }

    private void Registrar(RegistroDeLog registro)
    {
        lock (registros) registros.Add(registro);
    }

    private sealed class Logger(LogDeTeste dono) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => dono.escopos.Push(state);

        public bool IsEnabled(LogLevel nivel) => true;

        public void Log<TState>(LogLevel nivel, EventId evento, TState state, Exception? excecao, Func<TState, Exception?, string> formatar)
        {
            var escopo = new Dictionary<string, object?>();
            dono.escopos.ForEachScope((valor, destino) =>
            {
                if (valor is IEnumerable<KeyValuePair<string, object>> pares)
                    foreach (var par in pares)
                        destino[par.Key] = par.Value;
            }, escopo);

            dono.Registrar(new RegistroDeLog(nivel, formatar(state, excecao), excecao, escopo));
        }
    }
}
