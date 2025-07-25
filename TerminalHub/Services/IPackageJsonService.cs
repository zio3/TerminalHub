using TerminalHub.Models;

namespace TerminalHub.Services
{
    public interface IPackageJsonService
    {
        Task<PackageJsonInfo?> LoadPackageJsonAsync(string folderPath);
        Task<List<TaskInfo>> GetTasksAsync(string folderPath);
    }
}