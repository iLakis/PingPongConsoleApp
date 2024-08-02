using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Utils.Configs.Client
{
    public class DefaultClientConfig : IClientConfig
    {
        public string SslPass { get; set; }
        public int Interval { get; set; } = 3000;
        public int MaxReconnectAttempts { get; set; } = 5;
        public int ReconnectDelay { get; set; } = 5000;
        public int ReadTimeout { get; set; } = 5000;
        public int WriteTimeout { get; set; } = 5000;
        public int HighLatencyThreshold { get; set; } = 50;
        public int LowLatencyThreshold { get; set; } = 20;
        public int MaxReadTimeout { get; set; } = 20000;
        public int MinReadTimeout { get; set; } = 2000;
        public int MaxWriteTimeout { get; set; } = 20000;
        public int MinWriteTimeout { get; set; } = 2000;
        public int MaxInterval { get; set; } = 5000;
        public int MinInterval { get; set; } = 1000;
        public string Separator { get; set; } = "<EOF>";
        public int Port { get; set; } = 5001;
        public string ServerAddress { get; set; } = "localhost"; //"127.0.0.1";
    }
}
