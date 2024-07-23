using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;

namespace PingPongConsoleApp {
    public class TcpClient {
        private const string ServerAddress = "127.0.0.1";
        private const int Port = 5001;
        private static X509Certificate2 ClientCertificate;
        private static int Interval;

        public TcpClient() {
            try {
                //Configuration:
                Console.WriteLine("Loading configuration...");
                var configuration = new ConfigurationBuilder()
                    .SetBasePath(Directory.GetCurrentDirectory())
                    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                    .Build();

                string intervalString = configuration["Interval"];
                if (int.TryParse(intervalString, out int interval)) {
                    Interval = interval;
                } else {
                    throw new Exception("Interval in appsettings.json is not a valid integer.");
                }
                Console.WriteLine("Configuration loaded successfully.");

                //SSL Certificate:
                string certPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ClientCertificate\\client.pfx");

                if (!File.Exists(certPath)) {
                    throw new FileNotFoundException("Certificate file not found", certPath);
                }

                ClientCertificate = new X509Certificate2(certPath, "pingpongpassclient");
                Console.WriteLine("Certificate loaded successfully.");

            } catch (Exception ex) {
                Console.WriteLine($"Error in TcpClient constructor: {ex.Message}");
                throw;
            }
        }

        public void Start() {
            try {
                System.Net.Sockets.TcpClient client = new System.Net.Sockets.TcpClient(ServerAddress, Port);
                var sslStream = new SslStream(client.GetStream(), false, new RemoteCertificateValidationCallback(ValidateServerCertificate));
                sslStream.AuthenticateAsClient(ServerAddress, new X509CertificateCollection { ClientCertificate }, SslProtocols.Tls12, checkCertificateRevocation: true);

                using StreamReader reader = new StreamReader(sslStream);
                using (StreamWriter writer = new StreamWriter(sslStream) { AutoFlush = true }) {
                    while (client.Connected) {
                        string timestamp = DateTime.UtcNow.ToString("o"); // Using "o" for ISO 8601. Otherwise caused format problems
                        string message = $"<ping timestamp=\"{timestamp}\"/>";
                        writer.WriteLine(message);
                        Console.WriteLine($"Sent: {message}");

                        string response = reader.ReadLine();
                        if (response != null) {
                            Console.WriteLine($"Received: {response}");
                        }

                        Thread.Sleep(Interval);
                    }
                }
            } catch (Exception ex) {
                Console.WriteLine($"Error in TcpClient.Start: {ex.Message}");
            }
        }

        private static bool ValidateServerCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors) {
            // All certificates are accepted for testing purposes
            if (sslPolicyErrors == SslPolicyErrors.None)
                return true;

            Console.WriteLine($"Ignoring certificate validation errors: {sslPolicyErrors}");
            return true;

            // Stricter:
            // return sslPolicyErrors == SslPolicyErrors.None;
        }
    }
}
