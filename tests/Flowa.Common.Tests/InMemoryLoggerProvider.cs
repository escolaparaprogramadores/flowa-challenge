using Microsoft.Extensions.Logging;

namespace Flowa.Common.Tests;

public sealed record LogEntry(LogLevel Level, string Message, Exception? Exception, IReadOnlyDictionary<string, object?> Scope);

/// <summary>Provedor de log em memória que guarda, junto de cada mensagem, os valores dos escopos abertos.</summary>
public sealed class InMemoryLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly List<LogEntry> entries = [];
    private IExternalScopeProvider scopes = new LoggerExternalScopeProvider();

    public IReadOnlyList<LogEntry> Entries
    {
        get { lock (entries) return entries.ToList(); }
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => scopes = scopeProvider;

    public void Dispose() { }

    private void Add(LogEntry entry)
    {
        lock (entries) entries.Add(entry);
    }

    private sealed class Logger(InMemoryLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner.scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var scope = new Dictionary<string, object?>();
            owner.scopes.ForEachScope((value, target) =>
            {
                if (value is IEnumerable<KeyValuePair<string, object>> pairs)
                    foreach (var pair in pairs)
                        target[pair.Key] = pair.Value;
            }, scope);

            owner.Add(new LogEntry(logLevel, formatter(state, exception), exception, scope));
        }
    }
}
