namespace Utils.Configs.Server
{
    public class DefaultServerConfig : IServerConfig
    {
        public int Port { get; set; } = 5001;
        public string ServerSslPass { get; set; }
        public int ReadTimeout { get; set; } = 5000;
        public int WriteTimeout { get; set; } = 5000;
        public string Separator { get; set; } = "<EOF>";
    }
}
