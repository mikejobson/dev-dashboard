using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace DevDash.Agent.Logging;

/// <summary>
/// A small rolling file logger for when the agent runs as a service and has no terminal. One file per
/// day (<c>agent-20261118.log</c>), a new numbered file if a day's log passes the size cap, and old
/// files deleted beyond a fixed count. Written here rather than pulled in as a package because this is
/// all the agent needs, and it keeps the single-file binary free of extra dependencies.
/// </summary>
[ProviderAlias("File")]
public sealed class FileLoggerProvider : ILoggerProvider
{
    public const string Prefix = "agent-";

    private readonly string _directory;
    private readonly TimeProvider _time;
    private readonly long _maxBytes;
    private readonly int _retainedFiles;
    private readonly Lock _lock = new();
    private StreamWriter? _writer;
    private string? _day;
    private int _part;
    private bool _failed;

    public FileLoggerProvider(string directory, TimeProvider? time = null, long maxBytes = 10 * 1024 * 1024, int retainedFiles = 14)
    {
        _directory = directory;
        _time = time ?? TimeProvider.System;
        _maxBytes = maxBytes;
        _retainedFiles = retainedFiles;
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private void Write(string category, LogLevel level, string message, Exception? exception)
    {
        var now = _time.GetLocalNow();
        var line = new StringBuilder()
            .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
            .Append(" [").Append(Abbreviate(level)).Append("] ")
            .Append(category).Append(": ").Append(message);
        if (exception is not null)
        {
            line.AppendLine().Append(exception);
        }

        lock (_lock)
        {
            try
            {
                var writer = WriterFor(now);
                writer.WriteLine(line);
                writer.Flush();
                _failed = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never take the agent down. Say so once on stderr, then keep trying quietly.
                if (!_failed)
                {
                    _failed = true;
                    Console.Error.WriteLine($"devdash-agent: cannot write to the log in {_directory}: {ex.Message}");
                }

                _writer?.Dispose();
                _writer = null;
            }
        }
    }

    private StreamWriter WriterFor(DateTimeOffset now)
    {
        var day = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        if (_writer is not null && day == _day && _writer.BaseStream.Length < _maxBytes)
        {
            return _writer;
        }

        _writer?.Dispose();
        _writer = null;
        Directory.CreateDirectory(_directory);
        if (day != _day)
        {
            _day = day;
            _part = 0;
        }

        // Skip parts that are already full, including ones left by an earlier run today.
        string path;
        while (true)
        {
            path = Path.Combine(_directory, _part == 0 ? $"{Prefix}{day}.log" : $"{Prefix}{day}.{_part}.log");
            if (!File.Exists(path) || new FileInfo(path).Length < _maxBytes)
            {
                break;
            }

            _part++;
        }

        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(stream, new UTF8Encoding(false));
        Prune();
        return _writer;
    }

    private void Prune()
    {
        var old = Directory.EnumerateFiles(_directory, Prefix + "*.log")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ThenByDescending(f => f, StringComparer.Ordinal)
            .Skip(_retainedFiles);
        foreach (var file in old)
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // someone has it open; try again at the next roll
            }
        }
    }

    private static string Abbreviate(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???",
    };

    /// <summary>The newest log file in <paramref name="directory"/>, or null when there is none.</summary>
    public static string? LatestFile(string directory) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, Prefix + "*.log").OrderByDescending(File.GetLastWriteTimeUtc).ThenByDescending(f => f, StringComparer.Ordinal).FirstOrDefault()
            : null;

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                provider.Write(category, logLevel, formatter(state, exception), exception);
            }
        }
    }
}
