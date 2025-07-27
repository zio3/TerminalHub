namespace TerminalHub.Services
{
    public interface IPackageJsonService
    {
        Task<Dictionary<string, string>?> GetNpmScriptsAsync(string folderPath);
        Task<bool> HasPackageJsonAsync(string folderPath);
    }
}