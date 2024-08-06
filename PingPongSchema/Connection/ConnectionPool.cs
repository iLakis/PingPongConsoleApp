using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Net.Sockets;
using System.Security.Authentication;

namespace Utils.Connection {
    public class ConnectionPool: IConnectionPool {
        private readonly ConcurrentQueue<SslStream> _connections = new ConcurrentQueue<SslStream>();
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

        public static async Task<ConnectionPool> CreateAsync(
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

        private async Task InitializeAsync(CancellationToken token) {
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
        private async Task<SslStream> CreateConnectionAsync(CancellationToken token) {
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
            return sslStream;
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
        public async Task<bool> ReconnectAsync(CancellationToken token, SslStream? connection = null) {
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
                    if (attempts >= _maxReconnectAttempts) {
                        _logger.LogError($"Max reconnection attempts reached. Giving up."); //dont add timestamp or test will fail
                        //throw;
                        return false;
                    }
                    await Task.Delay(_reconnectDelay, token);
                }
            }
            return false;
        }
        public async Task<SslStream> GetConnectionAsync(CancellationToken token) {
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
                SslStream newConnection = null;
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
        private bool IsConnectionValid(SslStream connection) {
            try {
                return connection != null && connection.CanRead && connection.CanWrite;
            } catch {
                return false;
            }
        }
        public async Task CloseConnectionAsync(SslStream connection) {
            try {
                if (connection != null) { //&& connection.CanRead && connection.CanWrite) {
                    await connection.ShutdownAsync();
                } else {
                    _logger.LogError($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Attempting to close a null connection.");
                }
            } catch (ObjectDisposedException) {
                // Connection already disposed
            } catch (Exception ex) {
                _logger.LogError($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Error while shutting down connection: {ex.Message}");
            } finally {
                connection?.Dispose();
                _logger.LogWarning($"[{DateTime.UtcNow:HH:mm:ss.fff}]: Connection closed.");
                _semaphore.Release();
            }
        }
        public async Task CloseAllConnectionsAsync() {
            while (_connections.TryDequeue(out var connection)) {
                await CloseConnectionAsync(connection);
            }
            _logger.LogInformation($"[{DateTime.UtcNow:HH:mm:ss.fff}]: All connections closed.");
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
