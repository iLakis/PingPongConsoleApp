using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PingPongClient;
using PingPongServer;
using System.Text.RegularExpressions;
using Utils;
using System.Xml.Schema;
using Tests.TestClients;
using Tests.TestServers;
using Microsoft.Extensions.Configuration;
using Utils.Configs;
using Utils.Configs.Client;
using Newtonsoft.Json.Linq;

namespace PingPongTests
{
    public class TestScenarios {
        private readonly MemoryLoggerProvider _memoryLoggerProvider;
        private readonly ILogger<PingPongTcpServer> _serverLogger;
        private readonly ILogger<SslEventListener> _sslListenerLogger;
        private readonly ILoggerFactory _loggerFactory;
        private readonly XmlSchemaSet _schemaSet;
        private readonly SslEventListener _sslListener;

        public TestScenarios() {
            _memoryLoggerProvider = new MemoryLoggerProvider();
            var serviceProvider = new ServiceCollection()
                .AddLogging(configure => configure.AddProvider(_memoryLoggerProvider))
                .BuildServiceProvider();

            _loggerFactory = serviceProvider.GetService<ILoggerFactory>();
            _serverLogger = _loggerFactory.CreateLogger<PingPongTcpServer>();
            _sslListenerLogger = _loggerFactory.CreateLogger<SslEventListener>();
            _schemaSet = new XmlSchemaSet();
            var schemaPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "schema.xsd");
            _schemaSet.Add("", schemaPath);
            _sslListener = new SslEventListener(_sslListenerLogger);
        }

        [Fact]
        public async Task TestServerOverload() {
            var cts = new CancellationTokenSource();
            var token = cts.Token;

            var server = new PingPongTcpServer(_serverLogger);
            var serverTask = Task.Run(() => { server.StartAsync(token); });

            await Task.Delay(1000); // Wait for server to boot up

            var clientTasks = new List<Task>();
            var clients = new List<(PingPongTcpClient client, string clientLoggerCategory, string responseLoggerCategory)>();
            var path = Directory.GetCurrentDirectory() + "\\TestConfigs\\Client\\";

            for (int i = 0; i < 500; i++) { // Amount of clients
                string clientSystemLoggerCategory = $"TcpClient_{i}";
                string clientResponseLoggerCategory = $"ClientResponseLogger_{i}";
                var clientSystemLogger = _loggerFactory.CreateLogger(clientSystemLoggerCategory);
                var clientResponseLogger = _loggerFactory.CreateLogger(clientResponseLoggerCategory);
                IConfigLoader<DefaultClientConfig> configLoader = new JsonConfigLoader<DefaultClientConfig>(Path.Combine(path, "TestClientConfig_LowInterval.json"), clientSystemLogger);

                var client = new PingPongTcpClient(clientSystemLogger, clientResponseLogger, configLoader);
                clients.Add((client, clientSystemLoggerCategory, clientResponseLoggerCategory));
                clientTasks.Add(Task.Run(() => client.StartAsync(token)));
                //await Task.Delay(200); // Delay between connections
            }

            await Task.Delay(15000); // Wait to ensure some communication occurs

            try {
                cts.Cancel();
                await Task.WhenAll(serverTask);
                if (serverTask.IsCompleted) {
                    serverTask.Dispose();
                }
                foreach (var task in clientTasks) {
                    await Task.WhenAll(task);
                    if (task.IsCompleted) {
                        task.Dispose();
                    }
                }
            } catch (OperationCanceledException) {
                //Expected exception
            } catch (Exception ex) {
                _serverLogger.LogError($"Unexpected error during testing: {ex.Message}");
            }

            var serverLogs = _memoryLoggerProvider.GetLogs(typeof(PingPongTcpServer).FullName);
            var serverMessages = serverLogs.Where(log => log.Contains("Received message:")).ToList();

            Assert.NotEmpty(serverMessages);
            Assert.All(serverMessages, msg => Assert.True(XmlTools.ValidateXml(StringTools.RemovePrefix(msg, "Received message: "), _schemaSet)));
            var serverErrorLogs = serverLogs.Where(log => log.Contains("error", StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.True(!serverErrorLogs.Any(), $"Found error logs: {string.Join(Environment.NewLine, serverErrorLogs)}");


            foreach (var (client, clientLoggerCategory, responseLoggerCategory) in clients) {
                var responseLogs = _memoryLoggerProvider.GetLogs(responseLoggerCategory);
                var clientMessages = responseLogs.Where(log => log.Contains("Received response:")).ToList();
                if (responseLoggerCategory.Contains("495")) {
                    Assert.NotEmpty(clientMessages); // just to stop debugging at certain client
                }

                Assert.True(clientMessages.Count > 0, $"{clientLoggerCategory} has no messages");
                Assert.All(clientMessages, msg => 
                Assert.True(XmlTools.ValidateXml(StringTools.RemovePrefix(msg, "Received response: "), _schemaSet), $"{clientLoggerCategory} reveived invalid response"));

                var clientLogs = _memoryLoggerProvider.GetLogs(clientLoggerCategory);
                var clientErrorLogs = clientLogs.Where(log => log.Contains("error", StringComparison.OrdinalIgnoreCase)).ToList();
                Assert.True(!clientErrorLogs.Any(), $"Found error logs: {string.Join(Environment.NewLine, clientErrorLogs)}");

            }

        }

        [Fact]
        public async Task TestClientOverload() {
            string clientLSystemLoggerCategory = "ClientSystemLogger";
            string clientResponseLoggerCategory = "ClientResponseLogger";
            var clientSystemLogger = _loggerFactory.CreateLogger(clientLSystemLoggerCategory);
            var clientResponseLogger = _loggerFactory.CreateLogger(clientResponseLoggerCategory);
            var serverLogger = _loggerFactory.CreateLogger<TestServer_SpammingMessages>();
            var cts = new CancellationTokenSource();

            var server = new TestServer_SpammingMessages(serverLogger);
            var serverTask = Task.Run(() => server.StartAsync(cts.Token));
            //var serverTask = testServer.StartSendingPongs(20000, 1, cts.Token);

            await Task.Delay(1000);

            var client = new TestClient_NonStopListening(clientSystemLogger, clientResponseLogger);
            var clientTask =Task.Run(() => client.StartAsync(cts.Token));

            await Task.Delay(30000);

            try {
                cts.Cancel();
                await Task.Delay(1000);
            } catch (OperationCanceledException ex) {
                //clientSystemLogger.LogWarning($"Operation canceled: {ex.Message}");
            } catch (Exception ex) {
                clientSystemLogger.LogError($"Unexpected error: {ex.Message}");
            }

            if (serverTask.IsCompleted) {
                serverTask.Dispose();
            }
            if (clientTask.IsCompleted) {
                clientTask.Dispose();
            }

            ValidateLogs(clientLSystemLoggerCategory, clientResponseLoggerCategory, typeof(TestServer_SpammingMessages).FullName);
        }

        [Fact]
        public async Task TestClientReconnection() {
            var cts = new CancellationTokenSource();
            var token = cts.Token;

            var server = new PingPongTcpServer(_serverLogger);
            var serverTask = Task.Run(() => server.StartAsync(token));

            await Task.Delay(1000); // Wait for server to boot up

            string clientLoggerCategory = "TcpClient_ReconnectTest";
            string clientResponseLoggerCategory = "ResponseLogger_ReconnectTest";
            var clientLogger = _loggerFactory.CreateLogger(clientLoggerCategory);
            var responseLogger = _loggerFactory.CreateLogger(clientResponseLoggerCategory);

            var client = new PingPongTcpClient(clientLogger, responseLogger);
            var clientTask = Task.Run(() => client.StartAsync(token));

            await Task.Delay(10000); // Wait to ensure some communication occurs

            // Simulate connection loss
            client.DisconnectCurrentConnection();

            await Task.Delay(20000); // Wait to ensure reconnection and some communication occurs

            cts.Cancel();
            try {
                await Task.WhenAll(serverTask, clientTask);
            } catch (OperationCanceledException) {
                // Expected exception when tasks are cancelled
            } catch (Exception ex) {
                clientLogger.LogError($"Unexpected error during testing: {ex.Message}");
            }

            if (serverTask.IsCompleted) {
                serverTask.Dispose();
            }
            if (clientTask.IsCompleted) {
                clientTask.Dispose();
            }

            var serverLogs = _memoryLoggerProvider.GetLogs(typeof(PingPongTcpServer).FullName);
            var serverMessages = serverLogs.Where(log => log.Contains("Received message:")).ToList();

            var clientLogs = _memoryLoggerProvider.GetLogs(clientLoggerCategory);
            var clientResponseLogs = _memoryLoggerProvider.GetLogs(clientResponseLoggerCategory);
            var clientMessages = clientResponseLogs.Where(log => log.Contains("Received response:")).ToList();

            var logsAfterReconnect = clientLogs.SkipWhile(log => !log.Contains("Connection swapped successfully.")).Skip(1).ToList();
            var firstMessageAfterReconnect = logsAfterReconnect.FirstOrDefault(log => log.Contains("Sent:"));
            var firstMessageTimestamp = ExtractTimestamp(firstMessageAfterReconnect);

            var clientMessagesAfterReconnect = clientMessages.Where(msg => ExtractTimestamp(msg) >= firstMessageTimestamp).ToList();
            Assert.True(clientMessagesAfterReconnect.Count > 0, "Client received no messages after reconnection");

            var serverLogsAfterConnect = serverLogs.SkipWhile(log => !log.Contains("Client connected")).Skip(1).ToList();
            var serverLogsAfterReconnect = serverLogsAfterConnect.SkipWhile(log => !log.Contains("Client connected")).Skip(1).ToList();
            var serverMessagesAfterReconnect = serverLogsAfterReconnect.Where(log => log.Contains("Received message:")).ToList();


            Assert.True(serverMessagesAfterReconnect.Count > 0, "Server received no messages received after reconnection");
            var reconnectionAttempts = clientLogs.Count(log => log.Contains("Attempting to reconnect"));
            Assert.True(reconnectionAttempts <= 5, "Client attempted to reconnect more than the maximum allowed attempts"); // TODO read attemts variable from config

            ValidateLogs(clientLoggerCategory, clientResponseLoggerCategory, typeof(PingPongTcpServer).FullName);
            
        }
        [Fact]
        public async Task TestClientWithSlowConnection() {
            var cts = new CancellationTokenSource();
            var token = cts.Token;

            //var server = new TestTcpServer_Slow(_serverLogger);
            var server = new PingPongTcpServer(_serverLogger);
            var serverTask = Task.Run(() => server.StartAsync(token));

            await Task.Delay(1000); // Wait for server to boot up

            string clientLoggerCategory = "TcpClient_SlowConnectionTest";
            string clietnResponseLoggerCategory = "ClientResponseLogger_SlowConnectionTest";
            var clientLogger = _loggerFactory.CreateLogger(clientLoggerCategory);
            var responseLogger = _loggerFactory.CreateLogger(clietnResponseLoggerCategory);

            var client = new TestTcpClient_Slow(clientLogger, responseLogger);
            var clientTask = Task.Run(() => client.StartAsync(token));

            await Task.Delay(40000); // Wait to ensure some communication occurs

            cts.Cancel();

            try {
                await Task.WhenAll(serverTask, clientTask);
            } catch (OperationCanceledException) {
                // Expected exception when tasks are cancelled
            } catch (Exception ex) {
                clientLogger.LogError($"Unexpected error during testing: {ex.Message}");
                if(ex.InnerException != null) {
                    clientLogger.LogError($"Inner error: {ex.InnerException.Message}");
                }
            }

            if (serverTask.IsCompleted) {
                serverTask.Dispose();
            }
            if (clientTask.IsCompleted) {
                clientTask.Dispose();
            }

            ValidateLogs(clientLoggerCategory, clietnResponseLoggerCategory, typeof(PingPongTcpServer).FullName);
        }
        [Fact]
        public async Task TestServerShutdown() {
            var serverCts = new CancellationTokenSource();
            var clientCts = new CancellationTokenSource();

            var serverToken = serverCts.Token;
            var clientToken = clientCts.Token;

            var serverLogger = _loggerFactory.CreateLogger<PingPongTcpServer>();
            var server = new PingPongTcpServer(serverLogger);
            var serverTask = Task.Run(() => server.StartAsync(serverToken));

            await Task.Delay(1000); // Wait for server to boot up

            string clientSystemLoggerCategory = "TcpClient_ServerShutdownTest";
            string clientResponseLoggerCategory = "ClientResponseLogger_ServerShutdownTest";
            var clientLogger = _loggerFactory.CreateLogger(clientSystemLoggerCategory);
            var clientResponseLogger = _loggerFactory.CreateLogger(clientResponseLoggerCategory);

            var client = new PingPongTcpClient(clientLogger, clientResponseLogger);
            var clientTask = Task.Run(() => client.StartAsync(clientToken));

            await Task.Delay(5000); // Wait to ensure some communication occurs

            serverCts.Cancel();

            // Wait some more time to see how the client reacts to server shutdown
            await Task.Delay(120000);

            clientCts.Cancel();

            try {
                await Task.WhenAll(serverTask, clientTask);
            } catch (OperationCanceledException) {
                // Expected exception when tasks are cancelled
            } catch (Exception ex) {
                clientLogger.LogError($"Unexpected error during testing: {ex.Message}");
                if (ex.InnerException != null) {
                    clientLogger.LogError($"Inner error: {ex.InnerException.Message}");
                }
            }

            if (serverTask.IsCompleted) {
                serverTask.Dispose();
            }
            if (clientTask.IsCompleted) {
                clientTask.Dispose();
            }

            var serverLogs = _memoryLoggerProvider.GetLogs(typeof(PingPongTcpServer).FullName);
            var serverMessages = serverLogs.Where(log => log.Contains("Received message:")).ToList();

            var clientLogs = _memoryLoggerProvider.GetLogs(clientSystemLoggerCategory);
            var clientResponseLogs = _memoryLoggerProvider.GetLogs(clientResponseLoggerCategory);
            var clientMessages = clientResponseLogs.Where(log => log.Contains("Received response:")).ToList();

            Assert.NotEmpty(clientMessages);
            Assert.All(clientMessages, msg => Assert.True(XmlTools.ValidateXml(StringTools.RemovePrefix(msg, "Received response: "), _schemaSet), "Client received invalid response"));

            Assert.NotEmpty(serverMessages);
            Assert.All(serverMessages, msg => Assert.True(XmlTools.ValidateXml(StringTools.RemovePrefix(msg, "Received message: "), _schemaSet), "Server received invalid response"));
            Assert.True(clientLogs.Contains("Max reconnection attempts reached. Giving up."), "Client didn't reach max reconnection attempts");
            Assert.False(clientLogs.Contains("Attempt 6 to reconnect"), "Client exceeded reconnection cap"); //TODO get the cap from config
        }
        private DateTime ExtractTimestamp(string log) {
            try {
                // Try to extract timestamp from "Sent: " log
                if (log.Contains("Sent: ")) {
                    var timestampPart = log.Split(new[] { "Sent: " }, StringSplitOptions.None)[1];
                    return DateTime.Parse(timestampPart.Trim(), null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);
                }

                // Try to extract timestamp from response or server log
                var regex = new Regex(@"timestamp=""(?<timestamp>[^""]+)""");
                var match = regex.Match(log);
                if (match.Success) {
                    var timestampPart = match.Groups["timestamp"].Value;
                    return DateTime.Parse(timestampPart, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);
                }

                throw new FormatException($"Log '{log}' does not contain a valid timestamp.");
            } catch (Exception ex) {
                throw new FormatException($"Log '{log}' does not contain a valid timestamp. Error: {ex.Message}", ex);
            }
        }
        private void ValidateLogs(string clientSystemLoggerCategory, string clientResponseLoggerCategory, string serverLoggerCategory) {
            var serverLogs = _memoryLoggerProvider.GetLogs(serverLoggerCategory);
            var serverMessages = serverLogs.Where(log => log.Contains("Received message:")).ToList();

            var clientLogs = _memoryLoggerProvider.GetLogs(clientSystemLoggerCategory);
            var clientResponseLogs = _memoryLoggerProvider.GetLogs(clientResponseLoggerCategory);
            var clientMessages = clientResponseLogs.Where(log => log.Contains("Received response:")).ToList();

            Assert.NotEmpty(clientMessages);
            Assert.All(clientMessages, msg => Assert.True(XmlTools.ValidateXml(StringTools.RemovePrefix(msg, "Received response: "), _schemaSet), "Client received invalid response"));

            Assert.NotEmpty(serverMessages);
            Assert.All(serverMessages, msg => Assert.True(XmlTools.ValidateXml(StringTools.RemovePrefix(msg, "Received message: "), _schemaSet), "Server received invalid response"));

            var serverErrorLogs = serverLogs.Where(log => log.Contains("error", StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.True(!serverErrorLogs.Any(), $"Found error logs: {string.Join(Environment.NewLine, serverErrorLogs)}");
            var clientErrorLogs = clientLogs.Where(log => log.Contains("error", StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.True(!clientErrorLogs.Any(), $"Found error logs: {string.Join(Environment.NewLine, clientErrorLogs)}");
        }
            
    }

}
