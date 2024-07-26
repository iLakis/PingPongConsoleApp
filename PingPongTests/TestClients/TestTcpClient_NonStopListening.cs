using Microsoft.Extensions.Logging;
using PingPongClient;
using System.Net.Security;
using System.Text;
using System.Xml.Serialization;
using Utils;

namespace Tests.TestClients
{
    public class TestTcpClient_NonStopListening : TcpClient
    {
        public TestTcpClient_NonStopListening(ILogger systemLogger, ILogger responseLogger)
            : base(systemLogger, responseLogger) { }
        protected override async Task CommunicateAsync(CancellationToken token)
        {
            using StreamReader reader = new StreamReader(_sslStream);
            using StreamWriter writer = new StreamWriter(_sslStream) { AutoFlush = true };
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
                string line = await reader.ReadLineAsync();
                if (!string.IsNullOrWhiteSpace(line))
                {
                    responseBuilder.AppendLine(line);
                    if (line.EndsWith(Separator))
                    {
                        string response = responseBuilder.ToString();
                        response = response.Replace(Separator, "");
                        _responseLogger.LogInformation($"Received response: {response}", response);

                        try
                        {
                            using (var stringReader = new StringReader(response))
                            {
                                if (token.IsCancellationRequested) throw new TaskCanceledException();
                                ReadPong(pongSerializer, stringReader);
                            }
                        }
                        catch (InvalidOperationException ex)
                        {
                            _systemLogger.LogError($"XML Deserialization error: {ex.Message}");
                            if (ex.InnerException != null)
                            {
                                _systemLogger.LogError($"Inner exception: {ex.InnerException.Message}");
                            }
                        }
                        responseBuilder.Clear();
                    }
                }
                else
                {
                    _systemLogger.LogError("Received empty response or whitespace.");
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
                    await Task.Delay(Interval, token);
                }
            }
            catch (TaskCanceledException ex)
            {
                //_systemLogger.LogError($"Task cancelled: {ex.Message}");
                _systemLogger.LogWarning($"Task cancelled: {ex.Message}");
            }
        }
    }
}
