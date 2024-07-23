using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;

namespace PingPongConsoleApp {
    public class TcpServer {
        private const int Port = 5001;
        private static X509Certificate2 ServerCertificate;// = new X509Certificate2("server.pfx", "pingpongpass");

        public TcpServer() {
            try {
                string certPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ServerCertificate\\server.pfx");

                if (!File.Exists(certPath)) {
                    throw new FileNotFoundException("Certificate file not found", certPath);
                }

                ServerCertificate = new X509Certificate2(certPath, "pingpongpass");
                Console.WriteLine("Certificate loaded successfully.");

            } catch(Exception ex) {
                Console.WriteLine($"Error in TcpServer constructor: {ex.Message}");
                throw;
            }
        
        }

        public void Start() {
            TcpListener listener = new TcpListener(IPAddress.Any, Port);
            listener.Start();
            Console.WriteLine($"Server started on port {Port}");

            while (true) {
                System.Net.Sockets.TcpClient client = listener.AcceptTcpClient();
                Console.WriteLine("Client connected");
                var sslStream = new SslStream(client.GetStream(), false);
                sslStream.AuthenticateAsServer(ServerCertificate, clientCertificateRequired: false, checkCertificateRevocation: true);

                using (StreamReader reader = new StreamReader(sslStream))
                using (StreamWriter writer = new StreamWriter(sslStream) { AutoFlush = true }) {
                    while (client.Connected) {
                        string message = reader.ReadLine();
                        if (message != null) {
                            Console.WriteLine($"Received: {message}");
                            if (message.StartsWith("<ping")) {
                                string timestamp = DateTime.UtcNow.ToString("o"); // Using "o" for ISO 8601. Otherwise caused format problems
                                string response = $"<pong timestamp=\"{timestamp}\"/>";
                                writer.WriteLine(response);
                                Console.WriteLine($"Sent: {response}");
                            }

                        }
                    }
                }
            }
        }
    }
}
