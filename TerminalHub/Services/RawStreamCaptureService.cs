using System.Collections.Concurrent;
using System.Text;

namespace TerminalHub.Services
{
    /// <summary>
    /// ConPTY 生出力ストリームをファイルへ記録するデバッグ用サービス。
    /// VTエミュレータのパリティ検証フィクスチャ（*.raw）を実環境で採取するために使う。
    /// </summary>
    /// <remarks>
    /// 既定は無効。設定 → 開発ツール →「生ストリームをキャプチャ」で切り替える。
    /// 有効中はセッションごとに <c>%LOCALAPPDATA%\TerminalHub\captures\{sessionId}-{yyyyMMdd-HHmmss}.raw</c>
    /// へ UTF-8 で追記する。無効化時に全ライターを閉じる。
    /// </remarks>
    public interface IRawStreamCaptureService
    {
        bool Enabled { get; }
        void SetEnabled(bool enabled);
        void Capture(Guid sessionId, string data);
        string CapturesFolder { get; }
    }

    public sealed class RawStreamCaptureService : IRawStreamCaptureService, IDisposable
    {
        private readonly ILogger<RawStreamCaptureService> _logger;
        private readonly ConcurrentDictionary<Guid, StreamWriter> _writers = new();
        private readonly object _lock = new();
        private volatile bool _enabled;

        public RawStreamCaptureService(ILogger<RawStreamCaptureService> logger)
        {
            _logger = logger;
        }

        public bool Enabled => _enabled;

        public string CapturesFolder => Path.Combine(AppDataPaths.UserDataRoot, "captures");

        public void SetEnabled(bool enabled)
        {
            if (_enabled == enabled)
            {
                return;
            }

            lock (_lock)
            {
                _enabled = enabled;
                if (!enabled)
                {
                    CloseAllWriters();
                    _logger.LogInformation("生ストリームキャプチャを停止しました");
                }
                else
                {
                    Directory.CreateDirectory(CapturesFolder);
                    _logger.LogInformation("生ストリームキャプチャを開始しました: {Folder}", CapturesFolder);
                }
            }
        }

        public void Capture(Guid sessionId, string data)
        {
            if (!_enabled || string.IsNullOrEmpty(data))
            {
                return;
            }

            try
            {
                var writer = _writers.GetOrAdd(sessionId, CreateWriter);
                lock (writer)
                {
                    writer.Write(data);
                }
            }
            catch (Exception ex)
            {
                // キャプチャは診断目的。失敗しても本処理を止めない
                _logger.LogWarning(ex, "生ストリームキャプチャの書き込みに失敗: {SessionId}", sessionId);
            }
        }

        private StreamWriter CreateWriter(Guid sessionId)
        {
            Directory.CreateDirectory(CapturesFolder);
            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var path = Path.Combine(CapturesFolder, $"{sessionId}-{timestamp}.raw");
            // BOM なし UTF-8 / 追記
            var writer = new StreamWriter(path, append: true, new UTF8Encoding(false))
            {
                AutoFlush = true
            };
            _logger.LogInformation("キャプチャ開始: {Path}", path);
            return writer;
        }

        private void CloseAllWriters()
        {
            foreach (var kvp in _writers)
            {
                try
                {
                    lock (kvp.Value)
                    {
                        kvp.Value.Flush();
                        kvp.Value.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "キャプチャライターのクローズに失敗: {SessionId}", kvp.Key);
                }
            }
            _writers.Clear();
        }

        public void Dispose()
        {
            lock (_lock)
            {
                CloseAllWriters();
            }
        }
    }
}
