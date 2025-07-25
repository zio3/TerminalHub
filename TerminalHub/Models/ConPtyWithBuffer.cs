using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TerminalHub.Services;

namespace TerminalHub.Models
{
    /// <summary>
    /// ConPtyセッションとバッファリング機能を組み合わせたクラス
    /// セッションごとに独立したConPty + バッファの組を提供
    /// </summary>
    public class ConPtyWithBuffer : IDisposable
    {
        private readonly ConPtySession _conPtySession;
        private readonly CircularLineBuffer _outputBuffer;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly Task _readTask;
        private bool _disposed;
        
        // ターミナルサイズを記録
        private int _currentCols = 80;
        private int _currentRows = 24;

        // イベント：新しいデータを受信した時
        public event Action<string>? DataReceived;
        
        // イベント：セッションが切断された時
        public event Action? Disconnected;

        public ConPtyWithBuffer(ConPtySession conPtySession, ILogger logger, int bufferCapacity = 10000)
        {
            _conPtySession = conPtySession ?? throw new ArgumentNullException(nameof(conPtySession));
            _outputBuffer = new CircularLineBuffer(bufferCapacity);
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _cancellationTokenSource = new CancellationTokenSource();
            
            // ConPtySessionから初期サイズを取得したいが、現在は公開されていないのでデフォルト値を使用
            // TODO: ConPtySessionにサイズプロパティを追加

            // バックグラウンドでConPtyからデータを読み取り続ける
            _readTask = Task.Run(ReadFromConPtyAsync, _cancellationTokenSource.Token);
        }

        /// <summary>
        /// ConPtyセッションにデータを書き込み
        /// </summary>
        public async Task WriteAsync(string data)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ConPtyWithBuffer));

            await _conPtySession.WriteAsync(data);
        }

        /// <summary>
        /// バッファ内の全ての出力を取得
        /// </summary>
        public string GetBufferedOutput()
        {
            if (_disposed)
                return string.Empty;

            return _outputBuffer.GetAllText("");
        }

        /// <summary>
        /// バッファ内の最新N行を取得
        /// </summary>
        public IEnumerable<string> GetLastLines(int count)
        {
            if (_disposed)
                return Enumerable.Empty<string>();

            return _outputBuffer.GetLastLines(count);
        }

        /// <summary>
        /// バッファのスナップショットを取得（ターミナル表示用）
        /// </summary>
        /// <param name="maxLines">取得する最大行数。nullの場合は全ての行を取得</param>
        /// <returns>ターミナルに表示可能な形式の文字列</returns>
        public string GetSnapshot(int? maxLines = null)
        {
            if (_disposed)
                return string.Empty;

            if (!maxLines.HasValue)
            {
                // 全行を取得
                return _outputBuffer.GetAllText("");
            }
            else
            {
                // 指定行数の最新行を取得
                var lines = _outputBuffer.GetLastLines(maxLines.Value);
                return string.Join("", lines);
            }
        }

        /// <summary>
        /// ターミナルサイズに最適化されたスナップショットを取得
        /// </summary>
        /// <param name="terminalRows">ターミナルの行数</param>
        /// <param name="bufferMultiplier">バッファの倍率（デフォルト2倍）</param>
        /// <returns>ターミナル表示用の文字列</returns>
        public string GetTerminalSizedSnapshot(int terminalRows, double bufferMultiplier = 2.0)
        {
            if (_disposed)
                return string.Empty;

            // ターミナルサイズの2倍程度を取得（スクロール分を考慮）
            var targetLines = Math.Max(terminalRows, (int)(terminalRows * bufferMultiplier));
            return GetSnapshot(targetLines);
        }

        /// <summary>
        /// 実際に表示可能なN行分のデータを取得（制御シーケンスを考慮）
        /// </summary>
        /// <param name="visibleRows">表示したい行数</param>
        /// <param name="maxDataLines">取得する最大データ行数（デフォルト: visibleRows * 5）</param>
        /// <returns>ターミナル表示用の文字列</returns>
        public string GetVisibleLinesSnapshot(int visibleRows, int? maxDataLines = null)
        {
            if (_disposed || visibleRows <= 0)
                return string.Empty;

            // 制御シーケンスを考慮して、表示行数の5倍程度のデータを取得
            var dataLinesToFetch = maxDataLines ?? (visibleRows * 5);
            var lines = _outputBuffer.GetLastLines(dataLinesToFetch).ToList();
            
            // 最新から遡って画面クリア命令を探す
            int startIndex = 0;
            var combinedLines = new List<string>();
            
            // 逆順で確認（最新から古い方へ）
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                // 画面クリア命令を含む行を探す
                if (lines[i].Contains("\x1b[2J") || lines[i].Contains("\x1b[H\x1b[2J") || lines[i].Contains("\x1b[H\x1b[J"))
                {
                    // 画面クリア以降のデータのみを使用
                    startIndex = i;
                    
                    // クリア命令を含む行から、クリア命令以降の部分を取得
                    var clearIndex = lines[i].LastIndexOf("\x1b[2J");
                    if (clearIndex == -1) clearIndex = lines[i].LastIndexOf("\x1b[J");
                    
                    if (clearIndex >= 0 && clearIndex < lines[i].Length - 1)
                    {
                        // クリア命令の後にデータがある場合は、その部分を含める
                        lines[i] = lines[i].Substring(clearIndex);
                    }
                    else
                    {
                        // クリア命令で行が終わる場合は、次の行から開始
                        startIndex = i + 1;
                    }
                    break;
                }
            }
            
            // 画面クリア以降のデータを結合
            var relevantLines = lines.Skip(startIndex).ToList();
            var rawData = string.Join("", relevantLines);
            
            // エスケープシーケンスを解析して実際の表示行数を計算
            var estimatedVisibleLines = EstimateVisibleLines(rawData);
            
            // 表示行数が不足している場合は、さらにデータを取得（ただし画面クリアより前には遡らない）
            if (estimatedVisibleLines < visibleRows && dataLinesToFetch < _outputBuffer.Count && startIndex == 0)
            {
                // 画面クリアが見つからなかった場合のみ、追加データを取得
                var additionalLines = (int)((visibleRows - estimatedVisibleLines) * 3);
                dataLinesToFetch = Math.Min(dataLinesToFetch + additionalLines, _outputBuffer.Count);
                
                // 再帰的に呼び出し（maxDataLinesを更新）
                return GetVisibleLinesSnapshot(visibleRows, dataLinesToFetch);
            }
            
            return rawData;
        }

        /// <summary>
        /// データから実際の表示行数を推定（簡易版）
        /// </summary>
        private int EstimateVisibleLines(string data)
        {
            if (string.IsNullOrEmpty(data))
                return 0;

            // 簡易的な推定：
            // - 改行文字をカウント
            // - カーソル上移動(\033[nA)を減算
            // - 行クリア(\033[K, \033[2K)は影響なし
            // - 画面クリア(\033[2J)は全行クリア
            
            int visibleLines = 0;
            var lines = data.Split(new[] { '\n', '\r' }, StringSplitOptions.None);
            
            foreach (var line in lines)
            {
                if (!string.IsNullOrEmpty(line))
                {
                    visibleLines++;
                    
                    // カーソル上移動を検出（簡易版）
                    if (line.Contains("\x1b[") && line.Contains("A"))
                    {
                        // 非常に簡易的な処理（本来は数値を解析すべき）
                        visibleLines = Math.Max(0, visibleLines - 1);
                    }
                }
            }
            
            // 画面クリアがある場合はリセット
            if (data.Contains("\x1b[2J"))
            {
                // 最後の画面クリア以降のデータのみを考慮
                var lastClearIndex = data.LastIndexOf("\x1b[2J");
                var afterClear = data.Substring(lastClearIndex);
                return EstimateVisibleLines(afterClear);
            }
            
            return Math.Max(1, visibleLines);
        }

        /// <summary>
        /// バッファをクリア
        /// </summary>
        public void ClearBuffer()
        {
            if (!_disposed)
            {
                _outputBuffer.Clear();
            }
        }

        /// <summary>
        /// バッファの現在の行数
        /// </summary>
        public int BufferLineCount => _disposed ? 0 : _outputBuffer.Count;

        /// <summary>
        /// ConPtyのサイズを変更
        /// </summary>
        public void Resize(int cols, int rows)
        {
            if (!_disposed)
            {
                _conPtySession.Resize(cols, rows);
                _currentCols = cols;
                _currentRows = rows;
            }
        }
        
        /// <summary>
        /// 現在のターミナルサイズを取得
        /// </summary>
        public (int cols, int rows) GetTerminalSize()
        {
            return (_currentCols, _currentRows);
        }

        /// <summary>
        /// バックグラウンドでConPtyからデータを継続的に読み取り
        /// </summary>
        private async Task ReadFromConPtyAsync()
        {
            var buffer = new char[4096];
            var stringBuilder = new StringBuilder();

            try
            {
                while (!_cancellationTokenSource.Token.IsCancellationRequested && !_disposed)
                {
                    try
                    {
                        var bytesRead = await _conPtySession.ReadAsync(buffer, 0, buffer.Length);
                        
                        if (bytesRead > 0)
                        {
                            var data = new string(buffer, 0, bytesRead);
                            
                            // バッファに保存
                            ProcessAndBufferData(data);
                            
                            // イベント発火
                            DataReceived?.Invoke(data);
                        }
                        else
                        {
                            // データがない場合は短時間待機
                            await Task.Delay(10, _cancellationTokenSource.Token);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // キャンセルされた場合は正常終了
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        // オブジェクトが破棄された場合は終了
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error reading from ConPty in ConPtyWithBuffer");
                        await Task.Delay(100, _cancellationTokenSource.Token); // エラー時は少し長く待機
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 正常なキャンセル
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fatal error in ConPtyWithBuffer read loop");
            }
            finally
            {
                // 切断イベントを発火
                try
                {
                    Disconnected?.Invoke();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error firing Disconnected event");
                }
            }
        }

        /// <summary>
        /// 受信データを処理してバッファに格納
        /// </summary>
        private void ProcessAndBufferData(string data)
        {
            if (string.IsNullOrEmpty(data))
                return;

            // 改行で分割して行ごとに処理
            var lines = data.Split(new[] { '\r', '\n' }, StringSplitOptions.None);
            
            foreach (var line in lines)
            {
                // 空行も含めてバッファに追加（ターミナル出力の整合性のため）
                _outputBuffer.AddLine(line);
            }
        }

        /// <summary>
        /// デバッグ情報を取得
        /// </summary>
        public string GetDebugInfo()
        {
            if (_disposed)
                return "ConPtyWithBuffer: Disposed";

            return $"ConPtyWithBuffer: BufferLines={_outputBuffer.Count}, BufferCapacity={_outputBuffer.Capacity}, IsDisposed={_disposed}";
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            try
            {
                // 読み取りタスクをキャンセル
                _cancellationTokenSource.Cancel();
                
                // タスクの完了を待機（最大1秒）
                if (!_readTask.IsCompleted)
                {
                    _readTask.Wait(TimeSpan.FromSeconds(1));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error stopping read task in ConPtyWithBuffer");
            }

            try
            {
                // ConPtyセッションを破棄
                _conPtySession.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing ConPty session in ConPtyWithBuffer");
            }

            try
            {
                // CancellationTokenSourceを破棄
                _cancellationTokenSource.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing CancellationTokenSource in ConPtyWithBuffer");
            }

            // バッファをクリア
            _outputBuffer.Clear();

            _logger.LogInformation("ConPtyWithBuffer disposed");
        }
    }
}