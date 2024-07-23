
namespace PingPongTests {
    using Microsoft.Extensions.Logging;
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;

    public class MemoryLogger<T> : ILogger<T> {
        private readonly ConcurrentQueue<string> _logs = new ConcurrentQueue<string>();

        public IEnumerable<string> Logs => _logs;

        public IDisposable BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter) {
            if (formatter != null) {
                _logs.Enqueue(formatter(state, exception));
            }
        }
    }

    public class MemoryLoggerProvider : ILoggerProvider {
        private readonly ConcurrentDictionary<string, MemoryLogger<object>> _loggers = new ConcurrentDictionary<string, MemoryLogger<object>>();

        public ILogger CreateLogger(string categoryName) {
            return _loggers.GetOrAdd(categoryName, new MemoryLogger<object>());
        }

        public void Dispose() {
            _loggers.Clear();
        }

        public IEnumerable<string> GetLogs(string categoryName) {
            if (_loggers.TryGetValue(categoryName, out var logger)) {
                return logger.Logs;
            }

            return Array.Empty<string>();
        }
    }

}
