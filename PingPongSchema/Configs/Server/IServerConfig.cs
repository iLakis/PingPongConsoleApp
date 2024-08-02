namespace Utils.Configs.Server
{
    public interface IServerConfig
    {
        public int Port { get; set; }//= 5001;
        public string ServerSslPass { get; set; }
        public int ReadTimeout { get; set; }
        public int WriteTimeout { get; set; }
        public string Separator { get; set; }
    }
}
