namespace TerminalHub.Models
{
    public class TaskInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Command { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public bool IsRunning { get; set; }
        public Guid? SessionId { get; set; } // タスク実行用セッションID
        public DateTime? StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public int? ExitCode { get; set; }
    }

    public class PackageJsonInfo
    {
        public string FilePath { get; set; } = string.Empty;
        public Dictionary<string, string> Scripts { get; set; } = new();
        public string? Name { get; set; }
        public string? Version { get; set; }
        public DateTime LastModified { get; set; }
    }
}