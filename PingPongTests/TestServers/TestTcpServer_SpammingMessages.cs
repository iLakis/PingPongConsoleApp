﻿using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Xml.Serialization;
using Microsoft.Extensions.Logging;
using PingPongServer;
using Utils;

namespace Tests.TestServers
{
    public class TestServer_SpammingMessages : PingPongTcpServer
    {
        public TestServer_SpammingMessages(ILogger<PingPongTcpServer> logger) : base(logger) { }

        public async Task StartSendingPongs(int count, int delayMs, CancellationToken token)
        {
            TcpListener listener = new TcpListener(IPAddress.Any, _config.Port);
            listener.Start();
            _logger.LogInformation($"TestServer started on port {_config.Port}");

            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (listener.Pending())
                    {
                        TcpClient client = listener.AcceptTcpClient();
                        _logger.LogInformation("Client connected");
                        var sslStream = new SslStream(client.GetStream(), false);
                        await AuthenticateSsl(sslStream);

                        var readingTask = ReadMessagesAsync(sslStream, token);
                        var sendingTask = SendMessagesAsync(sslStream, count, delayMs, token);

                        await Task.WhenAll(readingTask, sendingTask);

                        client.Close();
                        _logger.LogWarning("Client connection closed.");
                    }
                    else
                    {
                        await Task.Delay(100, token);
                    }
                }

            }
            catch (TaskCanceledException)
            {
                _logger.LogWarning("Task was cancelled.");
            }
            finally
            {
                listener.Stop();
                _logger.LogWarning("TestServer stopped");
            }


        }

        private async Task ReadMessagesAsync(SslStream sslStream, CancellationToken token)
        {
            var reader = new StreamReader(sslStream);
            var pingSerializer = new XmlSerializer(typeof(ping));
            StringBuilder messageBuilder = new StringBuilder();

            try
            {
                while (!token.IsCancellationRequested)
                {
                    string line = await reader.ReadLineAsync();
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        messageBuilder.AppendLine(line);
                        if (line.EndsWith(_config.Separator))
                        {
                            string message = messageBuilder.ToString();
                            message = message.Replace(_config.Separator, "");
                            _logger.LogInformation($"Received message: {message}");

                            try
                            {
                                using (var stringReader = new StringReader(message))
                                {
                                    if (token.IsCancellationRequested) throw new TaskCanceledException();
                                    ReadPing(stringReader, pingSerializer);
                                }
                            }
                            catch (InvalidOperationException ex)
                            {
                                _logger.LogError($"XML Deserialization error: {ex.Message}");
                                if (ex.InnerException != null)
                                {
                                    _logger.LogError($"Inner exception: {ex.InnerException.Message}");
                                }
                            }
                            messageBuilder.Clear();
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Received empty message or whitespace.");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error while reading messages: {ex.Message}");
            }
        }

        private async Task SendMessagesAsync(SslStream sslStream, int count, int delayMs, CancellationToken token)
        {
            var writer = new StreamWriter(sslStream) { AutoFlush = true };

            try
            {
                for (int i = 0; i < count && !token.IsCancellationRequested; i++)
                {
                    var pongVar = new pong { timestamp = DateTime.UtcNow };
                    var pongMessage = XmlTools.SerializeToXml(pongVar) + _config.Separator;
                    await writer.WriteLineAsync(pongMessage);
                    _logger.LogInformation($"Sent: {pongVar.timestamp}");
                    await Task.Delay(delayMs, token);
                }
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogError($"Task cancelled while sending messages");

            }
            catch (Exception ex)
            {
                _logger.LogError($"Error while sending messages: {ex.Message}");
            }
        }

        private void ReadPing(StringReader stringReader, XmlSerializer pingSerializer)
        {
            var pingVar = (ping)pingSerializer.Deserialize(stringReader);
            var receivedTime = DateTime.UtcNow;
            var sentTime = pingVar.timestamp;
            var deliveryTime = receivedTime - sentTime;
            _logger.LogInformation($"Received: {pingVar.timestamp}, Delivery Time: {deliveryTime.TotalMilliseconds}ms");
        }
    }
}
