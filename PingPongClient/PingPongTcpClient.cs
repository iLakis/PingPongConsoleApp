using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml.Schema;
using System.Xml.Serialization;
using Utils;
using Utils.Configs;
using Utils.Configs.Client;
using Utils.Connection;


namespace PingPongClient
{
    public class PingPongTcpClient {
        protected X509Certificate2? _clientCertificate;
        protected XmlSchemaSet? _schemaSet;
        protected DefaultClientConfig? _config;
        protected SslStream? _currentConnection;
        protected readonly ILogger _systemLogger;
        protected readonly ILogger _responseLogger;
        protected IConnectionPool _connectionPool;

        public PingPongTcpClient(ILogger systemLogger, ILogger responseLogger, IConfigLoader<DefaultClientConfig>? configLoader = null) {
            _systemLogger = systemLogger;
            _responseLogger = responseLogger;
            if (configLoader == null) {
                _systemLogger.LogWarning("No configuration loader provided, using default JsonConfigLoader.");
                configLoader = new JsonConfigLoader<DefaultClientConfig>(Path.Combine(Directory.GetCurrentDirectory(), "appsettings.client.json"), _systemLogger);
            }
            _config = configLoader.LoadConfig();            

            try {
                LoadCertificate();
                LoadXsdSchema();
            } catch (Exception ex) {
                _systemLogger.LogError($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Error in TcpClient constructor: {ex.Message}");
                throw;
            }          
        }
        public async Task StartAsync(CancellationToken token) {
            _connectionPool = await ConnectionPool.CreateAsync(2, _config.ServerAddress, _config.Port, _clientCertificate, _systemLogger, token, _config.MaxReconnectAttempts, _config.ReconnectDelay);
            try {
                while (!token.IsCancellationRequested) {              
                    try {
                        await GetConnectionFromPoolAsync(token);
                        _systemLogger.LogInformation("Got connection from pool.");
                        await CommunicateAsync(_currentConnection, token);
                    } catch (AuthenticationException ex) {
                        _systemLogger.LogError($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Authentication failed: {ex.Message}");
                        if (ex.InnerException != null) {
                            _systemLogger.LogError($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Inner exception: {ex.InnerException.Message}");
                        }
                    } catch (ObjectDisposedException ex) {
                        _systemLogger.LogError($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Object Disposed error: {ex.Message}");
                    } catch (IOException ioEx) {
                        _systemLogger.LogError($"[{DateTime.UtcNow:HH:mm:ss.fff}]: IO error: {ioEx.Message}");
                        if (ioEx.InnerException != null) {
                            _systemLogger.LogError($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Inner exception: {ioEx.InnerException.Message}");
                        }
                    } catch (OperationCanceledException ex) { // cancellation token throws this, not TaskCancelledException
                        if (ex.InnerException != null) {
                            _systemLogger.LogWarning($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Task cancelled: {ex.InnerException.Message}");
                        } else {
                            _systemLogger.LogWarning($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Task cancelled");
                        }
                    } catch (Exception ex) {
                        _systemLogger.LogError($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Error during communication: {ex.Message}");
                    } finally {
                        var brokenConnection = _currentConnection;
                        await SwapConnectionAsync(token);
                        _ = _connectionPool.ReturnConnectionToPool(brokenConnection, token); // can switch to await to debug. _ = does not block the thread
                    }
                }
            } finally {
                //await _connectionPool.CloseAllConnectionsAsync();
                DisconnectAllConnections();
            }
        }
        public async Task GetConnectionFromPoolAsync(CancellationToken token) {
            token.ThrowIfCancellationRequested();
            try {
                var connection = await _connectionPool.GetConnectionAsync(token);
                _currentConnection = connection;
                _currentConnection.ReadTimeout = _config.ReadTimeout;
                _currentConnection.WriteTimeout = _config.WriteTimeout;
                //_systemLogger.LogInformation("Got connection from pool.");
            } catch (Exception ex) {
                _systemLogger.LogError($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Connection error: {ex.Message}");
                throw;
            }
        }
        protected virtual async Task CommunicateAsync(SslStream connection, CancellationToken token) {
            using StreamReader reader = new StreamReader(connection); //_sslStream
            using StreamWriter writer = new StreamWriter(connection) { AutoFlush = true };
            var pingSerializer = new XmlSerializer(typeof(ping));
            var pongSerializer = new XmlSerializer(typeof(pong));
            StringBuilder responseBuilder = new StringBuilder();
            Stopwatch stopwatch = new Stopwatch();

            while (connection.CanRead && connection.CanWrite && !token.IsCancellationRequested) { //_client.Connected
                token.ThrowIfCancellationRequested();
                SendPing(writer);

                stopwatch.Restart();
                bool pongReceived = false;

                while (connection.CanRead && connection.CanWrite && !token.IsCancellationRequested) { //_client.Connected
                    token.ThrowIfCancellationRequested();
                    string line = await reader.ReadLineAsync();
                    if (!string.IsNullOrWhiteSpace(line)) {
                        responseBuilder.AppendLine(line);
                        if (line.EndsWith(_config.Separator)) {
                            string response = responseBuilder.ToString();
                            response = response.Replace(_config.Separator, "");
                            _responseLogger.LogInformation($"Received response: {response}", response);

                            try {
                                using (var stringReader = new StringReader(response)) {
                                    token.ThrowIfCancellationRequested();
                                    ReadPong(pongSerializer, stringReader);
                                    pongReceived = true;
                                }
                            } catch (InvalidOperationException ex) {
                                _systemLogger.LogError($"[{DateTime.UtcNow:HH:mm:ss.fff}]: XML Deserialization error: {ex.Message}");
                                if (ex.InnerException != null) {
                                    _systemLogger.LogError($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Inner exception: {ex.InnerException.Message}");
                                }
                            }
                            responseBuilder.Clear();
                            break;
                        }
                    } else {
                        _systemLogger.LogError("Received empty response or whitespace.");
                        DisconnectCurrentConnection();
                        //return;
                    }
                }
                stopwatch.Stop();
                if (!pongReceived) {
                    _systemLogger.LogWarning("Pong not received within the expected time frame.");
                    await HandleTimeoutAsync(token);
                } else {
                    var latency = stopwatch.ElapsedMilliseconds;
                    _responseLogger.LogInformation($"Latency: {latency}ms");
                    //AdjustBehaviorBasedOnLatency(latency);
                }
                await Task.Delay(_config.Interval, token);
            }
            //_systemLogger.LogWarning("Client stopped");
        }

        protected async Task HandleTimeoutAsync(CancellationToken token) {
            _systemLogger.LogWarning("Handling timeout - attempting to reconnect...");
            await SwapConnectionAsync(token);
        }
        protected virtual void AdjustBehaviorBasedOnLatency(long latency) { 
            if (latency > _config.HighLatencyThreshold) { 
                _systemLogger.LogWarning("High latency detected, increasing interval and timeouts.");
                _config.Interval = Math.Min(_config.Interval + 100, _config.MaxInterval);
                _config.ReadTimeout = Math.Min(_config.ReadTimeout + 1000, _config.MaxReadTimeout);
                _config.WriteTimeout = Math.Min(_config.WriteTimeout + 1000, _config.MaxWriteTimeout); 

            } else if (latency < _config.LowLatencyThreshold) {
                _systemLogger.LogInformation("Low latency detected, decreasing interval and timeouts.");
                _config.Interval = Math.Max(_config.Interval - 50, _config.MinInterval);
                _config.ReadTimeout = Math.Max(_config.ReadTimeout - 500, _config.MinReadTimeout);
                _config.WriteTimeout = Math.Max(_config.WriteTimeout - 500, _config.MinWriteTimeout); 
            }
            _currentConnection.ReadTimeout = _config.ReadTimeout;
            _currentConnection.WriteTimeout = _config.WriteTimeout;
        }
        public async Task SwapConnectionAsync(CancellationToken token) {
            try {
                _systemLogger.LogInformation("Swapping connection...");
                await GetConnectionFromPoolAsync(token);
                _systemLogger.LogInformation("Connection swapped successfully.");
                return; 
            } catch (OperationCanceledException ex) {
                _systemLogger.LogWarning($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Connection swap canceled");
            } catch (Exception ex) {
                _systemLogger.LogError($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Error during connection swap: {ex.Message}");
            }
            return;
        }
        protected void SendPing(StreamWriter writer) {
            var pingVar = new ping { timestamp = DateTime.UtcNow };           
            var pingMessage = XmlTools.SerializeToXml(pingVar) + _config.Separator;
            writer.WriteLine(pingMessage);
            _systemLogger.LogInformation($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Sent: {pingVar.timestamp}");
        }
        protected void ReadPong(XmlSerializer pongSerializer, StringReader stringReader) {
            var pongVar = (pong)pongSerializer.Deserialize(stringReader);
            var receivedTime = DateTime.UtcNow;
            var sentTime = pongVar.timestamp;
            var deliveryTime = receivedTime - sentTime;
            _responseLogger.LogInformation($"Received: {pongVar.timestamp}, Delivery Time: {deliveryTime.TotalMilliseconds}ms");
        }
        protected void LoadCertificate() {
            try {
                string certPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ClientCertificate\\client.pfx");

                if (!File.Exists(certPath)) {
                    throw new FileNotFoundException("Certificate file not found", certPath);
                }

                _clientCertificate = new X509Certificate2(
                    certPath,
                    _config.SslPass,
                    X509KeyStorageFlags.MachineKeySet);
                _systemLogger.LogInformation("Certificate loaded successfully.");
            } catch (Exception ex) {
                _systemLogger.LogError($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Error loading certificate: {ex.Message}");
                throw;
            }
        }
        protected void LoadXsdSchema() {
            try {
                _schemaSet = new XmlSchemaSet();
                string schemaPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "schema.xsd");
                if (!File.Exists(schemaPath)) {
                    throw new FileNotFoundException("XML schema file not found", schemaPath);
                }
                _schemaSet.Add("", schemaPath);
                _systemLogger.LogInformation("Schema loaded successfully.");
            } catch (Exception ex) {
                _systemLogger.LogError($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Error loading schema: {ex.Message}");
                throw;
            }
        }
        public void DisconnectCurrentConnection() {
            _systemLogger.LogWarning("Disconnecting current connection of the client.");
            if (_currentConnection != null) {
                _connectionPool.CloseConnectionAsync(_currentConnection).Wait();
            } else {
                _systemLogger.LogError("No current connection to disconnect.");
            }
        }
        public void DisconnectAllConnections() {
            _systemLogger.LogWarning("Disconnecting all connections of the client.");
            _connectionPool?.CloseAllConnectionsAsync().Wait();
        }

    }
}
