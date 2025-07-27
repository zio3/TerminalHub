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
        private bool _disposed;
        
        // ターミナルサイズを記録
        private int _currentCols = 80;
        private int _currentRows = 24;
        
        // カーソル点滅パターン検出用
        private bool _isInCursorBlinkMode = false;
        private string? _lastProcessedData = null;
        private DateTime _lastCursorBlinkTime = DateTime.MinValue;
        private readonly TimeSpan _cursorBlinkTimeout = TimeSpan.FromSeconds(2);
        private readonly Queue<string> _recentCursorControlLines = new Queue<string>();
        private const int RecentLinesWindowSize = 5;
        
        // セッション切り替え時の再描画検出用
        private bool _isInRedrawMode = false;
        private int _redrawLineCount = 0;
        private DateTime _lastDataReceivedTime = DateTime.MinValue;

        // イベント：新しいデータを受信した時
        public event Action<string>? DataReceived;
        
        // イベント：セッションが切断された時
        public event Action? Disconnected;
        
        /// <summary>
        /// すべてのイベントハンドラーをクリア
        /// </summary>
        public void ClearEventHandlers()
        {
            DataReceived = null;
            Disconnected = null;
        }

        public ConPtyWithBuffer(ConPtySession conPtySession, ILogger logger, int bufferCapacity = 10000)
        {
            _conPtySession = conPtySession ?? throw new ArgumentNullException(nameof(conPtySession));
            _outputBuffer = new CircularLineBuffer(bufferCapacity);
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _cancellationTokenSource = new CancellationTokenSource();
            
            // ConPtySessionから初期サイズを取得
            _currentCols = _conPtySession.Cols;
            _currentRows = _conPtySession.Rows;

            // イベントハンドラを登録
            _conPtySession.DataReceived += OnConPtyDataReceived;
            _conPtySession.ProcessExited += OnConPtyProcessExited;

            // ConPtySessionを開始（これによりバックグラウンドでの読み取りが開始される）
            _conPtySession.Start();
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

            // CircularLineBufferから全行を取得
            var allLines = new List<string>();
            foreach (var line in _outputBuffer)
            {
                allLines.Add(line);
            }
            
            return string.Join("\r\n", allLines);
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
                
                // リサイズ時も再描画モードを開始
                _isInRedrawMode = true;
                _redrawLineCount = 0;
                // リサイズによる再描画モード開始
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
        /// 画面の再描画を要求（実験的）
        /// </summary>
        public async Task RequestRedraw()
        {
            if (!_disposed)
            {
                // 方法1: 画面の再描画を要求する制御シーケンス
                await WriteAsync("\x1b[7t");  // Request terminal status
                await Task.Delay(10);
                
                // 方法2: カーソル位置の保存と復元
                await WriteAsync("\x1b[s");   // Save cursor position
                await WriteAsync("\x1b[u");   // Restore cursor position
            }
        }
        
        /// <summary>
        /// 画面をクリアする
        /// </summary>
        public async Task ClearScreen()
        {
            if (!_disposed)
            {
                // 画面全体をクリアしてカーソルをホーム位置に移動
                await WriteAsync("\x1b[2J\x1b[H");
            }
        }
        
        /// <summary>
        /// バッファをクリアする
        /// </summary>
        public void ClearBuffer()
        {
            if (!_disposed)
            {
                _outputBuffer.Clear();
            }
        }
        
        /// <summary>
        /// データを処理してバッファに追加
        /// </summary>
        private void ProcessDataForBuffer(string data)
        {
            var lines = data.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            foreach (var line in lines)
            {
                _outputBuffer.AddLine(line);
            }
        }
        
        /// <summary>
        /// スクロールバックバッファをクリア（実験的）
        /// </summary>
        public async Task ClearScrollbackBuffer()
        {
            if (!_disposed)
            {
                // スクロールバックバッファをクリアする制御シーケンス
                await WriteAsync("\x1b[3J");
            }
        }

        /// <summary>
        /// ConPtySessionからデータ受信時のイベントハンドラ
        /// </summary>
        private void OnConPtyDataReceived(object? sender, DataReceivedEventArgs e)
        {
            if (_disposed)
                return;

            try
            {
                var data = e.Data;
                
                // BOM (Byte Order Mark) を削除
                if (data.Length > 0 && data[0] == '\uFEFF')
                {
                    data = data.Substring(1);
                    _logger.LogInformation("BOM detected and removed from ConPTY output");
                }
                
                // バッファリングを有効化
                var shouldBuffer = true;
                
                LogBufferAddition(data, shouldBuffer);
                
                if (shouldBuffer)
                {
                    // バッファに書き込み
                    // データを行ごとに分割してバッファに追加
                    ProcessDataForBuffer(data);
                }
                
                // イベント発火（表示は常に行う）
                if (DataReceived != null)
                {
                    //_logger.LogInformation($"[ConPtyWithBuffer] データ受信イベント発火 (長さ: {data.Length})");
                    DataReceived?.Invoke(data);
                }
                else
                {
                    //_logger.LogWarning($"[ConPtyWithBuffer] DataReceivedハンドラーが未設定");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing ConPTY data");
            }
        }

        /// <summary>
        /// ConPtySessionプロセス終了時のイベントハンドラ
        /// </summary>
        private void OnConPtyProcessExited(object? sender, EventArgs e)
        {
            if (_disposed)
                return;

            // 切断イベントを発火
            try
            {
                _logger.LogInformation("ConPtyセッションが終了しました");
                Disconnected?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error firing Disconnected event");
            }
        }

        /// <summary>
        /// 受信データを処理してバッファに格納
        /// </summary>
        private void ProcessAndBufferData(string data)
        {
            if (string.IsNullOrEmpty(data))
                return;

            // まず、連続する制御シーケンスをクリーンアップ
            data = CleanupRepeatedControlSequences(data);

            // 改行で分割して行ごとに処理
            var lines = data.Split(new[] { '\r', '\n' }, StringSplitOptions.None);
            
            foreach (var line in lines)
            {
                // カーソル点滅パターンのチェック
                if (ShouldSkipCursorBlinkPattern(line))
                {
                    continue;
                }
                
                // 空行も含めてバッファに追加（ターミナル出力の整合性のため）
                _outputBuffer.AddLine(line);
            }
        }
        
        /// <summary>
        /// 連続する制御シーケンスをクリーンアップ
        /// </summary>
        private string CleanupRepeatedControlSequences(string data)
        {
            if (string.IsNullOrEmpty(data))
                return data;
            
            // 大量の[Kの繰り返しを削減
            // [K[K[K... を [K[K[K] (3回)に制限
            var result = System.Text.RegularExpressions.Regex.Replace(
                data, 
                @"(\x1b\[K){4,}",  // 4回以上の[Kの繰り返し
                "\x1b[K\x1b[K\x1b[K");  // 3回に置換
            
            // カーソル表示/非表示の過度な繰り返しを削減
            // [?25h[?25l[?25h[?25l... のパターン
            result = System.Text.RegularExpressions.Regex.Replace(
                result,
                @"((\x1b\[\?25[hl]){2}){2,}",  // 表示/非表示のペアが2回以上
                "$2$2");  // 1ペア（2回）に置換
            
            return result;
        }
        
        /// <summary>
        /// カーソル点滅パターンをスキップすべきか判定（シンプル版）
        /// </summary>
        private bool ShouldSkipCursorBlinkPattern(string line)
        {
            // カーソル表示/非表示のみの行かチェック
            if (!IsCursorShowHideOnlyLine(line))
            {
                // カーソル表示/非表示以外の内容があれば履歴をクリア
                _recentCursorControlLines.Clear();
                return false;
            }
            
            // 最近のカーソル制御行を記録
            _recentCursorControlLines.Enqueue(line);
            if (_recentCursorControlLines.Count > RecentLinesWindowSize)
            {
                _recentCursorControlLines.Dequeue();
            }
            
            // 最低2つの履歴があれば点滅パターンをチェック
            if (_recentCursorControlLines.Count >= 2)
            {
                var lines = _recentCursorControlLines.ToArray();
                var lastLine = lines[lines.Length - 1];
                var prevLine = lines[lines.Length - 2];
                
                // カーソル表示と非表示が交互に来ているかチェック
                if ((ContainsCursorHide(lastLine) && ContainsCursorShow(prevLine)) ||
                    (ContainsCursorShow(lastLine) && ContainsCursorHide(prevLine)))
                {
                    // 点滅パターンと判定 - スキップ
                    return true;
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// カーソル非表示を含むかチェック
        /// </summary>
        private bool ContainsCursorHide(string line)
        {
            return line.Contains("\x1b[?25l");
        }
        
        /// <summary>
        /// カーソル表示を含むかチェック
        /// </summary>
        private bool ContainsCursorShow(string line)
        {
            return line.Contains("\x1b[?25h");
        }
        
        /// <summary>
        /// カーソル表示/非表示のみの行かどうか判定
        /// </summary>
        private bool IsCursorShowHideOnlyLine(string line)
        {
            if (string.IsNullOrEmpty(line))
                return false;
                
            var trimmed = line.Trim();
            
            // カーソル表示/非表示のパターンのみかチェック
            return trimmed == "\x1b[?25h" ||      // カーソル表示
                   trimmed == "\x1b[?25l" ||      // カーソル非表示
                   trimmed == "\x1b[?25h\x1b[K" || // カーソル表示 + 行クリア
                   trimmed == "\x1b[?25l\x1b[K" || // カーソル非表示 + 行クリア
                   trimmed == "\x1b[K" ||          // 行クリアのみ（連続する場合が多い）
                   IsRepeatedClearLine(trimmed);   // 複数の[Kの繰り返し
        }
        
        /// <summary>
        /// 繰り返しの行クリアかどうか判定
        /// </summary>
        private bool IsRepeatedClearLine(string line)
        {
            // [K[K[K... のパターンをチェック
            if (!line.StartsWith("\x1b[K"))
                return false;
                
            // すべて[Kの繰り返しかチェック
            var clearPattern = "\x1b[K";
            var repeatCount = 0;
            var index = 0;
            
            while (index < line.Length)
            {
                if (index + clearPattern.Length <= line.Length && 
                    line.Substring(index, clearPattern.Length) == clearPattern)
                {
                    repeatCount++;
                    index += clearPattern.Length;
                }
                else
                {
                    // [K以外の文字が含まれている
                    return false;
                }
            }
            
            // 3回以上の繰り返しなら除外対象
            return repeatCount >= 3;
        }
        
        /// <summary>
        /// カーソル制御のみの行かどうか判定
        /// </summary>
        private bool IsCursorControlOnlyLine(string line)
        {
            if (string.IsNullOrEmpty(line))
                return false;
            
            // よくあるカーソル制御パターン
            var cursorPatterns = new[]
            {
                "\x1b[K",      // 行末までクリア
                "\x1b[2K",     // 行全体クリア
                "\x1b[?25h",   // カーソル表示
                "\x1b[?25l",   // カーソル非表示
                "\x1b[H",      // ホーム位置
                "\x1b[J",      // 画面下部クリア
                "\x1b[2J",     // 画面全体クリア
            };
            
            // エスケープシーケンスのみで構成されているかチェック
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                return false;
                
            // 既知のカーソル制御パターンのみかチェック
            foreach (var pattern in cursorPatterns)
            {
                if (trimmed == pattern)
                    return true;
            }
            
            // 複数のエスケープシーケンスの組み合わせ（例: \x1b[?25l\x1b[K）
            if (trimmed.StartsWith("\x1b[") && !trimmed.Contains(' ') && trimmed.Length < 20)
            {
                // 印字可能文字が含まれていない場合は制御シーケンスとみなす
                var hasVisibleChar = false;
                foreach (var ch in trimmed)
                {
                    if (ch >= ' ' && ch < '\x7f' && ch != '[' && ch != ';' && ch != '?' && !char.IsDigit(ch))
                    {
                        hasVisibleChar = true;
                        break;
                    }
                }
                return !hasVisibleChar;
            }
            
            return false;
        }

        /// <summary>
        /// バッファ追記のログ出力
        /// </summary>
        private void LogBufferAddition(string data, bool wasBuffered)
        {
            var displayData = new StringBuilder();
            
            // データを文字ごとに処理
            for (int i = 0; i < data.Length; i++)
            {
                if (i < data.Length - 1 && data[i] == '\x1b' && data[i + 1] == '[')
                {
                    // エスケープシーケンスの開始を検出
                    int seqEnd = i + 2;
                    
                    // シーケンスの終端を探す
                    while (seqEnd < data.Length && !IsControlSequenceTerminator(data[seqEnd]))
                    {
                        seqEnd++;
                    }
                    
                    if (seqEnd < data.Length)
                    {
                        seqEnd++; // 終端文字を含める
                        var sequence = data.Substring(i, seqEnd - i);
                        displayData.Append($"[制御文:{EscapeSequenceToReadable(sequence)}] ");
                        i = seqEnd - 1; // forループで+1されるため
                    }
                    else
                    {
                        displayData.Append(data[i]);
                    }
                }
                else if (data[i] == '\r')
                {
                    displayData.Append("[CR] ");
                }
                else if (data[i] == '\n')
                {
                    displayData.Append("[LF] ");
                }
                else if (data[i] == '\t')
                {
                    displayData.Append("[TAB] ");
                }
                else if (data[i] < ' ' || data[i] == '\x7f')
                {
                    displayData.Append($"[制御:{(int)data[i]:X2}] ");
                }
                else
                {
                    displayData.Append(data[i]);
                }
            }
            
            var status = wasBuffered ? "バッファ追記" : "スキップ";
            // デバッグ出力を削除
        }
        
        /// <summary>
        /// 制御シーケンスの終端文字かどうか判定
        /// </summary>
        private bool IsControlSequenceTerminator(char c)
        {
            return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '~' || c == '=' || c == '>' || c == '<';
        }
        
        /// <summary>
        /// エスケープシーケンスを読みやすい形式に変換
        /// </summary>
        private string EscapeSequenceToReadable(string sequence)
        {
            return sequence
                .Replace("\x1b[?25h", "カーソル表示")
                .Replace("\x1b[?25l", "カーソル非表示")
                .Replace("\x1b[K", "行末クリア")
                .Replace("\x1b[2K", "行全体クリア")
                .Replace("\x1b[H", "ホーム")
                .Replace("\x1b[J", "画面下部クリア")
                .Replace("\x1b[2J", "画面クリア")
                .Replace("\x1b[1;6~", "特殊キー")
                .Replace("\x1b", "ESC");
        }

        /// <summary>
        /// セッション切り替え時の再描画を検出
        /// </summary>
        /// <param name="data">受信したデータ</param>
        /// <returns>バッファに保存すべきならtrue</returns>
        private bool ProcessSessionSwitchRedraw(string data)
        {
            var now = DateTime.UtcNow;
            var timeSinceLastData = now - _lastDataReceivedTime;
            _lastDataReceivedTime = now;
            
            // 再描画モードでない場合のみ、新規再描画を検出
            if (!_isInRedrawMode)
            {
                // 3秒以上データが来ていない場合は、セッション切り替えの可能性
                if (timeSinceLastData > TimeSpan.FromSeconds(3))
                {
                    // 大量の行クリア制御文を含む場合は再描画モード開始
                    if (data.Contains("\x1b[K") && CountOccurrences(data, "\x1b[K") > 10)
                    {
                        _isInRedrawMode = true;
                        _redrawLineCount = 0;
                        // セッション切替による再描画モード開始
                        return false; // バッファに保存しない
                    }
                }
            }
            
            // 再描画モード中
            if (_isInRedrawMode)
            {
                _redrawLineCount++;
                
                // 画面サイズ分の行を受信したら再描画モード終了
                if (_redrawLineCount >= _currentRows)
                {
                    _isInRedrawMode = false;
                    // 再描画モード終了
                }
                
                return false; // バッファに保存しない
            }
            
            return true; // 通常のデータはバッファに保存
        }
        
        /// <summary>
        /// 文字列内の特定の部分文字列の出現回数を数える
        /// </summary>
        private int CountOccurrences(string text, string pattern)
        {
            int count = 0;
            int index = 0;
            while ((index = text.IndexOf(pattern, index)) != -1)
            {
                count++;
                index += pattern.Length;
            }
            return count;
        }

        /// <summary>
        /// カーソル点滅状態を処理し、バッファに保存すべきか判定
        /// </summary>
        /// <param name="data">受信したデータ</param>
        /// <returns>バッファに保存すべきならtrue</returns>
        private bool ProcessCursorBlinkState(string data)
        {
            if (string.IsNullOrEmpty(data))
                return true; // 空データは保存

            var now = DateTime.UtcNow;
            
            // カーソル点滅パターンのチェック
            // 改行なしで制御コードが連続する場合を考慮
            bool containsCursorControl = data.Contains("\x1b[?25h") || data.Contains("\x1b[?25l") || data.Contains("\x1b[K");
            bool containsOnlyCursorControl = IsCursorControlOnlyData(data);
            
            if (containsOnlyCursorControl)
            {
                // カーソル制御のみのデータ
                if (_isInCursorBlinkMode)
                {
                    // 既に点滅モード中 - バッファに保存しない
                    _lastCursorBlinkTime = now;
                    return false;
                }
                else if (_lastProcessedData != null && IsCursorControlOnlyData(_lastProcessedData))
                {
                    // 前回もカーソル制御のみだった - 点滅モード開始
                    _isInCursorBlinkMode = true;
                    _lastCursorBlinkTime = now;
                    return false;
                }
            }
            else
            {
                // カーソル制御以外の内容を含む
                if (_isInCursorBlinkMode)
                {
                    // 点滅モード終了
                    _isInCursorBlinkMode = false;
                }
            }
            
            // タイムアウトチェック
            if (_isInCursorBlinkMode && (now - _lastCursorBlinkTime) > _cursorBlinkTimeout)
            {
                // タイムアウト - 点滅モード終了
                _isInCursorBlinkMode = false;
            }
            
            _lastProcessedData = data;
            return !_isInCursorBlinkMode;
        }
        
        /// <summary>
        /// データがカーソル制御のみかどうか判定（改行なしの連続データ対応）
        /// </summary>
        private bool IsCursorControlOnlyData(string data)
        {
            if (string.IsNullOrEmpty(data))
                return false;
            
            // 制御シーケンスのパターン
            string[] controlPatterns = {
                "\x1b[?25h",    // カーソル表示
                "\x1b[?25l",    // カーソル非表示
                "\x1b[K",       // 行末までクリア
                "\x1b[2K",      // 行全体クリア
                "\x1b[H",       // ホーム位置
                "\x1b[J",       // 画面下部クリア
                "\x1b[2J",      // 画面全体クリア
                "\x1b[1;6~"     // 特殊キーシーケンス
            };
            
            // データを一時的にコピーして処理
            string remaining = data;
            
            // 全ての制御シーケンスを削除
            foreach (var pattern in controlPatterns)
            {
                remaining = remaining.Replace(pattern, "");
            }
            
            // 数字とセミコロンを含む位置指定パターン (\x1b[数字;数字H など) を削除
            remaining = System.Text.RegularExpressions.Regex.Replace(
                remaining, 
                @"\x1b\[\d+;\d+[HfR]", 
                "");
            
            // 単純な数字付きパターン (\x1b[数字A など) を削除
            remaining = System.Text.RegularExpressions.Regex.Replace(
                remaining, 
                @"\x1b\[\d+[ABCDEFGJKST]", 
                "");
            
            // エスケープ文字自体を削除
            remaining = remaining.Replace("\x1b", "");
            
            // 残りが空白文字のみかチェック
            return string.IsNullOrWhiteSpace(remaining);
        }

        /// <summary>
        /// デバッグ情報を取得
        /// </summary>
        public string GetDebugInfo()
        {
            if (_disposed)
                return "ConPtyWithBuffer: Disposed";

            return $"ConPtyWithBuffer: BufferLines={_outputBuffer.Count}, BufferCapacity={_outputBuffer.Capacity}, IsDisposed={_disposed}, CursorBlinkMode={_isInCursorBlinkMode}";
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
                
                // イベントハンドラを解除
                _conPtySession.DataReceived -= OnConPtyDataReceived;
                _conPtySession.ProcessExited -= OnConPtyProcessExited;
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