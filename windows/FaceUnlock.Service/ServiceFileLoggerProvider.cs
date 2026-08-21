using System.Text;
using Microsoft.Extensions.Logging;

namespace FaceUnlock.Service;

internal sealed class ServiceFileLoggerProvider : ILoggerProvider
{
    private const long MaxLogSizeBytes = 5 * 1024 * 1024;
    private static readonly object Sync = new();
    private readonly string _logFile;
    private readonly string _backupFile;

    public ServiceFileLoggerProvider()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FaceUnlock", "logs");
        Directory.CreateDirectory(dir);
        _logFile = Path.Combine(dir, "service.log");
        _backupFile = Path.Combine(dir, "service.log.1");
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, Write);
    public void Dispose() { }

    private void Write(string category, LogLevel level, string message, Exception? exception)
    {
        if (!category.StartsWith("FaceUnlock.Service", StringComparison.Ordinal) || level < LogLevel.Information)
            return;

        try
        {
            lock (Sync)
            {
                if (File.Exists(_logFile) && new FileInfo(_logFile).Length >= MaxLogSizeBytes)
                {
                    try
                    {
                        if (File.Exists(_backupFile)) File.Delete(_backupFile);
                        File.Move(_logFile, _backupFile);
                    }
                    catch { }
                }

                var text = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fffZ}] [{level.ToString().ToUpperInvariant()}] [{category}] {message}";
                if (exception != null) text += $" | {exception.GetType().Name}: {exception.Message}";
                File.AppendAllText(_logFile, text + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch { }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly string _category;
        private readonly Action<string, LogLevel, string, Exception?> _write;
        public FileLogger(string category, Action<string, LogLevel, string, Exception?> write) { _category = category; _write = write; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) _write(_category, logLevel, formatter(state, exception), exception);
        }
    }
}
