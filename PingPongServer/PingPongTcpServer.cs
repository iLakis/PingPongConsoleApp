using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml.Schema;
using System.Xml.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Utils;
using Utils.Configs;
using Utils.Configs.Server;

namespace PingPongServer
{
    public class PingPongTcpServer {
        protected static X509Certificate2 ServerCertificate;
        protected static XmlSchemaSet schemaSet;
        protected IServerConfig _config;
        protected readonly ILogger<PingPongTcpServer> _logger;


        public PingPongTcpServer(ILogger<PingPongTcpServer> logger, IConfigLoader<DefaultServerConfig> configLoader = null) {
            _logger = logger;
            if (configLoader == null) {
                _logger.LogWarning("No configuration loader provided, using default JsonConfigLoader.");
                configLoader = new JsonConfigLoader<DefaultServerConfig>(Path.Combine(Directory.GetCurrentDirectory(), "appsettings.server.json"), _logger);
            }
            _config = configLoader.LoadConfig();
            try {
                LoadCertificate();
                LoadXsdSchema();

            } catch(Exception ex) {
                _logger.LogError($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Error in TcpServer constructor: {ex.Message}");
                throw;
            }
        
        }

        public async Task StartAsync(CancellationToken token) {
            ThreadPool.SetMinThreads(100, 100);
            TcpListener listener = new TcpListener(IPAddress.Any, _config.Port);
            listener.Start();
            _logger.LogInformation($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Server started on port {_config.Port}");

            try {
                while (!token.IsCancellationRequested) {
                    if (listener.Pending()) {
                        TcpClient client = listener.AcceptTcpClient();
                        _logger.LogInformation("Client connected");
                        _ = Task.Run(() => HandleClientAsync(client, token));
                    } else {
                        await Task.Delay(100, token);
                    }
                }
            } finally {
                listener.Stop();
                _logger.LogWarning("Server stopped");
            }
        }

        protected virtual async Task HandleClientAsync(TcpClient client, CancellationToken token) {
            var sslStream = new SslStream(client.GetStream(), false);
            sslStream.ReadTimeout = _config.ReadTimeout;
            sslStream.WriteTimeout = _config.WriteTimeout;
            try {
                await AuthenticateSsl(sslStream);

                using (StreamReader reader = new StreamReader(sslStream))
                using (StreamWriter writer = new StreamWriter(sslStream) { AutoFlush = true }) {
                    var pingSerializer = new XmlSerializer(typeof(ping));
                    var pongSerializer = new XmlSerializer(typeof(pong));
                    StringBuilder messageBuilder = new StringBuilder();

                    while (client.Connected && !token.IsCancellationRequested) {
                        token.ThrowIfCancellationRequested();
                        string line = await reader.ReadLineAsync();
                        if (!string.IsNullOrWhiteSpace(line)) {
                            messageBuilder.AppendLine(line);
                            if (line.EndsWith(_config.Separator)) {
                                string message = messageBuilder.ToString();
                                message = message.Replace(_config.Separator, "");
                                _logger.LogInformation($"Received message: {message}", message);
                                try {
                                    using (var stringReader = new StringReader(message)) {
                                        token.ThrowIfCancellationRequested();
                                        ReadPing(stringReader, pingSerializer);
                                        token.ThrowIfCancellationRequested();
                                        SendPong(writer);
                                    }
                                } catch (InvalidOperationException ex) {
                                    _logger.LogError($"[{DateTime.UtcNow:HH:mm:ss.fff}]: XML Deserialization error: {ex.Message}");
                                    if (ex.InnerException != null) {
                                        //Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Inner exception: {ex.InnerException.Message}");
                                        _logger.LogError($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Inner exception: {ex.InnerException.Message}");
                                    }
                                }
                                messageBuilder.Clear();
                            }

                        } else {
                            _logger.LogWarning("Received empty message or whitespace.");
                            break; // Exit the loop if the message is null
                        }
                    }
                    _logger.LogWarning("Client disconnected.");
                }

            } catch (AuthenticationException ex) {
                _logger.LogError($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Authentication failed: {ex.Message}");
                if (ex.InnerException != null) {
                    _logger.LogError($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Inner exception: {ex.InnerException.Message}");
                }
            } catch (IOException ioEx) {
                if (!token.IsCancellationRequested) { // without this can throw IO error after server work should be stopped.
                    _logger.LogError($"[{DateTime.UtcNow:HH:mm:ss.fff}]: IO error: {ioEx.Message}");
                    if (ioEx.InnerException != null) {
                        _logger.LogError($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Inner exception: {ioEx.InnerException.Message}");
                    }
                }
            } catch (TaskCanceledException ex) {
                _logger.LogWarning($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Task cancelled");
                
            } catch (Exception ex) {
                _logger.LogInformation($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Error: {ex.Message}");
                if (ex.InnerException != null) {
                    _logger.LogError($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Inner exception: {ex.InnerException.Message}");
                }
            } finally {
                sslStream.Close();
                client.Close(); // Ensure the client is closed on error
                _logger.LogWarning("Client connection closed.");
            }

        }
        protected void LoadCertificate() {
            string certPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ServerCertificate\\server.pfx");

            if (!File.Exists(certPath)) {
                throw new FileNotFoundException("Certificate file not found", certPath);
            }

            ServerCertificate = new X509Certificate2(
                certPath,
                _config.ServerSslPass,
                X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.MachineKeySet);
            _logger.LogInformation("Certificate loaded successfully.");
        }

        protected void LoadXsdSchema() {
            schemaSet = new XmlSchemaSet();
            string schemaPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "schema.xsd");
            if (!File.Exists(schemaPath)) {
                throw new FileNotFoundException("XML schema file not found", schemaPath);
            }
            schemaSet.Add("", schemaPath);
            _logger.LogInformation("Schema loaded successfully.");
        }
        protected async Task AuthenticateSsl(SslStream sslStream) {
            //Console.WriteLine("Authenticating SSL...");
            _logger.LogInformation("Authenticating SSL...");
            await sslStream.AuthenticateAsServerAsync(
                ServerCertificate,
                clientCertificateRequired: false,
                enabledSslProtocols: SslProtocols.Tls12 | SslProtocols.Tls13,
                checkCertificateRevocation: false); //true. false for testing 
            //Console.WriteLine("SSL authentication succeeded.");
            _logger.LogInformation("SSL authentication succeeded.");
        }
        protected void SendPong(StreamWriter writer) {
            var pongVar = new pong { timestamp = DateTime.UtcNow };
            var pongMessage = Utils.XmlTools.SerializeToXml(pongVar) + _config.Separator;
            writer.WriteLine(pongMessage);
            _logger.LogInformation($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Sent: {pongVar.timestamp}");
        }
        protected void ReadPing(StringReader stringReader, XmlSerializer pingSerializer) {
            var pingVar = (ping)pingSerializer.Deserialize(stringReader);
            var receivedTime = DateTime.UtcNow;
            var sentTime = pingVar.timestamp;
            var deliveryTime = receivedTime - sentTime;
            _logger.LogInformation($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Received: {pingVar.timestamp}, Delivery Time: {deliveryTime.TotalMilliseconds}ms");
        }
    }
}
