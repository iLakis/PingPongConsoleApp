﻿using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Net.Security;
using System.Net.Sockets;
using System.IO;
using System.Diagnostics;
using System.Xml.Serialization;
using System.Text;
using Utils;
using PingPongClient;

namespace Tests.TestClients {
    public class TestTcpClient_Slow : PingPongTcpClient {
        public TestTcpClient_Slow(ILogger systemLogger, ILogger responseLogger) : base(systemLogger, responseLogger) { }

        protected override async Task CommunicateAsync(SslStream connection, CancellationToken token) {
            using StreamReader reader = new StreamReader(connection);
            using StreamWriter writer = new StreamWriter(connection) { AutoFlush = true };
            var pingSerializer = new XmlSerializer(typeof(ping));
            var pongSerializer = new XmlSerializer(typeof(pong));
            StringBuilder responseBuilder = new StringBuilder();

            while (connection.CanRead && connection.CanWrite && !token.IsCancellationRequested) {
                bool pongReceived = false;
                token.ThrowIfCancellationRequested();
                try {
                    await SendPingWithTimeout(writer, token);
                    pongReceived = await ReadPongWithTimeout(reader, pongSerializer, responseBuilder, token);
                } catch (TimeoutException ex) {
                    if (!pongReceived) {
                        _systemLogger.LogWarning($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Pong not received within the expected time frame.");
                        await HandleTimeoutAsync(token);
                    }
                }
             
                await Task.Delay(_config.Interval, token);
            }
        }

        private async Task SendPingWithTimeout(StreamWriter writer, CancellationToken token) {
            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token)) {
                timeoutCts.CancelAfter(_config.WriteTimeout);
                var pingVar = new ping { timestamp = DateTime.UtcNow };
                var pingMessage = XmlTools.SerializeToXml(pingVar) + _config.Separator;

                try {
                    await writer.WriteLineAsync(pingMessage).WaitAsync(timeoutCts.Token);
                    _systemLogger.LogInformation($" Sent: {pingVar.timestamp}");
                } catch (OperationCanceledException) {
                    _systemLogger.LogError($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Sending ping message timed out.");
                    throw new TimeoutException("Sending ping message timed out.");
                }
            }
        }

        private async Task<bool> ReadPongWithTimeout(StreamReader reader, XmlSerializer pongSerializer, StringBuilder responseBuilder, CancellationToken token) {
            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token)) {
                timeoutCts.CancelAfter(_config.ReadTimeout);
                try {
                    while (!timeoutCts.Token.IsCancellationRequested) {
                        var line = await reader.ReadLineAsync().WaitAsync(timeoutCts.Token);
                        if (!string.IsNullOrWhiteSpace(line)) {
                            responseBuilder.AppendLine(line);
                            if (line.EndsWith(_config.Separator)) {
                                string response = responseBuilder.ToString();
                                response = response.Replace(_config.Separator, "");
                                _responseLogger.LogInformation($"Received response: {response}", response);

                                using (var stringReader = new StringReader(response)) {
                                    ReadPong(pongSerializer, stringReader);
                                    return true;
                                }
                            }
                        }
                    }
                } catch (OperationCanceledException) {
                    _systemLogger.LogError($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Reading pong message timed out.");
                    throw new TimeoutException("Reading pong message timed out.");
                } catch (Exception ex) {
                    _systemLogger.LogError($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Error during reading: {ex.Message}");
                    throw;
                }
            }
            return false;
        }

        protected override async Task HandleTimeoutAsync(CancellationToken token) {
            _systemLogger.LogWarning($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Handling timeout - attempting to reconnect...");
            try {
                await SwapConnectionAsync(token);
            } catch (Exception ex) {
                _systemLogger.LogError($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Error during reconnection: {ex.Message}");
                throw;
            }
        }
    }
}
