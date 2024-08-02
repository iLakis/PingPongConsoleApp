using Microsoft.Extensions.Logging;
using PingPongClient;
using System.Net.Security;
using System.Text;
using System.Xml.Serialization;
using Utils;

namespace Tests.TestClients
{
    public class TestTcpClient_NonStopListening : PingPongTcpClient
    {
        public TestTcpClient_NonStopListening(ILogger systemLogger, ILogger responseLogger)
            : base(systemLogger, responseLogger) { }
        protected override async Task CommunicateAsync(SslStream connection, CancellationToken token)
        {
            using StreamReader reader = new StreamReader(_currentConnection);
            using StreamWriter writer = new StreamWriter(_currentConnection) { AutoFlush = true };
            var pingSerializer = new XmlSerializer(typeof(ping));
            var pongSerializer = new XmlSerializer(typeof(pong));
            StringBuilder responseBuilder = new StringBuilder();

            var readingTask = ReadMessagesAsync(reader, pongSerializer, responseBuilder, token);
            var sendingTask = SendMessagesAsync(writer, token);

            await Task.WhenAll(readingTask, sendingTask);

            _systemLogger.LogWarning("Client stopped");
        }
        private async Task ReadMessagesAsync(StreamReader reader, XmlSerializer pongSerializer, StringBuilder responseBuilder, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try {

                    string line = await reader.ReadLineAsync();
                    if (!string.IsNullOrWhiteSpace(line)) {
                        responseBuilder.AppendLine(line);
                        if (line.EndsWith(_config.Separator)) {
                            string response = responseBuilder.ToString();
                            response = response.Replace(_config.Separator, "");
                            _responseLogger.LogInformation($"Received response: {response}", response);

                            try {
                                using (var stringReader = new StringReader(response)) {
                                    if (token.IsCancellationRequested) throw new TaskCanceledException();
                                    ReadPong(pongSerializer, stringReader);
                                }
                            } catch (InvalidOperationException ex) {
                                _systemLogger.LogError($"XML Deserialization error: {ex.Message}");
                                if (ex.InnerException != null) {
                                    _systemLogger.LogError($"Inner exception: {ex.InnerException.Message}");
                                }
                            }
                            responseBuilder.Clear();
                        }
                    } else {
                        _systemLogger.LogError("Received empty response or whitespace.");
                        await HandleConnectionErrorAsync(token);
                    }

                } catch (IOException ex) {
                    _systemLogger.LogError($"IO error: {ex.Message}");
                } catch (ObjectDisposedException ex) {
                    _systemLogger.LogError($"Object disposed error: {ex.Message}");                  
                } finally {
                    await HandleConnectionErrorAsync(token);
                }
            }
        }
        private async Task SendMessagesAsync(StreamWriter writer, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    SendPing(writer);
                    await Task.Delay(_config.Interval, token);
                }
            }
            catch (TaskCanceledException ex)
            {
                //_systemLogger.LogError($"Task cancelled: {ex.Message}");
                _systemLogger.LogWarning($"Task cancelled: {ex.Message}");
            }
        }

        private async Task HandleConnectionErrorAsync(CancellationToken token) {
            _systemLogger.LogWarning("Handling connection error, attempting to swap connection...");
            DisconnectCurrentConnection();
            await SwapConnectionAsync(token);
        }
    }
}
