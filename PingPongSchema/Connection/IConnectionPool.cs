using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Security;
using System.Text;
using System.Threading.Tasks;

namespace Utils.Connection {
    public interface IConnectionPool {
        event EventHandler<ConnectionEventArgs> ConnectionOpened;
        event EventHandler<ConnectionEventArgs> ConnectionClosed;
        event EventHandler<ConnectionErrorEventArgs> ConnectionError;
        Task<ClientConnection> GetConnectionAsync(CancellationToken token);
        //Task ReturnConnectionToPool(SslStream connection, CancellationToken token);
        Task CloseConnectionAsync(ClientConnection connection);
        Task CloseAllConnectionsAsync();
        Task InitializeAsync(CancellationToken token);
    }
}
