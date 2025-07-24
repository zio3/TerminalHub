namespace TerminalHub.Models
{
    public class ConnectionInfo
    {
        public string ConnectionId { get; set; } = string.Empty;
        public Guid SessionId { get; set; }
        public bool IsMaster { get; set; }
        public DateTime ConnectedAt { get; set; } = DateTime.Now;
        public string? UserAgent { get; set; }
        public string? IpAddress { get; set; }
    }
}