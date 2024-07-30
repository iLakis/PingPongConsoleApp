using Microsoft.Extensions.Logging;
using PingPongServer;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Security;
using System.Security.Authentication;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Utils;

namespace Tests.TestServers
{
    public class TestTcpServer_Slow : PingPongTcpServer
    {
        public TestTcpServer_Slow(ILogger<PingPongTcpServer> logger) : base(logger) { }
        protected override async Task HandleClientAsync(System.Net.Sockets.TcpClient client, CancellationToken token)
        {
            var sslStream = new SslStream(client.GetStream(), false);
            try
            {
                await AuthenticateSsl(sslStream);

                using (StreamReader reader = new StreamReader(sslStream))
                using (StreamWriter writer = new StreamWriter(sslStream) { AutoFlush = true })
                {
                    var pingSerializer = new XmlSerializer(typeof(ping));
                    var pongSerializer = new XmlSerializer(typeof(pong));
                    StringBuilder messageBuilder = new StringBuilder();

                    while (client.Connected && !token.IsCancellationRequested)
                    {
                        token.ThrowIfCancellationRequested();
                        // Simulate slow connection by adding delay
                        await Task.Delay(2000, token); //Delay

                        string line = await reader.ReadLineAsync();
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            messageBuilder.AppendLine(line);
                            if (line.EndsWith(_config.Separator))
                            {
                                string message = messageBuilder.ToString();
                                message = message.Replace(_config.Separator, "");
                                _logger.LogInformation($"Received message: {message}", message);

                                try
                                {
                                    using (var stringReader = new StringReader(message))
                                    {
                                        await Task.Delay(2000, token); //Delay
                                        ReadPing(stringReader, pingSerializer);
                                        await Task.Delay(2000, token); //Delay
                                        SendPong(writer);
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
                            break; // Exit the loop if the message is null
                        }
                    }
                    _logger.LogWarning("Client disconnected.");
                }
            }
            catch (AuthenticationException ex)
            {
                _logger.LogError($"Authentication failed: {ex.Message}");
                if (ex.InnerException != null)
                {
                    _logger.LogError($"Inner exception: {ex.InnerException.Message}");
                }
            }
            catch (IOException ioEx)
            {
                _logger.LogError($"IO error: {ioEx.Message}");
                if (ioEx.InnerException != null)
                {
                    _logger.LogError($"Inner exception: {ioEx.InnerException.Message}");
                }
            }
            catch (TaskCanceledException ex)
            {
                if (ex.InnerException != null)
                {
                    _logger.LogError($"Task cancelled: {ex.InnerException.Message}");
                }
                else
                {
                    _logger.LogError($"Task cancelled");
                }
            }
            catch (Exception ex)
            {
                _logger.LogInformation($"Error: {ex.Message}");
                if (ex.InnerException != null)
                {
                    _logger.LogError($"Inner exception: {ex.InnerException.Message}");
                }
            }
            finally
            {
                client.Close(); // Ensure the client is closed on error
                _logger.LogWarning("Client connection closed.");
            }
        }
    }
}
