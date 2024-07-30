﻿using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml.Schema;
using System.Xml.Serialization;
using Utils;


namespace PingPongClient {
    public class TcpClient {
        protected static X509Certificate2 ClientCertificate;
        protected static XmlSchemaSet schemaSet;
        protected DefaultClientConfig _config;


        protected System.Net.Sockets.TcpClient _client;
        protected SslStream _sslStream;
        protected readonly ILogger _systemLogger;
        protected readonly ILogger _responseLogger;
        protected IConfiguration _configuration;

        public TcpClient(ILogger systemLogger, ILogger responseLogger, IConfigLoader<DefaultClientConfig>? configLoader = null) {
            _systemLogger = systemLogger;
            _responseLogger = responseLogger;
            if (configLoader == null) {
                _systemLogger.LogWarning("No configuration loader provided, using default JsonConfigLoader.");
                configLoader = new JsonConfigLoader<DefaultClientConfig>(Path.Combine(Directory.GetCurrentDirectory(), "appsettings.client.json"), _systemLogger);
            }
            _config = configLoader.LoadConfig();
            try {
                //LoadConfiguration();
                LoadCertificate();
                LoadXsdSchema();
            } catch (Exception ex) {
                _systemLogger.LogError($"Error in TcpClient constructor: {ex.Message}");
                throw;
            }
        }
        public async Task StartAsync(CancellationToken token) {
            bool shouldReconnect = true; // without this bool reconnection will not stop at maxReconnectionAttempts, the cycle will repeat
            while (!token.IsCancellationRequested && shouldReconnect) {
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
                } catch (OperationCanceledException ex) { // cancellation token throws this, not TaskCancelledException
                    if (ex.InnerException != null) {
                        _systemLogger.LogError($"Task cancelled: {ex.InnerException.Message}");
                    } else {
                        _systemLogger.LogError($"Task cancelled");
                    }
                } catch (Exception ex) {
                    _systemLogger.LogError($"Error: {ex.Message}");
                } finally {
                    shouldReconnect = await ReconnectAsync(token);
                }
            }
        }
        public async Task ConnectAsync(CancellationToken token) {
            token.ThrowIfCancellationRequested();
            try {
                _client = new System.Net.Sockets.TcpClient();
                await _client.ConnectAsync(_config.ServerAddress, _config.Port);
                _sslStream = new SslStream(
                    _client.GetStream(),
                    false,
                    new RemoteCertificateValidationCallback(ValidateServerCertificate));
                AuthenticateSsl(_sslStream);
                _sslStream.ReadTimeout = _config.ReadTimeout;
                _sslStream.WriteTimeout = _config.WriteTimeout;
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
            Stopwatch stopwatch = new Stopwatch();

            while (_client.Connected && !token.IsCancellationRequested) {
                token.ThrowIfCancellationRequested();
                SendPing(writer);

                stopwatch.Restart();
                bool pongReceived = false;

                while (_client.Connected && !token.IsCancellationRequested) {
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
                        Disconnect();
                    }
                }
                stopwatch.Stop();
                if (!pongReceived) {
                    _systemLogger.LogWarning("Pong not received within the expected time frame.");
                    await HandleTimeoutAsync(token);
                } else {
                    var latency = stopwatch.ElapsedMilliseconds;
                    _responseLogger.LogInformation($"Latency: {latency}ms");
                    AdjustBehaviorBasedOnLatency(latency);
                }
                await Task.Delay(_config.Interval, token);
            }
            _systemLogger.LogWarning("Client stopped");
        }

        private async Task HandleTimeoutAsync(CancellationToken token) {
            _systemLogger.LogWarning("Handling timeout - attempting to reconnect...");
            await ReconnectAsync(token);
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
            _sslStream.ReadTimeout = _config.ReadTimeout;
            _sslStream.WriteTimeout = _config.WriteTimeout;
        }
        public async Task<bool> ReconnectAsync(CancellationToken token) {
            int attempts = 0;
            while (attempts < _config.MaxReconnectAttempts) {
                token.ThrowIfCancellationRequested();
                try {
                    _systemLogger.LogInformation("Attempting to reconnect...");
                    await ConnectAsync(token);
                    _systemLogger.LogInformation("Reconnected successfully.");
                    return true; // else it will go till the end and return false, which means it won't try to reconnect again if disconnected
                } catch (Exception ex) {
                    _systemLogger.LogError($"Reconnection attempt {attempts + 1} failed: {ex.Message}");
                    attempts++;
                    if (attempts >= _config.MaxReconnectAttempts) {
                        _systemLogger.LogError("Max reconnection attempts reached. Giving up.");
                        break;
                    }
                    await Task.Delay(_config.ReconnectDelay, token); 
                }
            }
            return false;
        }
        protected void AuthenticateSsl(SslStream sslStream) {
            _systemLogger.LogInformation("Starting SSL handshake...");
            sslStream.AuthenticateAsClient(
                _config.ServerAddress,
                new X509CertificateCollection { ClientCertificate },
                SslProtocols.Tls12 | SslProtocols.Tls13,
                checkCertificateRevocation: true);//true. false for testing
            _systemLogger.LogInformation("SSL handshake completed.");
        }
        protected void SendPing(StreamWriter writer) {
            var pingVar = new ping { timestamp = DateTime.UtcNow };           
            var pingMessage = XmlTools.SerializeToXml(pingVar) + _config.Separator;
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
        protected static bool ValidateServerCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors) {        
            if (sslPolicyErrors == SslPolicyErrors.None)
                return true;
            throw new Exception($"Certificate validation errors: {sslPolicyErrors}");
            // All certificates are accepted for testing purposes:
            // //Console.WriteLine($"Ignoring certificate validation errors: {sslPolicyErrors}");
            // return true;

        }
       /* protected void LoadConfiguration() {
            List<string> usingDefaults = new List<string>();
            try {
                _systemLogger.LogInformation("Loading configuration...");
                if(_configuration == null) {
                    _systemLogger.LogInformation("Configuration file not given, searching for the file");
                    var configuration = new ConfigurationBuilder()
                                        .SetBasePath(Directory.GetCurrentDirectory())
                                        .AddJsonFile("appsettings.client.json", optional: false, reloadOnChange: true)
                                        .Build();
                    _configuration = configuration;
                }

                var configParams = new Dictionary<string, Action<string>> {
                    { "ClientSslPass", value => ClientSslPass = value },
                    { "Interval", value => TryParseInt(value, "Interval", v => Interval = v, usingDefaults) },
                    { "MaxReconnectAttempts", value => TryParseInt(value, "MaxReconnectAttempts", v => _maxReconnectAttempts = v, usingDefaults) },
                    { "ReconnectDelay", value => TryParseInt(value, "ReconnectDelay", v => _reconnectDelay = v, usingDefaults) },
                    { "ReadTimeout", value => TryParseInt(value, "ReadTimeout", v => _readTimeout = v, usingDefaults) },
                    { "WriteTimeout", value => TryParseInt(value, "WriteTimeout", v => _writeTimeout = v, usingDefaults) },
                    { "HighLatencyThresholdMs", value => TryParseInt(value, "HighLatencyThresholdMs", v => HighLatencyThresholdMs = v, usingDefaults) },
                    { "LowLatencyThresholdMs", value => TryParseInt(value, "LowLatencyThresholdMs", v => LowLatencyThresholdMs = v, usingDefaults) },
                    { "MaxReadTimeoutMs", value => TryParseInt(value, "MaxReadTimeout", v => MaxReadTimeoutMs = v, usingDefaults) },
                    { "MinReadTimeoutMs", value => TryParseInt(value, "MinReadTimeout", v => MinReadTimeoutMs = v, usingDefaults) },
                    { "MaxWriteTimeoutMs", value => TryParseInt(value, "MaxWriteTimeout", v => MaxWriteTimeoutMs = v, usingDefaults) },
                    { "MinWriteTimeoutMs", value => TryParseInt(value, "MinWriteTimeout", v => MinWriteTimeoutMs = v, usingDefaults) },
                    { "MaxIntervalMs", value => TryParseInt(value, "MaxInterval", v => MaxIntervalMs = v, usingDefaults) },
                    { "MinIntervalMs", value => TryParseInt(value, "MinInterval", v => MinIntervalMs = v, usingDefaults) }
                };
                foreach (var param in configParams) {
                    var value = _configuration[param.Key];
                    if (!string.IsNullOrEmpty(value)) {
                        param.Value(value);
                    } else {
                        _systemLogger.LogError($"{param.Key} was not found in config file");
                        usingDefaults.Add(param.Key);
                    }
                }

                if (usingDefaults.Count > 0) {
                    _systemLogger.LogWarning($"Configuration loaded with errors: Could not find or parse: {string.Join(", ", usingDefaults)}");
                    _systemLogger.LogWarning("Using default values for missing variables.");
                } else {
                    _systemLogger.LogInformation("Configuration loaded successfully.");
                }
            } catch (KeyNotFoundException ex) {
                _systemLogger.LogError($"Variable not found in the config file: {ex.Message}");
                throw;                        
            } catch (FormatException ex) {
                _systemLogger.LogError($"Wrong format while parsing config file: {ex.Message}");
                throw;
            } catch (Exception ex) {
                _systemLogger.LogError($"Error loading configuration: {ex.Message}");
                throw;
            }
        }*/
        protected void LoadCertificate() {
            try {
                string certPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ClientCertificate\\client.pfx");

                if (!File.Exists(certPath)) {
                    throw new FileNotFoundException("Certificate file not found", certPath);
                }

                ClientCertificate = new X509Certificate2(
                    certPath,
                    _config.SslPass,
                    X509KeyStorageFlags.MachineKeySet);
                _systemLogger.LogInformation("Certificate loaded successfully.");
            } catch (Exception ex) {
                _systemLogger.LogError($"Error loading certificate: {ex.Message}");
                throw;
            }
        }
        protected void LoadXsdSchema() {
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
        public void Disconnect() {
            _systemLogger.LogWarning("Disconnecting the client.");
            _sslStream?.Close();
            _client?.Close();
        }
        private void TryParseInt(string value, string paramName, Action<int> setValue, List<string> usingDefaults) {
            if (int.TryParse(value, out int result)) {
                setValue(result);
            } else {
                _systemLogger.LogError($"{paramName} in config is not a valid integer.");
                usingDefaults.Add(paramName);
            }
        }
    }
}
