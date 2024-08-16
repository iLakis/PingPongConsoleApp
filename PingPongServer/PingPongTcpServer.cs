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
using Utils.Connection;

namespace PingPongServer
{
    public class PingPongTcpServer {
        protected static X509Certificate2 ServerCertificate;
        protected static XmlSchemaSet schemaSet;
        protected IServerConfig _config;
        protected readonly ILogger<PingPongTcpServer> _logger;

        public event EventHandler<ConnectionEventArgs> ConnectionOpened;
        public event EventHandler<ConnectionEventArgs> ConnectionClosed;
        public event EventHandler<ConnectionErrorEventArgs> ConnectionError;
        private readonly Dictionary<Guid, ClientConnection> _activeConnections = new Dictionary<Guid, ClientConnection>();

        public PingPongTcpServer(ILogger<PingPongTcpServer> logger, IConfigLoader<DefaultServerConfig> configLoader = null) {
            _logger = logger;
            if (configLoader == null) {
                _logger.LogWarning($"[{DateTime.UtcNow:HH:mm:ss.fff}]: No configuration loader provided, using default JsonConfigLoader.");
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
            ThreadPool.SetMinThreads(100, 100); //TODO move to config
            TcpListener listener = new TcpListener(IPAddress.Any, _config.Port);
            listener.Start();
            _logger.LogInformation($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Server started on port {_config.Port}");

            try {
                while (!token.IsCancellationRequested) {
                    if (listener.Pending()) {
                        TcpClient client = listener.AcceptTcpClient();
                        var sslStream = new SslStream(client.GetStream(), false);
                        _logger.LogInformation($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Client connected");
                        var connection = new ClientConnection(client, sslStream);
                        OnConnectionOpened(connection);
                        _ = Task.Run(() => HandleClientAsync(connection, token));
                    } else {
                        await Task.Delay(100, token);
                    }
                }
            } finally {
                listener.Stop();
                _logger.LogWarning($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Server stopped");
            }
        }
        /*public async Task StartAsync(CancellationToken token) {
            ThreadPool.SetMinThreads(100, 100);
            TcpListener listener = new TcpListener(IPAddress.Any, _config.Port);
            listener.Start();
            _logger.LogInformation($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Server started on port {_config.Port}");

            try {
                while (!token.IsCancellationRequested) {
                    TcpClient client = await listener.AcceptTcpClientAsync(); // Асинхронное ожидание нового подключения
                    _logger.LogInformation($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Client connected");
                    _ = Task.Run(() => HandleClientAsync(client, token)); // Обработка клиента в отдельной задаче
                }
            } finally {
                listener.Stop();
                _logger.LogWarning($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Server stopped");
            }
        }*/


        protected virtual async Task HandleClientAsync(ClientConnection connection, CancellationToken token) {
            var sslStream = new SslStream(connection.TcpClient.GetStream(), false);
            sslStream.ReadTimeout = _config.ReadTimeout;
            sslStream.WriteTimeout = _config.WriteTimeout;
            try {
                await AuthenticateSsl(sslStream);

                using (StreamReader reader = new StreamReader(sslStream))
                using (StreamWriter writer = new StreamWriter(sslStream) { AutoFlush = true }) {
                    var pingSerializer = new XmlSerializer(typeof(ping));
                    var pongSerializer = new XmlSerializer(typeof(pong));
                    StringBuilder messageBuilder = new StringBuilder();

                    while (connection.TcpClient.Connected && !token.IsCancellationRequested) {
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
                                        ReadPing(connection, stringReader, pingSerializer);
                                        token.ThrowIfCancellationRequested();
                                        SendPong(connection, writer);
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
                            _logger.LogWarning($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Received empty message or whitespace.");
                            break; // Exit the loop if the message is null
                        }
                    }
                    _logger.LogWarning($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Client disconnected.");
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
                OnConnectionError(connection, ioEx);
            } catch (TaskCanceledException ex) {
                _logger.LogWarning($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Task cancelled");
                
            } catch (Exception ex) {
                _logger.LogInformation($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Error: {ex.Message}");
                if (ex.InnerException != null) {
                    _logger.LogError($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Inner exception: {ex.InnerException.Message}");
                }
                OnConnectionError(connection, ex);
            } finally {
                connection.SslStream.Close();
                connection.TcpClient.Close(); // Ensure the client is closed on error
                _logger.LogWarning($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Client connection closed.");
                OnConnectionClosed(connection);
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
            _logger.LogInformation($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Certificate loaded successfully.");
        }
        protected void LoadXsdSchema() {
            schemaSet = new XmlSchemaSet();
            string schemaPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "schema.xsd");
            if (!File.Exists(schemaPath)) {
                throw new FileNotFoundException("XML schema file not found", schemaPath);
            }
            schemaSet.Add("", schemaPath);
            _logger.LogInformation($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Schema loaded successfully.");
        }
        protected async Task AuthenticateSsl(SslStream sslStream) {
            //Console.WriteLine("Authenticating SSL...");
            _logger.LogInformation($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Authenticating SSL...");
            await sslStream.AuthenticateAsServerAsync(
                ServerCertificate,
                clientCertificateRequired: false,
                enabledSslProtocols: SslProtocols.Tls12 | SslProtocols.Tls13,
                checkCertificateRevocation: false); //true. false for testing 
            //Console.WriteLine("SSL authentication succeeded.");
            _logger.LogInformation($"[{DateTime.UtcNow:HH:mm:ss.fff}]: SSL authentication succeeded.");
        }
        protected void SendPong(ClientConnection connection, StreamWriter writer) {
            var pongVar = new pong { timestamp = DateTime.UtcNow };
            var pongMessage = Utils.XmlTools.SerializeToXml(pongVar) + _config.Separator;
            writer.WriteLine(pongMessage);
            _logger.LogInformation($"Sent: {pongVar.timestamp} to {connection.Id}");
        }
        protected void ReadPing(ClientConnection connection, StringReader stringReader, XmlSerializer pingSerializer) {
            var pingVar = (ping)pingSerializer.Deserialize(stringReader);
            var receivedTime = DateTime.UtcNow;
            var sentTime = pingVar.timestamp;
            var deliveryTime = receivedTime - sentTime;
            _logger.LogInformation($"Received: {pingVar.timestamp} from {connection.Id}, Delivery Time: {deliveryTime.TotalMilliseconds}ms");
        }
        protected virtual void OnConnectionOpened(ClientConnection connection) {
            _activeConnections[connection.Id] = connection; 
            _logger.LogInformation($"Connection {connection.Id} opened. Total connections: {_activeConnections.Count}");
            ConnectionOpened?.Invoke(this, new ConnectionEventArgs(connection));
        }
        protected virtual void OnConnectionClosed(ClientConnection connection) {
            _activeConnections.Remove(connection.Id); 
            _logger.LogInformation($"Connection {connection.Id} closed. Total connections: {_activeConnections.Count}");
            ConnectionClosed?.Invoke(this, new ConnectionEventArgs(connection));
        }
        protected virtual void OnConnectionError(ClientConnection connection, Exception ex) {
            _logger.LogError($"Connection {connection.Id} encountered an error: {ex.Message}");
            ConnectionError?.Invoke(this, new ConnectionErrorEventArgs(connection, ex));
        }
        public void DisconnectClient(Guid connectionId) {
            if (_activeConnections.TryGetValue(connectionId, out var connection)) {
                connection.TcpClient.Close();
                _logger.LogInformation($"Disconnected client {connectionId}");
            } else {
                _logger.LogWarning($"Trying to close client with ID {connectionId}, not found.");
            }
        }
    }
}
