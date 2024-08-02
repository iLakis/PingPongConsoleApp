using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PingPongClient;
using PingPongServer;
using Utils;
using System.Xml.Schema;
using Microsoft.Extensions.Configuration;
using Utils.Configs;
using Utils.Configs.Client;

namespace PingPongTests
{
    public class BasicTests {
        private readonly MemoryLoggerProvider _memoryLoggerProvider;
        private readonly ILogger<PingPongTcpServer> _serverLogger;
        private readonly ILoggerFactory _loggerFactory;
        private readonly XmlSchemaSet _schemaSet;
        private readonly ILogger<SslEventListener> _sslListenerLogger;
        private readonly SslEventListener _sslListener;

        public BasicTests() {
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
        public async Task TestConnectionAndCommunication() {
            var cts = new CancellationTokenSource();
            var token = cts.Token;

            var server = new PingPongTcpServer(_serverLogger);
            var serverTask = Task.Run(() => server.StartAsync(token));

            await Task.Delay(1000); // Wait for server to boot up

            string clientLoggerCategory = "TcpClient_Test";
            string responseLoggerCategory = "ResponseLogger_Test";
            var clientLogger = _loggerFactory.CreateLogger(clientLoggerCategory);
            var responseLogger = _loggerFactory.CreateLogger(responseLoggerCategory);


            var client = new PingPongTcpClient(clientLogger, responseLogger);
            var clientTask = Task.Run(() => client.StartAsync(token));

            await Task.Delay(15000); // Wait to ensure some communication occurs

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
            var sslLogs = _memoryLoggerProvider.GetLogs(typeof(SslEventListener).FullName);

            var serverLogs = _memoryLoggerProvider.GetLogs(typeof(PingPongTcpServer).FullName);
            var serverMessages = serverLogs.Where(log => log.Contains("Received message:")).ToList();

            var clientLogs = _memoryLoggerProvider.GetLogs(clientLoggerCategory);
            var responseLogs = _memoryLoggerProvider.GetLogs(responseLoggerCategory);
            var clientMessages = responseLogs.Where(log => log.Contains("Received response:")).ToList();

            Assert.True(clientMessages.Count > 0, "Client received no messages");
            Assert.All(clientMessages, msg => Assert.True(XmlTools.ValidateXml(StringTools.RemovePrefix(msg, "Received response: "), _schemaSet)));

            Assert.True(serverMessages.Count > 0, "Server received no messages");
            Assert.All(serverMessages, msg => Assert.True(XmlTools.ValidateXml(StringTools.RemovePrefix(msg, "Received message: "), _schemaSet)));

            var serverErrorLogs = serverLogs.Where(log => log.Contains("error", StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.True(!serverErrorLogs.Any(), $"Found error logs: {string.Join(Environment.NewLine, serverErrorLogs)}");
            var clientErrorLogs = clientLogs.Where(log => log.Contains("error", StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.True(!clientErrorLogs.Any(), $"Found error logs: {string.Join(Environment.NewLine, clientErrorLogs)}");
        }

        [Fact]
        public void LoadConfiguration_MissingValues() {
            string clientSystemLoggerCategory = "ClientSystemLogger_LoadConfiguration_MissingValues";
            string clientResponseLoggerCategory = "ClientResponseLogger_LoadConfiguration_MissingValues";
            var clientSystemLogger = _loggerFactory.CreateLogger(clientSystemLoggerCategory);
            var clientResponseLogger = _loggerFactory.CreateLogger(clientResponseLoggerCategory);
            var path = Directory.GetCurrentDirectory() + "\\TestConfigs\\Client\\";

            IConfigLoader<DefaultClientConfig> configLoader = new JsonConfigLoader<DefaultClientConfig>(Path.Combine(path, "TestClientConfig_MissingValues.json"), clientSystemLogger);
            var client = new PingPongTcpClient(clientSystemLogger, clientResponseLogger, configLoader);

            var clientLogs = _memoryLoggerProvider.GetLogs(clientSystemLoggerCategory);

            Assert.Contains(clientLogs, log => log.Contains("ReadTimeout was not found in config file"));
            Assert.Contains(clientLogs, log => log.Contains("WriteTimeout was not found in config file"));
            Assert.Contains(clientLogs, log => log.Contains("Using default values for missing variables."));
            Assert.Contains(clientLogs, log => log.Contains("Configuration loaded with errors"));
        }

        [Fact]
        public void LoadConfiguration_InvalidValues() {
            string clientSystemLoggerCategory = "ClientSystemLogger_InvalidValues";
            string clientResponseLoggerCategory = "ClientResponseLogger_InvalidValues";
            var clientSystemLogger = _loggerFactory.CreateLogger(clientSystemLoggerCategory);
            var clientResponseLogger = _loggerFactory.CreateLogger(clientResponseLoggerCategory);
            var path = Directory.GetCurrentDirectory() + "\\TestConfigs\\Client\\";

            IConfigLoader<DefaultClientConfig> configLoader = new JsonConfigLoader<DefaultClientConfig>(Path.Combine(path, "TestClientConfig_InvalidValues.json"), clientSystemLogger);

            var client = new PingPongTcpClient(clientSystemLogger, clientResponseLogger, configLoader);

            var clientLogs = _memoryLoggerProvider.GetLogs(clientSystemLoggerCategory);

            Assert.Contains(clientLogs, log => log.Contains("Interval in config is not a valid integer."));
            Assert.Contains(clientLogs, log => log.Contains("ReconnectDelay in config is not a valid integer."));
            Assert.Contains(clientLogs, log => log.Contains("ReadTimeout in config is not a valid integer."));
            Assert.Contains(clientLogs, log => log.Contains("Using default values for missing variables."));
            Assert.Contains(clientLogs, log => log.Contains("Configuration loaded with errors"));
        }
    }
}