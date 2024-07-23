using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualBasic;
using PPClient;
using PPServer;
using System.Xml.Linq;
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
            } catch (OperationCanceledException ex) {

            } catch (Exception ex) {
            
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

            Assert.NotEmpty(clientMessages);
            Assert.All(clientMessages, msg => Assert.True(ValidateXml(RemovePrefix(msg, "Received response: "), _schemaSet)));

            Assert.NotEmpty(serverMessages);
            Assert.All(serverMessages, msg => Assert.True(ValidateXml(RemovePrefix(msg, "Received message: "), _schemaSet)));

            Assert.DoesNotContain(serverLogs, log => log.Contains("error", System.StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(clientLogs, log => log.Contains("error", System.StringComparison.OrdinalIgnoreCase));


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