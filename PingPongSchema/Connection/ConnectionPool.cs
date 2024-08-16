using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Net.Sockets;
using System.Security.Authentication;

namespace Utils.Connection {
    public class ConnectionPool: IConnectionPool {
        private readonly ConcurrentQueue<ClientConnection> _connections = new ConcurrentQueue<ClientConnection>();
        private readonly int _poolSize;
        private readonly ILogger _logger;
        private readonly string _serverAddress;
        private readonly int _port;
        private readonly X509Certificate2 _clientCertificate;
        private readonly SemaphoreSlim _semaphore;
        private readonly int _maxReconnectAttempts;
        private readonly int _reconnectDelay;
        private bool _shouldReconnect = true;
        //private bool _disposed;
        public event EventHandler<ConnectionEventArgs> ConnectionOpened;
        public event EventHandler<ConnectionEventArgs> ConnectionClosed;
        public event EventHandler<ConnectionErrorEventArgs> ConnectionError;

        private ConnectionPool(
            int poolSize,
            string serverAddress,
            int port,
            X509Certificate2 clientCertificate,
            ILogger logger,
            int maxReconnectAttempts,
            int reconnectDelay) {

            _poolSize = poolSize;
            _serverAddress = serverAddress;
            _port = port;
            _clientCertificate = clientCertificate;
            _logger = logger;
            _semaphore = new SemaphoreSlim(poolSize);
            _maxReconnectAttempts = maxReconnectAttempts;
            _reconnectDelay = reconnectDelay;
        }

        public static ConnectionPool Create(
            int poolSize,
            string serverAddress,
            int port,
            X509Certificate2 clientCertificate,
            ILogger logger,
            int maxReconnectAttempts,
            int reconnectDelay)
        {
            return new ConnectionPool(poolSize, serverAddress, port, clientCertificate, logger, maxReconnectAttempts, reconnectDelay);
        }

        /*public static async Task<ConnectionPool> CreateAsync(
            int poolSize,
            string serverAddress,
            int port,
            X509Certificate2 clientCertificate,
            ILogger logger,
            CancellationToken token,
            int maxReconnectAttempts,
            int reconnectDelay) {

            var pool = new ConnectionPool(poolSize, serverAddress, port, clientCertificate, logger, maxReconnectAttempts, reconnectDelay);
            await pool.InitializeAsync(token);
            return pool;
        }
        */
        public async Task InitializeAsync(CancellationToken token) {
            _logger.LogInformation($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Initializing connection pool.");
            for (int i = 0; i < _poolSize; i++) {
                try {
                    var connection = await CreateConnectionAsync(token);
                    if (!IsConnectionValid(connection) && _shouldReconnect) {
                        _shouldReconnect = await ReconnectAsync(token, connection);
                    }
                    _connections.Enqueue(connection);
                } catch (Exception ex) {
                    _logger.LogError($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Error while creating a connection: {ex.Message}");
                }
            }
            _logger.LogInformation("Connection pool initialized.");
        }
        private async Task<ClientConnection> CreateConnectionAsync(CancellationToken token) {
            try {
                token.ThrowIfCancellationRequested();
                var tcpClient = new TcpClient();
                await tcpClient.ConnectAsync(_serverAddress, _port, token);
                var sslStream = new SslStream(
                    tcpClient.GetStream(),
                    false,
                    new RemoteCertificateValidationCallback(ValidateServerCertificate));

                /*sslStream.AuthenticateAsClient(
                    _serverAddress, 
                    new X509CertificateCollection { _clientCertificate }, 
                    SslProtocols.Tls12 | SslProtocols.Tls13, true);
                */
                AuthenticateSsl(sslStream);
                _logger.LogInformation($"[{DateTime.UtcNow:HH:mm:ss.fff}]: New connection created.");
                var connection = new ClientConnection(tcpClient, sslStream);
                OnConnectionOpened(connection);
                return connection;
            } catch (Exception ex) {
                OnConnectionError(null, ex); 
                throw;
            }
        }
        protected void AuthenticateSsl(SslStream sslStream) {
            _logger.LogInformation($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Starting SSL handshake...");
            sslStream.AuthenticateAsClient(
                _serverAddress,
                new X509CertificateCollection { _clientCertificate },
                SslProtocols.Tls12 | SslProtocols.Tls13,
                checkCertificateRevocation: true);//true. false for testing
            _logger.LogInformation($"[{DateTime.UtcNow:HH:mm:ss.fff}]: SSL handshake completed.");
        }
        public async Task<bool> ReconnectAsync(CancellationToken token, ClientConnection? connection = null) {
            int attempts = 0;
            while (attempts < _maxReconnectAttempts) {
                token.ThrowIfCancellationRequested();
                try {
                    _logger.LogInformation($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Attempting to reconnect...");
                    connection = await CreateConnectionAsync(token);
                    if (IsConnectionValid(connection)) {
                        _logger.LogInformation($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Reconnected successfully.");
                        return true;
                    }
                    throw new Exception("Connection is not valid");
                } catch (Exception ex) {
                    _logger.LogError($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Attempt {attempts + 1} to reconnect failed: {ex.Message}");
                    attempts++;
                    //OnConnectionError(connection, ex);
                    if (attempts >= _maxReconnectAttempts) {
                        _logger.LogError($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Max reconnection attempts reached. Giving up."); //dont add timestamp or test will fail
                        //throw;
                        return false;
                    }
                    await Task.Delay(_reconnectDelay, token);
                }
            }
            return false;
        }
        public async Task<ClientConnection> GetConnectionAsync(CancellationToken token) {
            await _semaphore.WaitAsync(token);
            //_logger.LogInformation("Attempting to retrieve connection from pool...");
            while (_connections.TryDequeue(out var connection)) {
                if (IsConnectionValid(connection)) {
                    //_logger.LogInformation("Connection retrieved from pool.");
                    return connection;
                } else {
                    _logger.LogWarning($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Invalid connection detected");//+
                        //$". Creating a new one.");
                    await CloseConnectionAsync(connection);
                    // return await CreateConnectionAsync(token);
                }
            }
            _logger.LogWarning($"[{DateTime.UtcNow:HH:mm:ss.fff}]: No available connection. Trying to reconnect"); //+
                   // $"Creating a new one.");
                ClientConnection newConnection = null;
                if(!ReconnectAsync(token, newConnection).Result) {
                    throw new Exception("Reconnection failed");
                }
                return newConnection;
            
        }
        private bool ValidateServerCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors) {
            return sslPolicyErrors == SslPolicyErrors.None;

            // All certificates are accepted for testing purposes:
            // _logger.LogError($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Ignoring certificate validation errors: {sslPolicyErrors}");
            // return true;
        }
        private bool IsConnectionValid(ClientConnection connection) {
            try {
                return 
                    connection.SslStream != null && 
                    connection.SslStream.CanRead && 
                    connection.SslStream.CanWrite;
            } catch {
                return false;
            }
        }
        public async Task CloseConnectionAsync(ClientConnection connection) {
            try {
                if (connection != null) { //&& connection.CanRead && connection.CanWrite) {
                    await connection.SslStream.ShutdownAsync();
                } else {
                    _logger.LogError($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Attempting to close a null connection.");
                }
            } catch (ObjectDisposedException) {
                // Connection already disposed
            } catch (Exception ex) {
                _logger.LogError($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Error while shutting down connection: {ex.Message}");
                OnConnectionError(connection, ex);
            } finally {
                connection.SslStream?.Dispose();
                _logger.LogWarning($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Connection closed.");
                OnConnectionClosed(connection);
                _semaphore.Release();
            }
        }
        public async Task CloseAllConnectionsAsync() {
            while (_connections.TryDequeue(out var connection)) {
                await CloseConnectionAsync(connection);
            }
            _logger.LogInformation($"[{DateTime.UtcNow:HH:mm:ss.fff}]: All connections closed.");
        }
        private void OnConnectionOpened(ClientConnection connection) {
            //_logger.LogInformation($"    TEST [{DateTime.UtcNow:HH:mm:ss.fff}]: Invoking ConnectionOpened event...");
            ConnectionOpened?.Invoke(this, new ConnectionEventArgs(connection));
        }
        private void OnConnectionClosed(ClientConnection connection) {
            //_logger.LogInformation($"    TEST [{DateTime.UtcNow:HH:mm:ss.fff}]: Invoking ConnectionClosed event...");
            ConnectionClosed?.Invoke(this, new ConnectionEventArgs(connection));
        }
        private void OnConnectionError(ClientConnection connection, Exception ex) {
            //_logger.LogInformation($"    TEST [{DateTime.UtcNow:HH:mm:ss.fff}]: Invoking ConnectionError event...");
            ConnectionError?.Invoke(this, new ConnectionErrorEventArgs(connection, ex));
        }
        /* public async Task ReturnConnectionToPool(SslStream connection, CancellationToken token) {
             try {
                 token.ThrowIfCancellationRequested();
                 _logger.LogInformation($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Returning connection to pool.");
                 if (IsConnectionValid(connection)) {
                     _connections.Enqueue(connection);
                     _logger.LogInformation($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Connection returned to pool.");
                 } else {
                     _logger.LogWarning($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Connection is not valid and cannot be returned to pool.");//+
                         //$" Creating a new one.");
                     await CloseConnectionAsync(connection); // or _=
                     //var newConnection = await CreateConnectionAsync(token);
                     //_connections.Enqueue(newConnection);
                     //_logger.LogInformation($"[{DateTime.UtcNow:HH:mm:ss.fff}]: New connection added to pool.");
                 }
                 _semaphore.Release();
             } catch (OperationCanceledException ex){
                 _logger.LogError($"[{DateTime.UtcNow:HH: mm: ss.fff}]: Returning connection to pool canceled");
             }

         }*/

    }
}
