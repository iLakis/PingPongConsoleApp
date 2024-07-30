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

namespace PingPongServer {
    public class TcpServer {
        protected const int Port = 5001;
        protected string ServerSslPass;
        protected static X509Certificate2 ServerCertificate;
        protected static XmlSchemaSet schemaSet;
        protected const string Separator = "<EOF>";
        private int _readTimeout = 5000;
        private int _writeTimeout = 5000;

        protected readonly ILogger<TcpServer> _logger;
        protected IConfiguration _configuration;

        public TcpServer(ILogger<TcpServer> logger, IConfiguration? configuration = null) {
            _logger = logger;
            if (configuration != null) {
                _configuration = configuration;
            }
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
            sslStream.ReadTimeout = _readTimeout;
            sslStream.WriteTimeout = _writeTimeout;
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
                            if (line.EndsWith(Separator)) {
                                string message = messageBuilder.ToString();
                                message = message.Replace(Separator, "");
                                _logger.LogInformation($"Received message: {message}", message);
                                try {
                                    using (var stringReader = new StringReader(message)) {
                                        token.ThrowIfCancellationRequested();
                                        ReadPing(stringReader, pingSerializer);
                                        token.ThrowIfCancellationRequested();
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
            } catch (OperationCanceledException ex) {
                _logger.LogWarning($"Task cancelled");
                
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
            List<string> usingDefaults = new List<string>();
            try {
                _logger.LogInformation("Loading configuration...");
                if (_configuration == null) {
                    _logger.LogInformation("Configuration file not given, searching for the file");
                    _configuration = new ConfigurationBuilder()
                                        .SetBasePath(Directory.GetCurrentDirectory())
                                        .AddJsonFile("appsettings.server.json", optional: false, reloadOnChange: true)
                                        .Build();
                }

                var configParams = new Dictionary<string, Action<string>> {
                    { "ServerSslPass", value => ServerSslPass = value },
                    { "ReadTimeout", value => TryParseInt(value, "ReadTimeout", v => _readTimeout = v, usingDefaults) },
                    { "WriteTimeout", value => TryParseInt(value, "WriteTimeout", v => _writeTimeout = v, usingDefaults) }
                };

                foreach (var param in configParams) {
                    var value = _configuration[param.Key];
                    if (!string.IsNullOrEmpty(value)) {
                        param.Value(value);
                    } else {
                        _logger.LogError($"{param.Key} was not found in config file");
                        usingDefaults.Add(param.Key);
                    }
                }

                if (usingDefaults.Count > 0) {                  
                    _logger.LogWarning($"Configuration loaded with errors: Could not find or parse: {string.Join(", ", usingDefaults)}");
                    _logger.LogWarning("Using default values for missing variables.");
                } else {
                    _logger.LogInformation("Configuration loaded successfully.");
                }
            } catch (KeyNotFoundException ex) {
                _logger.LogError($"Variable not found in the config file: {ex.Message}");
                throw;
            } catch (FormatException ex) {
                _logger.LogError($"Wrong format while parsing config file: {ex.Message}");
                throw;
            } catch (Exception ex) {
                _logger.LogError($"Error loading configuration: {ex.Message}");
                throw;
            }
        }
        private void TryParseInt(string value, string paramName, Action<int> setValue, List<string> usingDefaults) {
            if (int.TryParse(value, out int result)) {
                setValue(result);
            } else {
                _logger.LogError($"{paramName} in config is not a valid integer.");
                usingDefaults.Add(paramName);
            }
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
            var pongMessage = Utils.XmlTools.SerializeToXml(pongVar) + Separator;
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
    }
}
