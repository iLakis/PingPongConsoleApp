using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using System.Xml.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PingPongSchema;

namespace PPServer {
    public class TcpServer {
        protected const int Port = 5001;
        protected string ServerSslPass;
        protected static X509Certificate2 ServerCertificate;
        protected static XmlSchemaSet schemaSet;
        protected const string Separator = "<EOF>";

        protected readonly ILogger<TcpServer> _logger;

        public TcpServer(ILogger<TcpServer> logger) {
            _logger = logger;
            try {
                LoadConfiguration();
                LoadCertificate();
                LoadXsdSchema();

            } catch(Exception ex) {
                _logger.LogError($"Error in TcpServer constructor: {ex.Message}");
                throw;
            }
        
        }

        public async Task StartAsync(CancellationToken token) {
            ThreadPool.SetMinThreads(100, 100);
            TcpListener listener = new TcpListener(IPAddress.Any, Port);
            listener.Start();
            _logger.LogInformation($"Server started on port {Port}");

            try {
                while (!token.IsCancellationRequested) {
                    if (listener.Pending()) {
                        System.Net.Sockets.TcpClient client = listener.AcceptTcpClient();
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

        protected virtual async Task HandleClientAsync(System.Net.Sockets.TcpClient client, CancellationToken token) {
            var sslStream = new SslStream(client.GetStream(), false);
            try {
                await AuthenticateSsl(sslStream);

                using (StreamReader reader = new StreamReader(sslStream))
                using (StreamWriter writer = new StreamWriter(sslStream) { AutoFlush = true }) {
                    var pingSerializer = new XmlSerializer(typeof(ping));
                    var pongSerializer = new XmlSerializer(typeof(pong));
                    StringBuilder messageBuilder = new StringBuilder();

                    while (client.Connected && !token.IsCancellationRequested) {
                        string line = await reader.ReadLineAsync();
                        if (!string.IsNullOrWhiteSpace(line)) {
                            messageBuilder.AppendLine(line);
                            if (line.EndsWith(Separator)) {
                                string message = messageBuilder.ToString();
                                message = message.Replace(Separator, "");
                                _logger.LogInformation($"Received message: {message}", message);

                                try {
                                    using (var stringReader = new StringReader(message)) {
                                        ReadPing(stringReader, pingSerializer);
                                        SendPong(writer);                                        
                                    }
                                } catch (InvalidOperationException ex) {
                                    _logger.LogError($"XML Deserialization error: {ex.Message}");
                                    if (ex.InnerException != null) {
                                        //Console.WriteLine($"Inner exception: {ex.InnerException.Message}");
                                        _logger.LogError($"Inner exception: {ex.InnerException.Message}");
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
                _logger.LogError($"Authentication failed: {ex.Message}");
                if (ex.InnerException != null) {
                    _logger.LogError($"Inner exception: {ex.InnerException.Message}");
                }
            } catch (IOException ioEx) {
                _logger.LogError($"IO error: {ioEx.Message}");
                if (ioEx.InnerException != null) {
                    _logger.LogError($"Inner exception: {ioEx.InnerException.Message}");
                }
            } catch (TaskCanceledException ex) {
                //_systemLogger.LogError($"Task cancelled: {ex.Message}");
                if (ex.InnerException != null) {
                    _logger.LogError($"Task cancelled: {ex.InnerException.Message}");
                } else {
                    _logger.LogError($"Task cancelled");
                }
            } catch (Exception ex) {
                _logger.LogInformation($"Error: {ex.Message}");
                if (ex.InnerException != null) {
                    _logger.LogError($"Inner exception: {ex.InnerException.Message}");
                }
            } finally {
                client.Close(); // Ensure the client is closed on error
                _logger.LogWarning("Client connection closed.");
            }

        }
        protected void LoadConfiguration() {
            _logger.LogInformation("Loading configuration...");
            var configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .Build();

            ServerSslPass = configuration["ServerSslPass"];
            if (string.IsNullOrEmpty(ServerSslPass)) {
                throw new Exception("SSL Password was not found in config file");
            }
            _logger.LogInformation("Configuration loaded successfully.");
        }

        protected void LoadCertificate() {
            string certPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ServerCertificate\\server.pfx");

            if (!File.Exists(certPath)) {
                throw new FileNotFoundException("Certificate file not found", certPath);
            }

            ServerCertificate = new X509Certificate2(
                certPath,
                ServerSslPass,
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
            Console.WriteLine("Authenticating SSL...");
            await sslStream.AuthenticateAsServerAsync(
                ServerCertificate,
                clientCertificateRequired: false,
                enabledSslProtocols: SslProtocols.Tls12 | SslProtocols.Tls13,
                checkCertificateRevocation: false); //true. false for testing 
            Console.WriteLine("SSL authentication succeeded.");
            _logger.LogInformation("SSL authentication succeeded.");
        }
        protected void SendPong(StreamWriter writer) {
            var pongVar = new pong { timestamp = DateTime.UtcNow };
            var pongMessage = SerializeToXml(pongVar) + Separator;
            writer.WriteLine(pongMessage);
            _logger.LogInformation($"Sent: {pongVar.timestamp}");
        }
        protected void ReadPing(StringReader stringReader, XmlSerializer pingSerializer) {
            var pingVar = (ping)pingSerializer.Deserialize(stringReader);
            var receivedTime = DateTime.UtcNow;
            var sentTime = pingVar.timestamp;
            var deliveryTime = receivedTime - sentTime;
            _logger.LogInformation($"Received: {pingVar.timestamp}, Delivery Time: {deliveryTime.TotalMilliseconds}ms");
        }
        protected string SerializeToXml<T>(T obj) {
            var serializer = new XmlSerializer(typeof(T));
            var settings = new XmlWriterSettings {
                Indent = true,
                Encoding = Encoding.UTF8,
                OmitXmlDeclaration = false
            };
            using (var stringWriter = new StringWriter())
            using (var xmlWriter = XmlWriter.Create(stringWriter, settings)) {
                serializer.Serialize(xmlWriter, obj);
                return stringWriter.ToString();
            }
        }
        protected static bool ValidateXml(XDocument xmlDoc, XmlSchemaSet schemaSet) {
            try {
                xmlDoc.Validate(schemaSet, (o, e) => {
                    Console.WriteLine($"XML validation error: {e.Message}");
                    throw new XmlSchemaValidationException(e.Message);
                });
                return true;
            } catch (XmlSchemaValidationException) {
                return false;
            }
        }
    }
}
