using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Utils.Connection {
    public class ClientConnection {
        public Guid Id { get; } 
        public TcpClient TcpClient { get; }
        public SslStream SslStream { get; }

        public ClientConnection(TcpClient tcpClient, SslStream sslStream) {
            Id = Guid.NewGuid(); 
            TcpClient = tcpClient;
            SslStream = sslStream;
        }
    }

}
