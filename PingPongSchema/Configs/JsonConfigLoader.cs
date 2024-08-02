using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Utils.Configs
{
    public class JsonConfigLoader<T> : IConfigLoader<T> where T : class, new()
    {
        private readonly string _configFilePath;
        private readonly ILogger _logger;

        public JsonConfigLoader(string configFilePath, ILogger logger)
        {
            _configFilePath = configFilePath;
            _logger = logger;
        }
        public T LoadConfig()
        {
            List<string> usingDefaults = new List<string>();
            T config = new T();

            try
            {
                var configuration = new ConfigurationBuilder()
                    .SetBasePath(Directory.GetCurrentDirectory())
                    .AddJsonFile(_configFilePath, optional: false, reloadOnChange: true)
                    .Build();

                foreach (var property in typeof(T).GetProperties())
                {
                    var value = configuration[property.Name];
                    if (!string.IsNullOrEmpty(value))
                    {
                        if (property.PropertyType == typeof(int))
                        {
                            if (int.TryParse(value, out int intValue))
                            {
                                property.SetValue(config, intValue);
                            }
                            else
                            {
                                _logger.LogError($"{property.Name} in config is not a valid integer.");
                                usingDefaults.Add(property.Name);
                            }
                        }
                        else
                        {
                            property.SetValue(config, value);
                        }
                    }
                    else
                    {
                        _logger.LogError($"{property.Name} was not found in config file");
                        usingDefaults.Add(property.Name);
                    }
                }

                if (usingDefaults.Count > 0)
                {
                    _logger.LogWarning($"Configuration loaded with errors: Could not find or parse: {string.Join(", ", usingDefaults)}");
                    _logger.LogWarning("Using default values for missing variables.");
                }
                else
                {
                    _logger.LogInformation("Configuration loaded successfully.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error loading configuration: {ex.Message}");
                throw;
            }

            return config;
        }
    }
}
