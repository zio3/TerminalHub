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
        Task<ConPtySession> CreateSessionAsync(string command, string? arguments, string? workingDirectory = null, int cols = 80, int rows = 24);
    }
    
    public class DataReceivedEventArgs : EventArgs
    {
        public string Data { get; }
        
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

        public Task<ConPtySession> CreateSessionAsync(string command, string? arguments, string? workingDirectory = null, int cols = 80, int rows = 24)
        {
            return Task.FromResult(new ConPtySession(command, arguments, workingDirectory, _logger, cols, rows, true)); // バッファリング有効
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
        private volatile bool _disposed;
        private readonly Decoder _utf8Decoder = Encoding.UTF8.GetDecoder();
        private Task? _readTask;
        private CancellationTokenSource? _readCancellationTokenSource;
        private bool _started = false;
        
        // 環境変数フラグ
        private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        
        // パフォーマンス最適化設定
        private bool _enableBuffering = true; // バッファリング有効
        private readonly SemaphoreSlim _outputSemaphore = new(1, 1);
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

        public ConPtySession(string command, string? arguments, string? workingDirectory, ILogger logger, int cols = 80, int rows = 24, bool enableBuffering = true)
        {
            _logger = logger;
            Cols = cols;
            Rows = rows;
            _enableBuffering = enableBuffering;
            
            // バッファリング用タイマーの初期化（有効時のみ）
            if (_enableBuffering)
            {
                _flushTimer = new System.Timers.Timer(FLUSH_INTERVAL_MS);
                _flushTimer.Elapsed += async (sender, e) =>
                {
                    if (!_disposed)
                        await FlushOutputBuffer();
                };
                _flushTimer.AutoReset = true;
            }

            InitializeConPty(command, arguments, workingDirectory);
        }
        
        public void Start()
        {
            if (_started || _disposed)
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
            _readTask = Task.Run(() => ReadPipeAsync(_readCancellationTokenSource.Token));
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
                CreatePipe(out var hPipeIn, out hWritePipe, IntPtr.Zero, pipeBufferSize);
                CreatePipe(out hReadPipe, out var hPipeOut, IntPtr.Zero, pipeBufferSize);

                _hPipeIn = hPipeIn;
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

                // プロセス属性リストの初期化
                var lpSize = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref lpSize);
                startupInfo.lpAttributeList = Marshal.AllocHGlobal(lpSize);
                InitializeProcThreadAttributeList(startupInfo.lpAttributeList, 1, 0, ref lpSize);

            // 擬似コンソールの属性を設定
            UpdateProcThreadAttribute(
                startupInfo.lpAttributeList,
                0,
                (IntPtr)ConPtyTerminalConstants.ProcThreadAttributePseudoConsole,
                _hPC,
                (IntPtr)IntPtr.Size,
                IntPtr.Zero,
                IntPtr.Zero);

            // プロセスの作成
            var processInfo = new PROCESS_INFORMATION();
            // argumentsが空の場合は余分なスペースを追加しない
            var cmdline = string.IsNullOrWhiteSpace(arguments) ? command : $"{command} {arguments}";

            _logger.LogInformation($"Creating process: {cmdline} in directory: {workingDirectory ?? "current"}");
            
            // XTerm互換のための環境変数を設定
            var envBlock = CreateEnvironmentBlock();
            
            var result = CreateProcess(
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
                
            // 環境変数ブロックを解放
            if (envBlock != IntPtr.Zero)
                Marshal.FreeHGlobal(envBlock);
                
            if (!result)
            {
                var error = Marshal.GetLastWin32Error();
                _logger.LogError($"CreateProcess failed with error: {error}");
                throw new InvalidOperationException($"Failed to create process: {error}");
            }

            _process = Process.GetProcessById((int)processInfo.dwProcessId);

                processInfoHandle = processInfo.hProcess;
                threadInfoHandle = processInfo.hThread;
                
                // ストリームの作成
                var pipeIn = new Microsoft.Win32.SafeHandles.SafeFileHandle(hWritePipe, true);
                var pipeOut = new Microsoft.Win32.SafeHandles.SafeFileHandle(hReadPipe, true);

                _pipeInStream = new FileStream(pipeIn, FileAccess.Write);
                _pipeOutStream = new FileStream(pipeOut, FileAccess.Read);
                
                // StreamWriterを作成（XTerm向けにUTF-8、改行コードLF）
                // バッファサイズを65KBに増やして長い文字列の問題を解決
                _writer = new StreamWriter(_pipeInStream, new UTF8Encoding(false), bufferSize: 65536)
                {
                    AutoFlush = true,
                    NewLine = "\n"  // LF改行（Unix形式）
                };
                
                // SafeFileHandleに所有権を移したので、元のハンドルは無効化
                hWritePipe = IntPtr.Zero;
                hReadPipe = IntPtr.Zero;

                // ハンドルのクリーンアップ
                CloseHandle(processInfoHandle);
                CloseHandle(threadInfoHandle);
                DeleteProcThreadAttributeList(startupInfo.lpAttributeList);
                Marshal.FreeHGlobal(startupInfo.lpAttributeList);
            }
            catch
            {
                // エラー時のクリーンアップ
                if (hWritePipe != IntPtr.Zero) CloseHandle(hWritePipe);
                if (hReadPipe != IntPtr.Zero) CloseHandle(hReadPipe);
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
            if (_writer != null && !_disposed)
            {
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
        }

        // バッファリング関連メソッド
        private async Task BufferOutput(string data)
        {
            if (_disposed || _outputSemaphore == null)
                return;
                
            try
            {
                await _outputSemaphore.WaitAsync();
                try
                {
                    if (!_disposed)
                    {
                        _outputBuffer.Append(data);
                        
                        // バッファサイズが上限に達したら即座にフラッシュ
                        if (_outputBuffer.Length >= MAX_BUFFER_SIZE)
                        {
                            var bufferedData = _outputBuffer.ToString();
                            _outputBuffer.Clear();
                            
                            DataReceived?.Invoke(this, new DataReceivedEventArgs(bufferedData));
                        }
                    }
                }
                finally
                {
                    if (!_disposed)
                        _outputSemaphore.Release();
                }
            }
            catch (ObjectDisposedException)
            {
                // セマフォが破棄されている場合は無視
            }
        }
        
        private async Task FlushOutputBuffer()
        {
            if (_disposed || _outputSemaphore == null)
                return;
                
            try
            {
                await _outputSemaphore.WaitAsync();
                try
                {
                    if (!_disposed && _outputBuffer.Length > 0)
                    {
                        var data = _outputBuffer.ToString();
                        _outputBuffer.Clear();
                        
                        // メインスレッドでイベントを発生
                        DataReceived?.Invoke(this, new DataReceivedEventArgs(data));
                    }
                }
                finally
                {
                    if (!_disposed)
                        _outputSemaphore.Release();
                }
            }
            catch (ObjectDisposedException)
            {
                // セマフォが破棄されている場合は無視
            }
        }

        // バックグラウンドでパイプを読み取るメソッド
        private async Task ReadPipeAsync(CancellationToken cancellationToken)
        {
            var byteBuffer = new byte[65536]; // 64KB
            var charBuffer = new char[65536]; // 64KB

            while (!cancellationToken.IsCancellationRequested && !_disposed)
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

                        if (_enableBuffering)
                        {
                            // バッファリングが有効な場合
                            await BufferOutput(data);
                        }
                        else
                        {
                            // 直接イベントを発生
                            DataReceived?.Invoke(this, new DataReceivedEventArgs(data));
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
            
            // プロセスが終了したことを通知
            ProcessExited?.Invoke(this, EventArgs.Empty);
        }

        public void Resize(int cols, int rows)
        {
            if (_hPC != IntPtr.Zero)
            {
                Cols = cols;
                Rows = rows;
                var size = new COORD { X = (short)cols, Y = (short)rows };
                ResizePseudoConsole(_hPC, size);

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
            if (_disposed) return;

            // まず破棄フラグを設定
            _disposed = true;

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
                    if (_outputBuffer.Length > 0)
                    {
                        var data = _outputBuffer.ToString();
                        DataReceived?.Invoke(this, new DataReceivedEventArgs(data));
                    }
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
            
            // セマフォを破棄
            _outputSemaphore?.Dispose();

            // プロセスを終了
            if (_process != null && !_process.HasExited)
            {
                try
                {
                    _process.Kill();
                    _process.WaitForExit(1000); // 1秒待機
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to terminate process");
                }
            }

            // プロセスとハンドルを破棄
            if (_hPC != IntPtr.Zero)
            {
                ClosePseudoConsole(_hPC);
                _hPC = IntPtr.Zero;
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
        }

        // P/Invoke定義
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, IntPtr lpPipeAttributes, uint nSize);
        
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern void ClosePseudoConsole(IntPtr hPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ResizePseudoConsole(IntPtr hPC, COORD size);

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