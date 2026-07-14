using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace TerminalHub.Services
{
    // Terminal定数 (ConPtyService内部で使用)
    internal static class ConPtyTerminalConstants
    {
        public const uint ExtendedStartupinfoPresent = 0x00080000;
        public const int ProcThreadAttributePseudoConsole = 0x00020016;
    }
    
    public interface IConPtyService
    {
        Task<ConPtySession> CreateSessionAsync(string command, string? arguments, string? workingDirectory = null, int cols = 80, int rows = 24, Guid? sessionId = null);
    }
    
    public class DataReceivedEventArgs : EventArgs
    {
        public string Data { get; }

        /// <summary>
        /// SessionManager のサーバー側タップ（最初の購読者）がバッファへ Append した結果、
        /// 進行中のリプレイキャプチャに取り込まれたかどうか。true のとき Circuit 側は
        /// このチャンクを xterm へ直接書き込んではならない（リプレイのテールで届くため二重になる）。
        /// </summary>
        public bool CapturedByReplay { get; set; }

        public DataReceivedEventArgs(string data)
        {
            Data = data;
        }
    }

    public class ConPtyService : IConPtyService
    {
        private readonly ILogger<ConPtyService> _logger;

        public ConPtyService(ILogger<ConPtyService> logger)
        {
            _logger = logger;
        }

        public Task<ConPtySession> CreateSessionAsync(string command, string? arguments, string? workingDirectory = null, int cols = 80, int rows = 24, Guid? sessionId = null)
        {
            return Task.FromResult(new ConPtySession(command, arguments, workingDirectory, _logger, cols, rows, true, sessionId)); // バッファリング有効
        }
    }

    public class ConPtySession : IDisposable
    {
        private Process? _process;
        private IntPtr _hPC = IntPtr.Zero;
        private IntPtr _hPipeIn = IntPtr.Zero;
        private IntPtr _hPipeOut = IntPtr.Zero;
        private readonly ILogger _logger;
        private FileStream? _pipeInStream;
        private FileStream? _pipeOutStream;
        private StreamWriter? _writer;
        private int _disposeState;
        private bool IsDisposed => Volatile.Read(ref _disposeState) != 0;
        private readonly object _pseudoConsoleLock = new();
        private readonly Decoder _utf8Decoder = Encoding.UTF8.GetDecoder();
        private Task? _readTask;
        private CancellationTokenSource? _readCancellationTokenSource;
        private bool _started = false;

        // このセッションの TerminalHub セッション GUID。子プロセスに環境変数 TERMINALHUB_SESSION_ID として
        // 注入し、CLI/エージェントが「自分がどの TerminalHub セッションか」を自己識別できるようにする。
        private readonly Guid? _sessionId;

        // Codex resume 起動停止の診断用。CreateProcess はコンストラクター内、パイプ読み取りは
        // Start() 後に始まるため、その間隔と「制御シーケンスしか届かない」状態を記録する。
        // 通常セッションのログを増やさないよう codex resume --last のときだけ有効化する。
        private readonly Stopwatch _startupStopwatch = Stopwatch.StartNew();
        private bool _startupDiagnosticsEnabled;
        private long _processStartedElapsedMs = -1;
        private long _readerStartedElapsedMs = -1;
        private long _firstOutputElapsedMs = -1;
        private long _firstPrintableElapsedMs = -1;
        private long _lastOutputElapsedMs = -1;
        private long _startupBytesRead;
        private int _startupChunksRead;
        private readonly StartupOutputProbe _startupOutputProbe = new();

        // 環境変数フラグ
        private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        
        // パフォーマンス最適化設定
        private bool _enableBuffering = true; // バッファリング有効
        // バッファ抽出と配送キューへの追加を同じロックで直列化し、ConPTYの出力順を保つ。
        // イベントはロック外で単一ディスパッチャーが発火し、購読者からDisposeされた場合も
        // 出力ロックへの再入によるデッドロックを防ぐ。
        private readonly object _outputLock = new();
        private readonly Queue<string> _pendingOutput = new();
        private bool _isDispatchingOutput;

        // 書き込みの直列化用。UI・MCP(send_to_session)・RemoteLaunch など複数経路から
        // 同時に WriteAsync が呼ばれると、256文字チャンク＋Delay の隙間で入力が混ざるため排他する
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        // _writeLock の待機を Dispose 時に解除するためのトークン。
        // SemaphoreSlim.Dispose() は既に WaitAsync で待機中のタスクを解放しないため、
        // Dispose で Cancel して待機側を OperationCanceledException で抜けさせ、永久ブロックを防ぐ
        private readonly CancellationTokenSource _writeLockCts = new();
        private System.Timers.Timer? _flushTimer;
        private readonly StringBuilder _outputBuffer = new();
        private const int FLUSH_INTERVAL_MS = 16; // 約60fps
        private const int MAX_BUFFER_SIZE = 65536; // 64KB

        // フロー制御用
        private volatile bool _outputPaused = false;
        private TaskCompletionSource<bool>? _resumeTcs;
        private readonly object _pauseLock = new();

        /// <summary>
        /// 出力が一時停止中かどうか
        /// </summary>
        public bool IsOutputPaused => _outputPaused;

        public int Cols { get; private set; }
        public int Rows { get; private set; }
        
        // プロセス情報
        public int ProcessId => _process?.Id ?? -1;
        public bool HasExited => _process?.HasExited ?? true;
        
        // 統計情報
        public long TotalBytesRead { get; private set; } = 0;
        public long TotalBytesWritten { get; private set; } = 0;
        
        // データ受信イベント
        public event EventHandler<DataReceivedEventArgs>? DataReceived;
        
        // プロセス終了イベント
        public event EventHandler? ProcessExited;

        public ConPtySession(string command, string? arguments, string? workingDirectory, ILogger logger, int cols = 80, int rows = 24, bool enableBuffering = true, Guid? sessionId = null)
        {
            _logger = logger;
            Cols = cols;
            Rows = rows;
            _enableBuffering = enableBuffering;
            _sessionId = sessionId;
            
            // バッファリング用タイマーの初期化（有効時のみ）
            if (_enableBuffering)
            {
                _flushTimer = new System.Timers.Timer(FLUSH_INTERVAL_MS);
                _flushTimer.Elapsed += (sender, e) =>
                {
                    if (!IsDisposed)
                        FlushOutputBuffer();
                };
                _flushTimer.AutoReset = true;
            }

            InitializeConPty(command, arguments, workingDirectory);
        }
        
        public void Start()
        {
            if (_started || IsDisposed)
                return;
                
            _started = true;
            
            // xterm.jsを使用する場合、初期化シーケンスは不要
            // xterm.jsが自動的にエスケープシーケンスを処理
            
            // バッファリングが有効な場合はタイマーを開始
            if (_enableBuffering)
            {
                _flushTimer?.Start();
            }
            
            _readCancellationTokenSource = new CancellationTokenSource();
            _readerStartedElapsedMs = _startupStopwatch.ElapsedMilliseconds;
            if (_startupDiagnosticsEnabled)
            {
                _logger.LogInformation(
                    "[ConPtyStartup] 出力読み取り開始: SessionId={SessionId}, ProcessId={ProcessId}, ProcessToReaderMs={ProcessToReaderMs}, Size={Cols}x{Rows}",
                    _sessionId, ProcessId, ElapsedSince(_processStartedElapsedMs, _readerStartedElapsedMs), Cols, Rows);
            }
            _readTask = Task.Run(() => ReadPipeAsync(_readCancellationTokenSource.Token));
            if (_startupDiagnosticsEnabled)
            {
                _ = Task.Run(() => MonitorStartupOutputAsync(_readCancellationTokenSource.Token));
            }
        }

        private IntPtr CreateEnvironmentBlock()
        {
            // xterm.js用の環境変数を設定
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var envVars = new Dictionary<string, string>
            {
                ["TERM"] = "xterm-256color",
                ["COLORTERM"] = "truecolor",
                ["HOME"] = userProfile, // Claude CLI等のNode.jsツールが使用
            };

            // このセッションの TerminalHub セッション GUID を注入する。
            // CLI/エージェントは環境変数 TERMINALHUB_SESSION_ID を読めば「自分がどのセッションか」を
            // 自己識別でき、MCP の set_memo 等で自分自身を対象にできる（同一フォルダで複数セッションでも
            // プロセス単位なので混線しない）。
            if (_sessionId is Guid sid && sid != Guid.Empty)
            {
                envVars["TERMINALHUB_SESSION_ID"] = sid.ToString();
            }

            // 既存の環境変数を取得してマージ
            var currentEnv = Environment.GetEnvironmentVariables();
            foreach (System.Collections.DictionaryEntry env in currentEnv)
            {
                var key = env.Key?.ToString();
                var value = env.Value?.ToString();
                if (key != null && value != null && !envVars.ContainsKey(key))
                {
                    envVars[key] = value;
                }
            }

            // 環境変数ブロックを作成（Unicode形式）
            var envBlock = new System.Text.StringBuilder();
            foreach (var kvp in envVars)
            {
                envBlock.Append($"{kvp.Key}={kvp.Value}\0");
            }
            envBlock.Append('\0'); // 終端のnull
            
            // Unicodeバイト配列に変換してマーシャリング
            var bytes = Encoding.Unicode.GetBytes(envBlock.ToString());
            var ptr = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            
            return ptr;
        }

        private void InitializeConPty(string command, string? arguments, string? workingDirectory)
        {
            // ConPTYの初期化
            var startupInfo = new STARTUPINFOEX();
            startupInfo.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();

            IntPtr hWritePipe = IntPtr.Zero;
            IntPtr hReadPipe = IntPtr.Zero;
            IntPtr processInfoHandle = IntPtr.Zero;
            IntPtr threadInfoHandle = IntPtr.Zero;
            
            try
            {
                // パイプの作成
                const uint pipeBufferSize = 65536;
                if (!CreatePipe(out var hPipeIn, out hWritePipe, IntPtr.Zero, pipeBufferSize))
                {
                    throw new InvalidOperationException($"Failed to create input pipe: {Marshal.GetLastWin32Error()}");
                }
                _hPipeIn = hPipeIn;

                if (!CreatePipe(out hReadPipe, out var hPipeOut, IntPtr.Zero, pipeBufferSize))
                {
                    throw new InvalidOperationException($"Failed to create output pipe: {Marshal.GetLastWin32Error()}");
                }
                _hPipeOut = hPipeOut;

            // ConPTYの作成（XTerm互換のための設定）
            var size = new COORD { X = (short)Cols, Y = (short)Rows };
            
            // ConPTYフラグ（現在は0しか定義されていない）
            uint conPtyFlags = 0;
            
            var hr = CreatePseudoConsole(size, hPipeIn, hPipeOut, conPtyFlags, out _hPC);
            if (hr != 0)
            {
                _logger.LogError($"CreatePseudoConsole failed with HRESULT: {hr:X}");
                throw new InvalidOperationException($"Failed to create pseudo console: {hr:X}");
            }

            // ConPTYへ渡した端は CreatePseudoConsole 内部で複製されるため、親側では即閉じる
            // （MS公式サンプルと同じ作法）。特に出力パイプの書き込み端をホストが保持し続けると、
            // ConPTY終了後も読み取り側がEOFにならず、読み取りループが抜けられなくなる
            CloseHandle(_hPipeIn);
            _hPipeIn = IntPtr.Zero;
            CloseHandle(_hPipeOut);
            _hPipeOut = IntPtr.Zero;

                // プロセス属性リストの初期化（1回目は必要サイズの取得。失敗が正常な標準パターン）
                var lpSize = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref lpSize);
                startupInfo.lpAttributeList = Marshal.AllocHGlobal(lpSize);
                if (!InitializeProcThreadAttributeList(startupInfo.lpAttributeList, 1, 0, ref lpSize))
                {
                    throw new InvalidOperationException($"Failed to initialize proc thread attribute list: {Marshal.GetLastWin32Error()}");
                }

            // 擬似コンソールの属性を設定
            if (!UpdateProcThreadAttribute(
                startupInfo.lpAttributeList,
                0,
                (IntPtr)ConPtyTerminalConstants.ProcThreadAttributePseudoConsole,
                _hPC,
                (IntPtr)IntPtr.Size,
                IntPtr.Zero,
                IntPtr.Zero))
            {
                throw new InvalidOperationException($"Failed to set pseudo console attribute: {Marshal.GetLastWin32Error()}");
            }

            // プロセスの作成
            var processInfo = new PROCESS_INFORMATION();
            // すべてのコマンドをcmd.exe経由で実行（.cmd/.batファイルも確実に動作）
            var fullCommand = string.IsNullOrWhiteSpace(arguments) ? command : $"{command} {arguments}";
            var cmdline = $"cmd.exe /c {fullCommand}";

            _startupDiagnosticsEnabled =
                cmdline.Contains("codex", StringComparison.OrdinalIgnoreCase) &&
                cmdline.Contains("resume --last", StringComparison.OrdinalIgnoreCase);

            _logger.LogInformation($"Creating process: {cmdline} in directory: {workingDirectory ?? "current"}");
            
            // XTerm互換のための環境変数を設定
            var envBlock = CreateEnvironmentBlock();
            
            bool result;
            try
            {
                result = CreateProcess(
                    null,
                    cmdline,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    ConPtyTerminalConstants.ExtendedStartupinfoPresent | CREATE_UNICODE_ENVIRONMENT,
                    envBlock,
                    workingDirectory,
                    ref startupInfo,
                    out processInfo);
            }
            finally
            {
                // 環境変数ブロックを確実に解放（例外時のリークも防ぐ）
                if (envBlock != IntPtr.Zero)
                    Marshal.FreeHGlobal(envBlock);
            }

            if (!result)
            {
                var error = Marshal.GetLastWin32Error();
                _logger.LogError($"CreateProcess failed with error: {error}");
                throw new InvalidOperationException($"Failed to create process: {error}");
            }

            _process = Process.GetProcessById((int)processInfo.dwProcessId);
            _processStartedElapsedMs = _startupStopwatch.ElapsedMilliseconds;
            if (_startupDiagnosticsEnabled)
            {
                _logger.LogInformation(
                    "[ConPtyStartup] Codex resume プロセス起動: SessionId={SessionId}, ProcessId={ProcessId}, Size={Cols}x{Rows}",
                    _sessionId, _process.Id, Cols, Rows);
            }

                processInfoHandle = processInfo.hProcess;
                threadInfoHandle = processInfo.hThread;
                
                // ストリームの作成
                var pipeIn = new Microsoft.Win32.SafeHandles.SafeFileHandle(hWritePipe, true);
                hWritePipe = IntPtr.Zero;
                _pipeInStream = new FileStream(pipeIn, FileAccess.Write);

                var pipeOut = new Microsoft.Win32.SafeHandles.SafeFileHandle(hReadPipe, true);
                hReadPipe = IntPtr.Zero;
                _pipeOutStream = new FileStream(pipeOut, FileAccess.Read);
                
                // StreamWriterを作成（XTerm向けにUTF-8、改行コードLF）
                // バッファサイズを65KBに増やして長い文字列の問題を解決
                _writer = new StreamWriter(_pipeInStream, new UTF8Encoding(false), bufferSize: 65536)
                {
                    AutoFlush = true,
                    NewLine = "\n"  // LF改行（Unix形式）
                };
                
                // ハンドルのクリーンアップ
                CloseHandle(processInfoHandle);
                CloseHandle(threadInfoHandle);
                DeleteProcThreadAttributeList(startupInfo.lpAttributeList);
                Marshal.FreeHGlobal(startupInfo.lpAttributeList);
            }
            catch
            {
                // エラー時のクリーンアップ（初期化失敗時はDisposeが呼ばれないため、ConPTY側の端もここで閉じる）
                if (hWritePipe != IntPtr.Zero) CloseHandle(hWritePipe);
                if (hReadPipe != IntPtr.Zero) CloseHandle(hReadPipe);
                _writer?.Dispose();
                _pipeInStream?.Dispose();
                _pipeOutStream?.Dispose();
                if (_hPipeIn != IntPtr.Zero) { CloseHandle(_hPipeIn); _hPipeIn = IntPtr.Zero; }
                if (_hPipeOut != IntPtr.Zero) { CloseHandle(_hPipeOut); _hPipeOut = IntPtr.Zero; }
                if (processInfoHandle != IntPtr.Zero) CloseHandle(processInfoHandle);
                if (threadInfoHandle != IntPtr.Zero) CloseHandle(threadInfoHandle);
                if (startupInfo.lpAttributeList != IntPtr.Zero)
                {
                    DeleteProcThreadAttributeList(startupInfo.lpAttributeList);
                    Marshal.FreeHGlobal(startupInfo.lpAttributeList);
                }
                if (_hPC != IntPtr.Zero)
                {
                    ClosePseudoConsole(_hPC);
                    _hPC = IntPtr.Zero;
                }
                throw;
            }
        }

        /// <summary>
        /// ConPtyにデータを書き込みます（デフォルトで即時送信）
        /// </summary>
        /// <param name="input">送信するデータ</param>
        public async Task WriteAsync(string input)
        {
            if (_writer == null || IsDisposed)
                return;

            // 複数経路(UI/MCP等)からの同時書き込みを直列化し、チャンクの混線を防ぐ。
            // 待機中に Dispose されると SemaphoreSlim.Dispose では解放されないため、
            // _writeLockCts のキャンセルで待機を打ち切る（OperationCanceledException で抜ける）
            try
            {
                await _writeLock.WaitAsync(_writeLockCts.Token);
            }
            catch (OperationCanceledException)
            {
                return; // Dispose により待機がキャンセルされた
            }
            catch (ObjectDisposedException)
            {
                return; // 破棄済みなら何もしない（Cancel 後に CTS が破棄されたケースを含む）
            }

            try
            {
                if (_writer == null || IsDisposed)
                    return;

                // 256文字単位で分割して送信（こまめにFlushすることで問題を解決）
                const int CHUNK_SIZE = 256;

                for (int i = 0; i < input.Length; i += CHUNK_SIZE)
                {
                    var chunk = i + CHUNK_SIZE < input.Length
                        ? input.Substring(i, CHUNK_SIZE)
                        : input.Substring(i);

                    await _writer.WriteAsync(chunk);
                    await _writer.FlushAsync();
                    await Task.Delay(20);
                }

                // 統計情報を更新
                TotalBytesWritten += Encoding.UTF8.GetByteCount(input);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or IOException)
            {
                // 書き込みループの途中で Dispose され _writer/パイプが破棄されたケース。
                // 呼び出し元(UIイベント/MCP send_to_session)へ例外を伝播させず握りつぶす
            }
            finally
            {
                try
                {
                    _writeLock.Release();
                }
                catch (ObjectDisposedException)
                {
                    // 書き込み中に Dispose されたケース。解放不要
                }
            }
        }

        // バッファリング関連メソッド
        private void BufferOutput(string data)
        {
            if (IsDisposed)
                return;

            bool startDispatcher = false;
            lock (_outputLock)
            {
                if (!IsDisposed)
                {
                    _outputBuffer.Append(data);
                    if (_outputBuffer.Length >= MAX_BUFFER_SIZE)
                        startDispatcher = EnqueueBufferedOutputLocked();
                }
            }

            if (startDispatcher)
                DispatchPendingOutput();
        }
        
        private void FlushOutputBuffer()
        {
            if (IsDisposed)
                return;

            bool startDispatcher;
            lock (_outputLock)
            {
                startDispatcher = !IsDisposed && EnqueueBufferedOutputLocked();
            }

            if (startDispatcher)
                DispatchPendingOutput();
        }

        private void FlushRemainingOutput(bool waitForDispatch = false)
        {
            bool startDispatcher;
            lock (_outputLock)
            {
                // Dispose後も既に読み取った末尾データは配送する。
                startDispatcher = EnqueueBufferedOutputLocked();
            }

            if (startDispatcher)
                DispatchPendingOutput();

            if (waitForDispatch)
            {
                lock (_outputLock)
                {
                    while (_isDispatchingOutput)
                        Monitor.Wait(_outputLock);
                }
            }
        }

        // _outputLockを保持して呼ぶこと。
        private bool EnqueueBufferedOutputLocked()
        {
            if (_outputBuffer.Length == 0)
                return false;

            _pendingOutput.Enqueue(_outputBuffer.ToString());
            _outputBuffer.Clear();
            if (_isDispatchingOutput)
                return false;

            _isDispatchingOutput = true;
            return true;
        }

        private void DispatchPendingOutput()
        {
            while (true)
            {
                string data;
                lock (_outputLock)
                {
                    if (_pendingOutput.Count == 0)
                    {
                        _isDispatchingOutput = false;
                        Monitor.PulseAll(_outputLock);
                        return;
                    }
                    data = _pendingOutput.Dequeue();
                }

                RaiseDataReceived(data);
            }
        }

        private void RaiseDataReceived(string data)
        {
            var args = new DataReceivedEventArgs(data);
            foreach (EventHandler<DataReceivedEventArgs> handler in DataReceived?.GetInvocationList() ?? [])
            {
                try { handler(this, args); }
                catch (Exception ex) { _logger.LogError(ex, "Error in ConPTY DataReceived subscriber"); }
            }
        }

        private void RaiseProcessExited()
        {
            foreach (EventHandler handler in ProcessExited?.GetInvocationList() ?? [])
            {
                try { handler(this, EventArgs.Empty); }
                catch (Exception ex) { _logger.LogError(ex, "Error in ConPTY ProcessExited subscriber"); }
            }
        }

        // バックグラウンドでパイプを読み取るメソッド
        private async Task ReadPipeAsync(CancellationToken cancellationToken)
        {
            var byteBuffer = new byte[65536]; // 64KB
            var charBuffer = new char[65536]; // 64KB

            while (!cancellationToken.IsCancellationRequested && !IsDisposed)
            {
                try
                {
                    // フロー制御: 一時停止中は再開を待つ
                    if (_outputPaused)
                    {
                        var resumeTask = _resumeTcs?.Task;
                        if (resumeTask != null)
                        {
                            // キャンセルトークンと組み合わせて待機
                            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            linkedCts.CancelAfter(TimeSpan.FromSeconds(30)); // 最大30秒待機

                            try
                            {
                                await resumeTask.WaitAsync(linkedCts.Token);
                            }
                            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                            {
                                // タイムアウトの場合は強制的に再開
                                _logger.LogWarning("Flow control timeout - forcing resume");
                                ResumeOutput();
                            }
                        }
                    }

                    if (_pipeOutStream == null)
                        break;

                    var bytesRead = await _pipeOutStream.ReadAsync(byteBuffer, 0, byteBuffer.Length, cancellationToken);

                    if (bytesRead > 0)
                    {
                        // 統計情報を更新
                        TotalBytesRead += bytesRead;

                        var charsRead = _utf8Decoder.GetChars(byteBuffer, 0, bytesRead, charBuffer, 0);
                        var data = new string(charBuffer, 0, charsRead);

                        RecordStartupOutput(bytesRead, data);

                        if (_enableBuffering)
                        {
                            // バッファリングが有効な場合
                            BufferOutput(data);
                        }
                        else
                        {
                            // 直接イベントを発生
                            RaiseDataReceived(data);
                        }
                    }
                    else
                    {
                        // ストリームが閉じられた場合
                        break;
                    }
                }
                catch (OperationCanceledException)
                {
                    // キャンセルされた場合は正常終了
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error reading from pipe");
                    break;
                }
            }
            
            // バッファリングタイマーを即座に停止
            if (_enableBuffering && _flushTimer != null)
            {
                _flushTimer.Stop();
                _flushTimer.Dispose();
                _flushTimer = null;
            }

            // 終了通知より先に、短時間バッファに残った末尾出力を配送する。
            FlushRemainingOutput(waitForDispatch: true);

            LogStartupSummary("読み取り終了");
            
            // プロセスが終了したことを通知
            RaiseProcessExited();
        }

        private void RecordStartupOutput(int bytesRead, string data)
        {
            if (!_startupDiagnosticsEnabled)
                return;

            var now = _startupStopwatch.ElapsedMilliseconds;
            Interlocked.Add(ref _startupBytesRead, bytesRead);
            Interlocked.Increment(ref _startupChunksRead);
            Volatile.Write(ref _lastOutputElapsedMs, now);

            if (Interlocked.CompareExchange(ref _firstOutputElapsedMs, now, -1) == -1)
            {
                _logger.LogInformation(
                    "[ConPtyStartup] 最初の出力受信: SessionId={SessionId}, ProcessId={ProcessId}, Bytes={Bytes}, Chars={Chars}, ProcessToOutputMs={ProcessToOutputMs}, ReaderToOutputMs={ReaderToOutputMs}",
                    _sessionId, ProcessId, bytesRead, data.Length,
                    ElapsedSince(_processStartedElapsedMs, now), ElapsedSince(_readerStartedElapsedMs, now));
            }

            if (_startupOutputProbe.FeedAndDetectPrintable(data) &&
                Interlocked.CompareExchange(ref _firstPrintableElapsedMs, now, -1) == -1)
            {
                _logger.LogInformation(
                    "[ConPtyStartup] 最初の表示文字受信: SessionId={SessionId}, ProcessId={ProcessId}, ProcessToPrintableMs={ProcessToPrintableMs}, BytesSoFar={Bytes}, ChunksSoFar={Chunks}",
                    _sessionId, ProcessId, ElapsedSince(_processStartedElapsedMs, now),
                    Interlocked.Read(ref _startupBytesRead), Volatile.Read(ref _startupChunksRead));
            }
        }

        private async Task MonitorStartupOutputAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
                LogStartupCheckpoint(3);

                await Task.Delay(TimeSpan.FromSeconds(7), cancellationToken);
                LogStartupCheckpoint(10);
            }
            catch (OperationCanceledException)
            {
                // セッション終了・破棄時の通常経路。
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[ConPtyStartup] 起動診断タスクでエラー: SessionId={SessionId}", _sessionId);
            }
        }

        private void LogStartupCheckpoint(int seconds)
        {
            if (!_startupDiagnosticsEnabled || Volatile.Read(ref _firstPrintableElapsedMs) >= 0)
                return;

            _logger.LogWarning(
                "[ConPtyStartup] {Seconds}秒経過しても表示文字なし: SessionId={SessionId}, ProcessId={ProcessId}, Bytes={Bytes}, Chunks={Chunks}, FirstOutputMs={FirstOutputMs}, LastOutputMs={LastOutputMs}, HasExited={HasExited}, OutputPaused={OutputPaused}, Size={Cols}x{Rows}, ProcessToReaderMs={ProcessToReaderMs}",
                seconds, _sessionId, ProcessId,
                Interlocked.Read(ref _startupBytesRead), Volatile.Read(ref _startupChunksRead),
                ElapsedSince(_processStartedElapsedMs, Volatile.Read(ref _firstOutputElapsedMs)),
                ElapsedSince(_processStartedElapsedMs, Volatile.Read(ref _lastOutputElapsedMs)),
                HasExited, _outputPaused, Cols, Rows,
                ElapsedSince(_processStartedElapsedMs, Volatile.Read(ref _readerStartedElapsedMs)));
        }

        private void LogStartupSummary(string reason)
        {
            if (!_startupDiagnosticsEnabled)
                return;

            _logger.LogInformation(
                "[ConPtyStartup] {Reason}: SessionId={SessionId}, ProcessId={ProcessId}, Bytes={Bytes}, Chunks={Chunks}, FirstOutputMs={FirstOutputMs}, FirstPrintableMs={FirstPrintableMs}, LastOutputMs={LastOutputMs}, HasExited={HasExited}",
                reason, _sessionId, ProcessId,
                Interlocked.Read(ref _startupBytesRead), Volatile.Read(ref _startupChunksRead),
                ElapsedSince(_processStartedElapsedMs, Volatile.Read(ref _firstOutputElapsedMs)),
                ElapsedSince(_processStartedElapsedMs, Volatile.Read(ref _firstPrintableElapsedMs)),
                ElapsedSince(_processStartedElapsedMs, Volatile.Read(ref _lastOutputElapsedMs)),
                HasExited);
        }

        private static long ElapsedSince(long start, long end)
            => start >= 0 && end >= start ? end - start : -1;

        /// <summary>
        /// ANSI/OSC/DCS をまたいで、画面に表示される空白以外の文字を検出する。
        /// チャンク境界でシーケンスが分断されても CSI の終端文字（K/h等）を本文扱いしない。
        /// </summary>
        private sealed class StartupOutputProbe
        {
            private ProbeState _state;

            public bool FeedAndDetectPrintable(string data)
            {
                var found = false;
                foreach (var ch in data)
                {
                    switch (_state)
                    {
                        case ProbeState.Ground:
                            if (ch == '\x1b')
                                _state = ProbeState.Escape;
                            else if (ch == '\u009b')
                                _state = ProbeState.Csi;
                            else if (ch == '\u009d')
                                _state = ProbeState.Osc;
                            else if (ch is '\u0090' or '\u0098' or '\u009e' or '\u009f')
                                _state = ProbeState.ControlString;
                            else if (!char.IsControl(ch) && !char.IsWhiteSpace(ch))
                                found = true;
                            break;

                        case ProbeState.Escape:
                            _state = ch switch
                            {
                                '[' => ProbeState.Csi,
                                ']' => ProbeState.Osc,
                                'P' or 'X' or '^' or '_' => ProbeState.ControlString,
                                >= ' ' and <= '/' => ProbeState.EscapeIntermediate,
                                _ => ProbeState.Ground
                            };
                            break;

                        case ProbeState.EscapeIntermediate:
                            if (ch == '\x1b')
                                _state = ProbeState.Escape;
                            else if (ch is >= '0' and <= '~')
                                _state = ProbeState.Ground;
                            else if (ch is not (>= ' ' and <= '/'))
                                _state = ProbeState.Ground;
                            break;

                        case ProbeState.Csi:
                            if (ch is >= '@' and <= '~')
                                _state = ProbeState.Ground;
                            break;

                        case ProbeState.Osc:
                            if (ch == '\a')
                                _state = ProbeState.Ground;
                            else if (ch == '\x1b')
                                _state = ProbeState.OscEscape;
                            break;

                        case ProbeState.OscEscape:
                            _state = ch == '\\' ? ProbeState.Ground : ProbeState.Osc;
                            break;

                        case ProbeState.ControlString:
                            if (ch == '\x1b')
                                _state = ProbeState.ControlStringEscape;
                            break;

                        case ProbeState.ControlStringEscape:
                            _state = ch == '\\' ? ProbeState.Ground : ProbeState.ControlString;
                            break;
                    }
                }
                return found;
            }

            private enum ProbeState
            {
                Ground,
                Escape,
                EscapeIntermediate,
                Csi,
                Osc,
                OscEscape,
                ControlString,
                ControlStringEscape
            }
        }

        public void Resize(int cols, int rows)
        {
            lock (_pseudoConsoleLock)
            {
                if (_hPC == IntPtr.Zero || IsDisposed)
                    return;

                // 0以下・short範囲外はConPTYに渡せない（shortキャストで壊れた値になる）
                if (cols <= 0 || rows <= 0 || cols > short.MaxValue || rows > short.MaxValue)
                {
                    _logger.LogWarning("無効なリサイズ要求を無視: {Cols}x{Rows}", cols, rows);
                    return;
                }

                // 同サイズなら何もしない。ConPTY は ResizePseudoConsole のたびに
                // ビューポート全体を「通常のスクロール出力」として再送してくるため、
                // 無駄な呼び出しは画面・スクロールバック多重化の原因になる
                if (Cols == cols && Rows == rows)
                {
                    return;
                }

                var size = new COORD { X = (short)cols, Y = (short)rows };
                var hr = ResizePseudoConsole(_hPC, size);
                if (hr != 0)
                {
                    // 失敗時は Cols/Rows を更新しない（実際のConPTYサイズと状態バッファの不一致を防ぐ）
                    _logger.LogWarning("ResizePseudoConsole failed with HRESULT: 0x{Hr:X8} ({Cols}x{Rows})", hr, cols, rows);
                    return;
                }

                Cols = cols;
                Rows = rows;

                // xterm.jsの場合、リサイズ後に追加の処理は不要
                // xterm.js側でリフローを処理する
            }
        }

        /// <summary>
        /// 出力読み取りを一時停止する（フロー制御用）
        /// クライアント側のバッファが溜まりすぎた時に呼び出す
        /// </summary>
        public void PauseOutput()
        {
            lock (_pauseLock)
            {
                if (!_outputPaused)
                {
                    _outputPaused = true;
                    _resumeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _logger.LogDebug("Output paused (flow control)");
                }
            }
        }

        /// <summary>
        /// 出力読み取りを再開する（フロー制御用）
        /// クライアント側のバッファが処理されたら呼び出す
        /// </summary>
        public void ResumeOutput()
        {
            lock (_pauseLock)
            {
                if (_outputPaused)
                {
                    _outputPaused = false;
                    _resumeTcs?.TrySetResult(true);
                    _resumeTcs = null;
                    _logger.LogDebug("Output resumed (flow control)");
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposeState, 1) != 0) return;

            // 書き込み待機を解除（_writeLock.WaitAsync で待機中の WriteAsync を抜けさせる）
            try
            {
                _writeLockCts.Cancel();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cancel write lock waiters during disposal");
            }

            // フロー制御を解除（読み取りタスクがブロックされている場合に備えて）
            lock (_pauseLock)
            {
                _outputPaused = false;
                _resumeTcs?.TrySetResult(true);
                _resumeTcs = null;
            }

            // 読み取りタスクをキャンセル
            try
            {
                _readCancellationTokenSource?.Cancel();
                // パイプ読み取りを先に解除し、待機がタイムアウトしにくいようにする。
                _pipeOutStream?.Dispose();
                _readTask?.Wait(1000); // 1秒待機
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to wait for read task during disposal");
            }

            // タイマーを停止
            if (_enableBuffering && _flushTimer != null)
            {
                _flushTimer.Stop();
                _flushTimer.Dispose();
                _flushTimer = null;
                
                // 残りのバッファを同期的にフラッシュ
                try
                {
                    FlushRemainingOutput();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to flush remaining buffer during disposal");
                }
            }
            
            // ストリームを破棄
            _writer?.Dispose();
            _pipeInStream?.Dispose();
            _pipeOutStream?.Dispose();
            
            _writeLock?.Dispose();

            // プロセスを終了（bun など孫プロセスが残らないようプロセスツリーごと kill）
            if (_process != null && !_process.HasExited)
            {
                try
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(1000); // 1秒待機
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to terminate process tree");
                }
            }

            // プロセスとハンドルを破棄
            lock (_pseudoConsoleLock)
            {
                if (_hPC != IntPtr.Zero)
                {
                    ClosePseudoConsole(_hPC);
                    _hPC = IntPtr.Zero;
                }
            }

            if (_hPipeIn != IntPtr.Zero)
            {
                CloseHandle(_hPipeIn);
                _hPipeIn = IntPtr.Zero;
            }

            if (_hPipeOut != IntPtr.Zero)
            {
                CloseHandle(_hPipeOut);
                _hPipeOut = IntPtr.Zero;
            }

            _process?.Dispose();
            _readCancellationTokenSource?.Dispose();
            _writeLockCts.Dispose();
        }

        // P/Invoke定義
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, IntPtr lpPipeAttributes, uint nSize);
        
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern void ClosePseudoConsole(IntPtr hPC);

        // HRESULT を返すAPI（bool宣言だと S_OK(0)=false と逆転判定になる）
        [DllImport("kernel32.dll")]
        private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr Attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcess(
            string? lpApplicationName,
            string lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string? lpCurrentDirectory,
            ref STARTUPINFOEX lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [StructLayout(LayoutKind.Sequential)]
        private struct COORD
        {
            public short X;
            public short Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct STARTUPINFO
        {
            public int cb;
            public IntPtr lpReserved;
            public IntPtr lpDesktop;
            public IntPtr lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct STARTUPINFOEX
        {
            public STARTUPINFO StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public uint dwProcessId;
            public uint dwThreadId;
        }
    }
}
