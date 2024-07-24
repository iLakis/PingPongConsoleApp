using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PingPongClient;
using PingPongServer;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Xml.Schema;

namespace PingPongTests {
    public class TestScenarios {
        private readonly MemoryLoggerProvider _memoryLoggerProvider;
        private readonly ILogger<TcpServer> _serverLogger;
        private readonly ILoggerFactory _loggerFactory;
        private readonly XmlSchemaSet _schemaSet;

        public TestScenarios() {
            _memoryLoggerProvider = new MemoryLoggerProvider();
            var serviceProvider = new ServiceCollection()
                .AddLogging(configure => configure.AddProvider(_memoryLoggerProvider))
                .BuildServiceProvider();

            _loggerFactory = serviceProvider.GetService<ILoggerFactory>();
            _serverLogger = _loggerFactory.CreateLogger<TcpServer>();

            _schemaSet = new XmlSchemaSet();
            var schemaPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "schema.xsd");
            _schemaSet.Add("", schemaPath);
        }

        [Fact]
        public async Task TestServerOverload() {
            var cts = new CancellationTokenSource();
            var token = cts.Token;

            var server = new TcpServer(_serverLogger);
            var serverTask = Task.Run(() => { server.StartAsync(token); });

            await Task.Delay(1000); // Wait for server to boot up

            var clientTasks = new List<Task>();
            var clients = new List<(TcpClient client, string clientLoggerCategory, string responseLoggerCategory)>();
            for (int i = 0; i < 200; i++) { // Amount of clients
                string clientLoggerCategory = $"TcpClient_{i}";
                string responseLoggerCategory = $"ResponseLogger_{i}";
                var clientLogger = _loggerFactory.CreateLogger(clientLoggerCategory);
                var responseLogger = _loggerFactory.CreateLogger(responseLoggerCategory);

                var client = new TcpClient(clientLogger, responseLogger);
                clients.Add((client, clientLoggerCategory, responseLoggerCategory));
                clientTasks.Add(Task.Run(() => client.StartAsync(token)));
                //await Task.Delay(200); // Delay between connections
            }

            await Task.Delay(10000); // Wait to ensure some communication occurs


            try {

                cts.Cancel();
               await Task.WhenAll(serverTask);
            } catch (OperationCanceledException) {

            } catch (Exception) {

            }

            if (serverTask.IsCompleted) {
                serverTask.Dispose();
            }

            var serverLogs = _memoryLoggerProvider.GetLogs(typeof(TcpServer).FullName);
            var serverMessages = serverLogs.Where(log => log.Contains("Received message:")).ToList();

            Assert.NotEmpty(serverMessages);
            Assert.All(serverMessages, msg => Assert.True(ValidateXml(RemovePrefix(msg, "Received message: "), _schemaSet)));

            foreach (var (client, clientLoggerCategory, responseLoggerCategory) in clients) {
                var responseLogs = _memoryLoggerProvider.GetLogs(responseLoggerCategory);
                var clientMessages = responseLogs.Where(log => log.Contains("Received response:")).ToList();

                Assert.True(clientMessages.Count > 0, $"{clientLoggerCategory} has no messages");
                Assert.All(clientMessages, msg => 
                Assert.True(ValidateXml(RemovePrefix(msg, "Received response: "), _schemaSet), $"{clientLoggerCategory} reveived invalid response"));

                var clientLogs = _memoryLoggerProvider.GetLogs(clientLoggerCategory);
                Assert.DoesNotContain(clientLogs, log => log.Contains("error", StringComparison.OrdinalIgnoreCase));
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


            var testServer = new TestServer_SpammingMessages(serverLogger);
            var serverTask = testServer.StartSendingPongs(20000, 1, cts.Token);

            await Task.Delay(1000);

            
            var client = new TestTcpClient_NonStopListening(clientSystemLogger, clientResponseLogger);
            var clientTask = client.StartAsync(cts.Token);

            await Task.Delay(22000); 
       
            try {
                cts.Cancel();
                await Task.Delay(1000);
                //await Task.WhenAny(serverTask, clientTask);
            } catch (OperationCanceledException ex) {

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

            var server = new TcpServer(_serverLogger);
            var serverTask = Task.Run(() => server.StartAsync(token));

            await Task.Delay(1000); // Wait for server to boot up

            string clientLoggerCategory = "TcpClient_ReconnectTest";
            string clientResponseLoggerCategory = "ResponseLogger_ReconnectTest";
            var clientLogger = _loggerFactory.CreateLogger(clientLoggerCategory);
            var responseLogger = _loggerFactory.CreateLogger(clientResponseLoggerCategory);

            var client = new TcpClient(clientLogger, responseLogger);
            var clientTask = Task.Run(() => client.StartAsync(token));

            await Task.Delay(5000); // Wait to ensure some communication occurs

            // Simulate connection loss
            client.Disconnect();

            await Task.Delay(10000); // Wait to ensure reconnection and some communication occurs

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

            var serverLogs = _memoryLoggerProvider.GetLogs(typeof(TcpServer).FullName);
            var serverMessages = serverLogs.Where(log => log.Contains("Received message:")).ToList();

            var clientLogs = _memoryLoggerProvider.GetLogs(clientLoggerCategory);
            var clientResponseLogs = _memoryLoggerProvider.GetLogs(clientResponseLoggerCategory);
            var clientMessages = clientResponseLogs.Where(log => log.Contains("Received response:")).ToList();

            var logsAfterReconnect = clientLogs.SkipWhile(log => !log.Contains("Reconnected successfully.")).Skip(1).ToList();
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

            ValidateLogs(clientLoggerCategory, clientResponseLoggerCategory, typeof(TcpServer).FullName);
            
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
            var responseLogs = _memoryLoggerProvider.GetLogs(clientResponseLoggerCategory);
            var clientMessages = responseLogs.Where(log => log.Contains("Received response:")).ToList();

            Assert.NotEmpty(clientMessages);
            Assert.All(clientMessages, msg => Assert.True(ValidateXml(RemovePrefix(msg, "Received response: "), _schemaSet), "Client received invalid response"));

            Assert.NotEmpty(serverMessages);
            Assert.All(serverMessages, msg => Assert.True(ValidateXml(RemovePrefix(msg, "Received message: "), _schemaSet), "Server received invalid response"));

            Assert.DoesNotContain(serverLogs, log => log.Contains("error", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(clientLogs, log => log.Contains("error", StringComparison.OrdinalIgnoreCase));
        }

        private bool ValidateXml(string xmlMessage, XmlSchemaSet schemaSet) {
            try {
                var xmlDoc = XDocument.Parse(xmlMessage);
                xmlDoc.Validate(schemaSet, (o, e) => {
                    throw new XmlSchemaValidationException(e.Message);
                });
                return true;
            } catch (XmlSchemaValidationException) {
                return false;
            }
        }

        private string RemovePrefix(string message, string prefix) {
            if (message.StartsWith(prefix)) {
                return message.Substring(prefix.Length).Trim();
            }
            return message;
        }
    
    }


}
