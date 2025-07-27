using System.Text.Json;

namespace TerminalHub.Services
{
    public class PackageJsonService : IPackageJsonService
    {
        private readonly ILogger<PackageJsonService> _logger;

        public PackageJsonService(ILogger<PackageJsonService> logger)
        {
            _logger = logger;
        }

        public Task<bool> HasPackageJsonAsync(string folderPath)
        {
            var packageJsonPath = Path.Combine(folderPath, "package.json");
            return Task.FromResult(File.Exists(packageJsonPath));
        }

        public async Task<Dictionary<string, string>?> GetNpmScriptsAsync(string folderPath)
        {
            try
            {
                var packageJsonPath = Path.Combine(folderPath, "package.json");
                _logger.LogInformation("Checking package.json at: {Path}", packageJsonPath);
                
                if (!File.Exists(packageJsonPath))
                {
                    _logger.LogWarning("package.json not found at: {Path}", packageJsonPath);
                    return null;
                }

                var jsonContent = await File.ReadAllTextAsync(packageJsonPath);
                _logger.LogInformation("package.json content length: {Length}", jsonContent.Length);
                
                using var document = JsonDocument.Parse(jsonContent);
                
                if (!document.RootElement.TryGetProperty("scripts", out var scriptsElement))
                {
                    _logger.LogWarning("No 'scripts' property found in package.json");
                    return null; // nullを返す
                }

                var scripts = new Dictionary<string, string>();
                foreach (var script in scriptsElement.EnumerateObject())
                {
                    scripts[script.Name] = script.Value.GetString() ?? string.Empty;
                    _logger.LogInformation("Found script: {Name} = {Value}", script.Name, script.Value.GetString());
                }

                _logger.LogInformation("Total scripts found: {Count}", scripts.Count);
                return scripts;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read package.json from {FolderPath}", folderPath);
                return null;
            }
        }
    }
}