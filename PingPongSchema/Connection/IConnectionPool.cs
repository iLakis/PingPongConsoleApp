using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Security;
using System.Text;
using System.Threading.Tasks;

namespace Utils.Connection {
    public interface IConnectionPool {
        Task<SslStream> GetConnectionAsync(CancellationToken token);
        //Task ReturnConnectionToPool(SslStream connection, CancellationToken token);
        Task CloseConnectionAsync(SslStream connection);
        Task CloseAllConnectionsAsync();
    }
}
