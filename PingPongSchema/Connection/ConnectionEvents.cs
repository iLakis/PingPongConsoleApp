using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Security;
using System.Text;
using System.Threading.Tasks;

namespace Utils.Connection {
    public class ConnectionEventArgs : EventArgs {
        public ClientConnection Connection { get; }

        public ConnectionEventArgs(ClientConnection connection) {
            Connection = connection;
        }
    }

    public class ConnectionErrorEventArgs : EventArgs {
        public ClientConnection Connection { get; }
        public Exception Exception { get; }

        public ConnectionErrorEventArgs(ClientConnection connection, Exception exception) {
            Connection = connection;
            Exception = exception;
        }
    }
}
