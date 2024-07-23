using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using System.Xml.Serialization;
using Utils;

namespace PingPongClient {
    public class TcpClient {
        protected const string ServerAddress = "localhost"; //"127.0.0.1";
        protected string ClientSslPass;
        protected const int Port = 5001;
        protected static X509Certificate2 ClientCertificate;
        protected static int Interval;
        protected static XmlSchemaSet schemaSet;
        protected const string Separator = "<EOF>";

        protected System.Net.Sockets.TcpClient _client;
        protected SslStream _sslStream;
        protected readonly ILogger _systemLogger;
        protected readonly ILogger _responseLogger;

        public TcpClient(ILogger systemLogger, ILogger responseLogger) {
            _systemLogger = systemLogger;
            _responseLogger = responseLogger;
            try {
                LoadConfiguration();
                LoadCertificate();
                LoadXsdSchema();
            } catch (Exception ex) {
                _systemLogger.LogError($"Error in TcpClient constructor: {ex.Message}");
                throw;
            }
        }
        public async Task StartAsync(CancellationToken token) {
            /*try {
                System.Net.Sockets.TcpClient client = new System.Net.Sockets.TcpClient(ServerAddress, Port);
                var sslStream = new SslStream(client.GetStream(), false, new RemoteCertificateValidationCallback(ValidateServerCertificate));
                AuthenticateSsl(sslStream);
                await CommunicateAsync(client, sslStream, token);
            } catch (AuthenticationException ex) {
                _systemLogger.LogError($"Authentication failed: {ex.Message}");
                if (ex.InnerException != null) {
                    _systemLogger.LogError($"Inner exception: {ex.InnerException.Message}");
                }
            } catch (IOException ioEx) {
                _systemLogger.LogError($"IO error: {ioEx.Message}");
            } catch (TaskCanceledException ex) {
                //_systemLogger.LogError($"Task cancelled: {ex.Message}");
                if (ex.InnerException != null) {
                    _systemLogger.LogError($"Task cancelled: {ex.InnerException.Message}");
                } else {
                    _systemLogger.LogError($"Task cancelled");
                }
            } catch (Exception ex) {
                _systemLogger.LogError($"Error: {ex.Message}");
            }*/
            while (!token.IsCancellationRequested) {
                try {
                    if (_client == null || !_client.Connected) {
                        await ConnectAsync(token);
                    }
                    await CommunicateAsync(token);
                } catch (AuthenticationException ex) {
                    _systemLogger.LogError($"Authentication failed: {ex.Message}");
                    if (ex.InnerException != null) {
                        _systemLogger.LogError($"Inner exception: {ex.InnerException.Message}");
                    }
                } catch (IOException ioEx) {
                    _systemLogger.LogError($"IO error: {ioEx.Message}");
                    if (ioEx.InnerException != null) {
                        _systemLogger.LogError($"Inner exception: {ioEx.InnerException.Message}");
                    }
                } catch (TaskCanceledException ex) {
                    //_systemLogger.LogError($"Task cancelled: {ex.Message}");
                    if (ex.InnerException != null) {
                        _systemLogger.LogError($"Task cancelled: {ex.InnerException.Message}");
                    } else {
                        _systemLogger.LogError($"Task cancelled");
                    }
                } catch (Exception ex) {
                    _systemLogger.LogError($"Error: {ex.Message}");
                } finally {
                    await ReconnectAsync(token);
                }
            }
        }
        public async Task ConnectAsync(CancellationToken token) {
            try {
                _client = new System.Net.Sockets.TcpClient();
                await _client.ConnectAsync(ServerAddress, Port);
                _sslStream = new SslStream(_client.GetStream(), false, new RemoteCertificateValidationCallback(ValidateServerCertificate));
                AuthenticateSsl(_sslStream);
            } catch (Exception ex) {
                _systemLogger.LogError($"Connection error: {ex.Message}");
                throw;
            }
        }
        protected virtual async Task CommunicateAsync(CancellationToken token) {
            using StreamReader reader = new StreamReader(_sslStream);
            using StreamWriter writer = new StreamWriter(_sslStream) { AutoFlush = true };
            var pingSerializer = new XmlSerializer(typeof(ping));
            var pongSerializer = new XmlSerializer(typeof(pong));
            StringBuilder responseBuilder = new StringBuilder();

            while (_client.Connected && !token.IsCancellationRequested) {
                SendPing(writer);

                while (_client.Connected && !token.IsCancellationRequested) {
                    string line = await reader.ReadLineAsync();
                    if (!string.IsNullOrWhiteSpace(line)) {
                        responseBuilder.AppendLine(line);
                        if (line.EndsWith(Separator)) {
                            string response = responseBuilder.ToString();
                            response = response.Replace(Separator, "");
                            _responseLogger.LogInformation($"Received response: {response}", response);

                            try {
                                using (var stringReader = new StringReader(response)) {
                                    if (token.IsCancellationRequested) throw new TaskCanceledException();
                                    ReadPong(pongSerializer, stringReader);
                                }
                            } catch (InvalidOperationException ex) {
                                _systemLogger.LogError($"XML Deserialization error: {ex.Message}");
                                if (ex.InnerException != null) {
                                    _systemLogger.LogError($"Inner exception: {ex.InnerException.Message}");
                                }
                            }
                            responseBuilder.Clear();
                            break;
                        }
                    } else {
                        _systemLogger.LogError("Received empty response or whitespace.");
                    }
                }

                Thread.Sleep(Interval);
            }
            _systemLogger.LogWarning("Client stopped");
        }
        public async Task ReconnectAsync(CancellationToken token) {
            while (!token.IsCancellationRequested) {
                try {
                    _systemLogger.LogInformation("Attempting to reconnect...");
                    await ConnectAsync(token);
                    _systemLogger.LogInformation("Reconnected successfully.");
                    break;
                } catch (Exception ex) {
                    _systemLogger.LogError($"Reconnection failed: {ex.Message}");
                    await Task.Delay(5000, token); // Wait 5 seconds before retrying
                }
            }
        }
        protected void AuthenticateSsl(SslStream sslStream) {
            _systemLogger.LogInformation("Starting SSL handshake...");
            sslStream.AuthenticateAsClient(
                ServerAddress,
                new X509CertificateCollection { ClientCertificate },
                SslProtocols.Tls12 | SslProtocols.Tls13,
                checkCertificateRevocation: true);//true. false for testing
            _systemLogger.LogInformation("SSL handshake completed.");
        }
        protected void SendPing(StreamWriter writer) {
            var pingVar = new ping { timestamp = DateTime.UtcNow };
            var pingMessage = SerializeToXml(pingVar) + Separator;
            writer.WriteLine(pingMessage);
            _systemLogger.LogInformation($"Sent: {pingVar.timestamp}");
        }
        protected void ReadPong(XmlSerializer pongSerializer, StringReader stringReader) {
            var pongVar = (pong)pongSerializer.Deserialize(stringReader);
            var receivedTime = DateTime.UtcNow;
            var sentTime = pongVar.timestamp;
            var deliveryTime = receivedTime - sentTime;
            _responseLogger.LogInformation($"Received: {pongVar.timestamp}, Delivery Time: {deliveryTime.TotalMilliseconds}ms");
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
                    throw new XmlSchemaValidationException(e.Message);
                });
                return true;
            } catch (XmlSchemaValidationException) {
                return false;
            }
        }
        protected static bool ValidateServerCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors) {        
            if (sslPolicyErrors == SslPolicyErrors.None)
                return true;
            throw new Exception($"Certificate validation errors: {sslPolicyErrors}");
            // All certificates are accepted for testing purposes:
            // //Console.WriteLine($"Ignoring certificate validation errors: {sslPolicyErrors}");
            // return true;

        }
        private void LoadConfiguration() {
            try {
                _systemLogger.LogInformation("Loading configuration...");
                var configuration = new ConfigurationBuilder()
                    .SetBasePath(Directory.GetCurrentDirectory())
                    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                    .Build();

                ClientSslPass = configuration["ClientSslPass"];
                if (string.IsNullOrEmpty(ClientSslPass)) {
                    throw new Exception("Client SSL Password was not found in config file");
                }

                string intervalString = configuration["Interval"];
                if (int.TryParse(intervalString, out int interval)) {
                    Interval = interval;
                } else {
                    throw new Exception("Interval in appsettings.json is not a valid integer.");
                }
                _systemLogger.LogInformation("Configuration loaded successfully.");
            } catch (Exception ex) {
                _systemLogger.LogError($"Error loading configuration: {ex.Message}");
                throw;
            }
        }
        private void LoadCertificate() {
            try {
                string certPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ClientCertificate\\client.pfx");

                if (!File.Exists(certPath)) {
                    throw new FileNotFoundException("Certificate file not found", certPath);
                }

                ClientCertificate = new X509Certificate2(
                    certPath,
                    ClientSslPass,
                    X509KeyStorageFlags.MachineKeySet);
                _systemLogger.LogInformation("Certificate loaded successfully.");
            } catch (Exception ex) {
                _systemLogger.LogError($"Error loading certificate: {ex.Message}");
                throw;
            }
        }
        private void LoadXsdSchema() {
            try {
                schemaSet = new XmlSchemaSet();
                string schemaPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "schema.xsd");
                if (!File.Exists(schemaPath)) {
                    throw new FileNotFoundException("XML schema file not found", schemaPath);
                }
                schemaSet.Add("", schemaPath);
                _systemLogger.LogInformation("Schema loaded successfully.");
            } catch (Exception ex) {
                _systemLogger.LogError($"Error loading schema: {ex.Message}");
                throw;
            }
        }
    }
}
