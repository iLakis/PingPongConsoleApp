using Microsoft.Extensions.Logging;
using PingPongClient;
using System.Text;
using System.Xml.Serialization;
using Utils;

namespace Tests.TestClients
{
    public class TestTcpClient_Slow : TcpClient
    {
        public TestTcpClient_Slow(ILogger systemLogger, ILogger responseLogger)
            : base(systemLogger, responseLogger) { }

        protected override async Task CommunicateAsync(CancellationToken token)
        {
            using StreamReader reader = new StreamReader(_sslStream);
            using StreamWriter writer = new StreamWriter(_sslStream) { AutoFlush = true };
            var pingSerializer = new XmlSerializer(typeof(ping));
            var pongSerializer = new XmlSerializer(typeof(pong));
            StringBuilder responseBuilder = new StringBuilder();

            while (_client.Connected && !token.IsCancellationRequested)
            {
                try {
                    token.ThrowIfCancellationRequested();
                    // Simulate slow connection by adding delay
                    await Task.Delay(2000, token); // Delay
                    SendPing(writer);

                    while (_client.Connected && !token.IsCancellationRequested) {
                        await Task.Delay(2000, token); // Delay
                        token.ThrowIfCancellationRequested();
                        string line = await reader.ReadLineAsync();
                        if (!string.IsNullOrWhiteSpace(line)) {
                            responseBuilder.AppendLine(line);
                            if (line.EndsWith(Separator)) {
                                string response = responseBuilder.ToString();
                                response = response.Replace(Separator, "");
                                _responseLogger.LogInformation($"Received response: {response}", response);

                                try {
                                    using (var stringReader = new StringReader(response)) {
                                        token.ThrowIfCancellationRequested();
                                        await Task.Delay(2000, token); // Delay
                                        ReadPong(pongSerializer, stringReader);
                                    }
                                } catch (InvalidOperationException ex) {
                                    _systemLogger.LogError($"XML Deserialization error: {ex.Message}");
                                    if (ex.InnerException != null) {
                                        _systemLogger.LogError($"Inner exception: {ex.InnerException.Message}");
                                    }
                                }
                                responseBuilder.Clear();
                                break;
                            }
                        } else {
                            _systemLogger.LogError("Received empty response or whitespace.");
                        }
                    }
                    await Task.Delay(Interval, token);
                } catch (IOException ioEx) {
                    _systemLogger.LogError($"IO error: {ioEx.Message}");
                } catch (OperationCanceledException ex) {
                    _systemLogger.LogError($"{ex.Message}");
                } catch (Exception ex) {
                    _systemLogger.LogError($"Unexpected error: {ex.Message}");
                } finally {
                    Disconnect();
                }
            }
            _systemLogger.LogWarning("Client stopped");
        }
    }
}
