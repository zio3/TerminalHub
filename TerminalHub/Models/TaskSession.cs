using TerminalHub.Services;

namespace TerminalHub.Models
{
    public enum TaskStatus
    {
        Idle,       // 未実行
        Running,    // 実行中
        Completed,  // 正常終了
        Failed,     // エラー終了
        Stopped     // 手動停止
    }

    public class TaskSession
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string ScriptName { get; set; } = string.Empty;
        public string ScriptCommand { get; set; } = string.Empty;
        public string WorkingDirectory { get; set; } = string.Empty; // 作業ディレクトリ
        public TaskStatus Status { get; set; } = TaskStatus.Idle;
        public DateTime? StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public ConPtySession? ConPtySession { get; set; }
        public List<string> OutputBuffer { get; set; } = new();
        public int ExitCode { get; set; }
        public bool IsTerminalConnected { get; set; } = false; // ターミナル接続状態
        public bool IsCommandSent { get; set; } = false; // npmコマンドが送信済みかどうか

        // 実行時間の計算
        public TimeSpan? Duration
        {
            get
            {
                if (StartTime == null) return null;
                var endTime = EndTime ?? DateTime.Now;
                return endTime - StartTime.Value;
            }
        }

        // ステータスバッジ用のCSSクラス
        public string StatusCssClass => Status switch
        {
            TaskStatus.Running => "badge bg-primary",
            TaskStatus.Completed => "badge bg-success",
            TaskStatus.Failed => "badge bg-danger",
            TaskStatus.Stopped => "badge bg-warning",
            _ => "badge bg-secondary"
        };

        // ステータステキスト
        public string StatusText => Status switch
        {
            TaskStatus.Idle => "待機中",
            TaskStatus.Running => "実行中",
            TaskStatus.Completed => "完了",
            TaskStatus.Failed => "失敗",
            TaskStatus.Stopped => "停止",
            _ => "不明"
        };
    }
}