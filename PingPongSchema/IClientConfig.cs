
namespace Utils {
    public interface IClientConfig {
        public string SslPass { get; set; }
        public int Interval { get; set; }
        public int MaxReconnectAttempts { get; set; }
        public int ReconnectDelay { get; set; }
        public int ReadTimeout { get; set; }
        public int WriteTimeout { get; set; }
        public int HighLatencyThreshold { get; set; }
        public int LowLatencyThreshold { get; set; }
        public int MaxReadTimeout { get; set; }
        public int MinReadTimeout { get; set; }
        public int MaxWriteTimeout { get; set; }
        public int MinWriteTimeout { get; set; }
        public int MaxInterval { get; set; }
        public int MinInterval { get; set; }
        public string Separator { get; set; }
        public int Port { get; set; }
        public string ServerAddress { get; set; }
    }
}
