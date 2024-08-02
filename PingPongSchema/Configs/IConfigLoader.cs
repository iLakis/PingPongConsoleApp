namespace Utils.Configs
{
    public interface IConfigLoader<T> where T : class, new()
    {
        T LoadConfig();
    }
}
