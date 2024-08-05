using Microsoft.Extensions.Logging;
using PingPongClient;
using System.Diagnostics;
using System.Net.Security;
using System.Text;
using System.Xml.Serialization;
using Utils;

namespace Tests.TestClients {
    public class TestClient_NonStopListening : PingPongTcpClient {
        public TestClient_NonStopListening(ILogger systemLogger, ILogger responseLogger)
            : base(systemLogger, responseLogger) { }

        protected override async Task CommunicateAsync(SslStream connection, CancellationToken token) {
            using StreamReader reader = new StreamReader(connection);
            using StreamWriter writer = new StreamWriter(connection) { AutoFlush = true };
            var pongSerializer = new XmlSerializer(typeof(pong));
            StringBuilder responseBuilder = new StringBuilder();
            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();

            // Отправляем первое сообщение клиенту
            SendPing(writer);

            // Задача для отправки ping сообщений
            var pingTask = Task.Run(async () => {
                while (connection.CanRead && connection.CanWrite && !token.IsCancellationRequested) {
                    if (stopwatch.ElapsedMilliseconds >= _config.Interval) {
                        SendPing(writer);
                        stopwatch.Restart();
                    }
                    await Task.Delay(1, token); // Минимальная задержка
                }
            }, token);

            // Главный цикл для обработки входящих сообщений
            while (connection.CanRead && connection.CanWrite && !token.IsCancellationRequested) {
                token.ThrowIfCancellationRequested();

                try {
                    while (connection.CanRead && connection.CanWrite) {
                        string line = await reader.ReadLineAsync();
                        if (!string.IsNullOrWhiteSpace(line)) {
                            responseBuilder.AppendLine(line);
                            if (line.EndsWith(_config.Separator)) {
                                string response = responseBuilder.ToString();
                                response = response.Replace(_config.Separator, "");
                                _responseLogger.LogInformation($"Received response: {response}");

                                try {
                                    using (var stringReader = new StringReader(response)) {
                                        token.ThrowIfCancellationRequested();
                                        ReadPong(pongSerializer, stringReader);
                                    }
                                } catch (InvalidOperationException ex) {
                                    _systemLogger.LogError($"[{DateTime.UtcNow:HH:mm:ss.fff}] XML Deserialization error: {ex.Message}");
                                }
                                responseBuilder.Clear();
                            }
                        } else {
                            _systemLogger.LogError($"[{DateTime.UtcNow:HH:mm:ss.fff}] Received empty response or whitespace.");
                            DisconnectCurrentConnection();
                        }
                    }
                } catch (OperationCanceledException ex) {
                    _systemLogger.LogWarning($"[{DateTime.UtcNow:HH:mm:ss.fff}] Task was canceled");
                } catch (Exception ex) {
                    _systemLogger.LogError($"[{DateTime.UtcNow:HH:mm:ss.fff}] Error while reading from stream: {ex.Message}");
                }

                await Task.Delay(1, token); // Минимальная задержка для обработки
            }

            await pingTask; // Завершаем задачу отправки ping сообщений
        }
    }
}