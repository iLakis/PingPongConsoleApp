using Microsoft.Extensions.Logging;
using PingPongServer;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Xml.Serialization;
using Utils;

public class TestTcpServer_Slow : PingPongTcpServer {
    public TestTcpServer_Slow(ILogger<PingPongTcpServer> logger) : base(logger) { }

    protected override async Task HandleClientAsync(TcpClient client, CancellationToken token) {
        var sslStream = new SslStream(client.GetStream(), false);
        sslStream.ReadTimeout = _config.ReadTimeout;
        sslStream.WriteTimeout = _config.WriteTimeout;
        try {
            await AuthenticateSsl(sslStream);

            using (StreamReader reader = new StreamReader(sslStream))
            using (StreamWriter writer = new StreamWriter(sslStream) { AutoFlush = true }) {
                var pingSerializer = new XmlSerializer(typeof(ping));
                var pongSerializer = new XmlSerializer(typeof(pong));
                StringBuilder messageBuilder = new StringBuilder();

                while (client.Connected && !token.IsCancellationRequested) {
                    token.ThrowIfCancellationRequested();

                    // Симуляция задержки чтения
                    await Task.Delay(2000, token);

                    var line = await ReadWithTimeout(reader, token);
                    if (!string.IsNullOrWhiteSpace(line)) {
                        messageBuilder.AppendLine(line);
                        if (line.EndsWith(_config.Separator)) {
                            string message = messageBuilder.ToString();
                            message = message.Replace(_config.Separator, "");
                            _logger.LogInformation($"Received message: {message}", message);
                            try {
                                using (var stringReader = new StringReader(message)) {
                                    token.ThrowIfCancellationRequested();
                                    ReadPing(stringReader, pingSerializer);

                                    // Симуляция задержки отправки ответа
                                    await Task.Delay(6000, token);

                                    await SendPongWithTimeout(writer, token);
                                }
                            } catch (InvalidOperationException ex) {
                                _logger.LogError($"[{DateTime.UtcNow:HH:mm:ss.fff}]: XML Deserialization error: {ex.Message}");
                                if (ex.InnerException != null) {
                                    _logger.LogError($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Inner exception: {ex.InnerException.Message}");
                                }
                            } 
                            messageBuilder.Clear();
                        }
                    } else {
                        _logger.LogWarning($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Received empty message or whitespace.");
                        break;
                    }
                }
                _logger.LogWarning($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Client disconnected.");
            }
        } catch (AuthenticationException ex) {
            _logger.LogError($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Authentication failed: {ex.Message}");
            if (ex.InnerException != null) {
                _logger.LogError($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Inner exception: {ex.InnerException.Message}");
            }
        } catch (IOException ioEx) {
            if (!token.IsCancellationRequested) {
                _logger.LogError($"[{DateTime.UtcNow:HH:mm:ss.fff}]: IO error: {ioEx.Message}");
                if (ioEx.InnerException != null) {
                    _logger.LogError($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Inner exception: {ioEx.InnerException.Message}");
                }
            }
        } catch (TaskCanceledException ex) {
            _logger.LogWarning($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Task cancelled");
        } catch (TimeoutException ex) {
            _logger.LogWarning($"[{DateTime.UtcNow:HH:mm:ss.fff}]: {ex.Message}");
        } catch (Exception ex) {
            _logger.LogInformation($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Error: {ex.Message}");
            if (ex.InnerException != null) {
                _logger.LogError($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Inner exception: {ex.InnerException.Message}");
            }
        } finally {
            sslStream.Close();
            client.Close(); // Ensure the client is closed on error
            _logger.LogWarning($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Client connection closed.");
        }
    }

    private async Task<string> ReadWithTimeout(StreamReader reader, CancellationToken token) {
        using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token)) {
            timeoutCts.CancelAfter(_config.ReadTimeout);
            try {
                return await reader.ReadLineAsync().WaitAsync(timeoutCts.Token);
            } catch (OperationCanceledException) {
                //_logger.LogError($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Reading client message timed out.");
                throw new TimeoutException("Reading client message timed out.");
            }
        }
    }

    private async Task SendPongWithTimeout(StreamWriter writer, CancellationToken token) {
        using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token)) {
            timeoutCts.CancelAfter(_config.WriteTimeout);
            var pongVar = new pong { timestamp = DateTime.UtcNow };
            var pongMessage = XmlTools.SerializeToXml(pongVar) + _config.Separator;

            try {
                await writer.WriteLineAsync(pongMessage).WaitAsync(timeoutCts.Token);
                _logger.LogInformation($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Sent: {pongVar.timestamp}");
            } catch (OperationCanceledException) {
                //_logger.LogError($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Sending pong message timed out.");
                throw new TimeoutException("Reading client message timed out.");
            }
        }
    }
}