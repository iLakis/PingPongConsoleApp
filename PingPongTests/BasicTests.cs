using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PingPongClient;
using PingPongServer;
using Utils;
using System.Xml.Schema;

namespace PingPongTests {
    public class BasicTests {
        private readonly MemoryLoggerProvider _memoryLoggerProvider;
        private readonly ILogger<TcpServer> _serverLogger;
        private readonly ILoggerFactory _loggerFactory;
        private readonly XmlSchemaSet _schemaSet;

        public BasicTests() {
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
        public async Task TestConnectionAndCommunication() {
            var cts = new CancellationTokenSource();
            var token = cts.Token;

            var server = new TcpServer(_serverLogger);
            var serverTask = Task.Run(() => server.StartAsync(token));

            await Task.Delay(1000); // Wait for server to boot up

            string clientLoggerCategory = "TcpClient_Test";
            string responseLoggerCategory = "ResponseLogger_Test";
            var clientLogger = _loggerFactory.CreateLogger(clientLoggerCategory);
            var responseLogger = _loggerFactory.CreateLogger(responseLoggerCategory);

            var client = new TcpClient(clientLogger, responseLogger);
            var clientTask = Task.Run(() => client.StartAsync(token));

            await Task.Delay(6000); // Wait to ensure some communication occurs

            cts.Cancel();
            try {
                await Task.WhenAll(serverTask, clientTask);
                //await Task.Delay(1000);
            } catch (OperationCanceledException ex) {

            } catch (Exception ex) {
                clientLogger.LogError($"Unexpected error during testing: {ex.Message}");
                if (ex.InnerException != null) {
                    clientLogger.LogError($"Inner Exception: {ex.InnerException.Message}");

                }
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
            var responseLogs = _memoryLoggerProvider.GetLogs(responseLoggerCategory);
            var clientMessages = responseLogs.Where(log => log.Contains("Received response:")).ToList();

            Assert.True(clientMessages.Count > 0, "Client received no messages");
            Assert.All(clientMessages, msg => Assert.True(XmlTools.ValidateXml(StringTools.RemovePrefix(msg, "Received response: "), _schemaSet)));

            Assert.True(serverMessages.Count > 0, "Server received no messages");
            Assert.All(serverMessages, msg => Assert.True(XmlTools.ValidateXml(StringTools.RemovePrefix(msg, "Received message: "), _schemaSet)));

            Assert.DoesNotContain(serverLogs, log => log.Contains("error", System.StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(clientLogs, log => log.Contains("error", System.StringComparison.OrdinalIgnoreCase));
        }

        
    }
}