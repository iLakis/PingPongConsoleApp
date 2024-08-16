using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Xml.Serialization;
using Microsoft.Extensions.Logging;
using PingPongServer;
using Utils;
using Utils.Configs.Server;
using Utils.Connection;

namespace Tests.TestServers {
    public class TestServer_SpammingMessages : PingPongTcpServer {
        private int _delayMs = 100;
        private int _count = 5;
        public TestServer_SpammingMessages(ILogger<PingPongTcpServer> logger) : base(logger) { }

        protected override async Task HandleClientAsync(ClientConnection connection, CancellationToken token) {
            var sslStream = new SslStream(connection.TcpClient.GetStream(), false);
            sslStream.ReadTimeout = _config.ReadTimeout;
            sslStream.WriteTimeout = _config.WriteTimeout;
            try {
                await AuthenticateSsl(sslStream);

                using (StreamReader reader = new StreamReader(sslStream))
                using (StreamWriter writer = new StreamWriter(sslStream) { AutoFlush = true }) {
                    var pingSerializer = new XmlSerializer(typeof(ping));
                    var pongSerializer = new XmlSerializer(typeof(pong));
                    StringBuilder messageBuilder = new StringBuilder();

                    while (connection.TcpClient.Connected && !token.IsCancellationRequested) {
                        token.ThrowIfCancellationRequested();
                        string line = await reader.ReadLineAsync();
                        if (!string.IsNullOrWhiteSpace(line)) {
                            messageBuilder.AppendLine(line);
                            if (line.EndsWith(_config.Separator)) {
                                string message = messageBuilder.ToString();
                                message = message.Replace(_config.Separator, "");
                                _logger.LogInformation($"Received message: {message}");
                                try {
                                    using (var stringReader = new StringReader(message)) {
                                        token.ThrowIfCancellationRequested();
                                        ReadPing(connection, stringReader, pingSerializer);
                                    }
                                } catch (InvalidOperationException ex) {
                                    _logger.LogError($"[{DateTime.UtcNow:HH:mm:ss.fff}] XML Deserialization error: {ex.Message}");
                                    if (ex.InnerException != null) {
                                        _logger.LogError($"[{DateTime.UtcNow:HH:mm:ss.fff}] Inner exception: {ex.InnerException.Message}");
                                    }
                                }
                                messageBuilder.Clear();
                                break; 
                            }
                        } else {
                            _logger.LogWarning($"[{DateTime.UtcNow:HH:mm:ss.fff}] Received empty message or whitespace.");
                            break;
                        }
                    }

                    while (connection.TcpClient.Connected && !token.IsCancellationRequested) {
                        token.ThrowIfCancellationRequested();

                        var pongVar = new pong { timestamp = DateTime.UtcNow };
                        var pongMessage = Utils.XmlTools.SerializeToXml(pongVar) + _config.Separator;
                        await writer.WriteLineAsync(pongMessage);
                        await writer.FlushAsync();
                        _logger.LogInformation($"Sent: {pongVar.timestamp}");

                        await Task.Delay(1, token);
                    }
                    _logger.LogWarning($"[{DateTime.UtcNow:HH:mm:ss.fff}] Client disconnected.");
                }
            } catch (OperationCanceledException ex) {
                _logger.LogWarning($"[{DateTime.UtcNow:HH:mm:ss.fff}] Task was canceled");
            } catch (Exception ex) {
                _logger.LogError($"[{DateTime.UtcNow:HH:mm:ss.fff}] Error: {ex.Message}");
                OnConnectionError(connection, ex);
            } finally {
                sslStream.Close();
                connection.TcpClient.Close();
                _logger.LogWarning($"[{DateTime.UtcNow:HH:mm:ss.fff}] Client connection closed.");
                OnConnectionClosed(connection);
            }
        }
    }
}
