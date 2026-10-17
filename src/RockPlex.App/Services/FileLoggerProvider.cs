using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using RockPlex.Plex.Http;

namespace RockPlex.App.Services;

/// <summary>
/// Minimal daily rolling log in <c>logs\rockplex-yyyyMMdd.log</c>, keeping the last
/// <see cref="RetainedFiles"/> files. Every line passes through <see cref="PlexRedactor"/>.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    public const int RetainedFiles = 7;

    private readonly string _directory;
    private readonly BlockingCollection<string> _queue = new(boundedCapacity: 10_000);
    private readonly Thread _writer;

    public FileLoggerProvider(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        PruneOldFiles();
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "RockPlex log writer" };
        _writer.Start();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(2));
        _queue.Dispose();
    }

    private void Enqueue(string line)
    {
        if (!_queue.IsAddingCompleted)
        {
            _queue.TryAdd(line);
        }
    }

    private void WriteLoop()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try
            {
                var path = Path.Combine(_directory, $"rockplex-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(path, line, Encoding.UTF8);
            }
            catch (IOException)
            {
                // Logging must never take the app down.
            }
        }
    }

    private void PruneOldFiles()
    {
        try
        {
            foreach (var old in Directory.GetFiles(_directory, "rockplex-*.log").Order(StringComparer.Ordinal).SkipLast(RetainedFiles))
            {
                File.Delete(old);
            }
        }
        catch (IOException)
        {
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var builder = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
                .Append(" [").Append(logLevel.ToString()[..4].ToUpperInvariant()).Append("] ")
                .Append(category).Append(": ")
                .Append(formatter(state, exception));
            if (exception is not null)
            {
                builder.AppendLine().Append(exception);
            }

            provider.Enqueue(PlexRedactor.Redact(builder.ToString()) + Environment.NewLine);
        }
    }
}
