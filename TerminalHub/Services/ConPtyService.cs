using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TerminalHub.Constants;

namespace TerminalHub.Services
{
    public interface IConPtyService
    {
        Task<ConPtySession> CreateSessionAsync(string command, string? arguments, string? workingDirectory = null, int cols = 80, int rows = 24);
    }

    public class ConPtyService : IConPtyService
    {
        private readonly ILogger<ConPtyService> _logger;

        public ConPtyService(ILogger<ConPtyService> logger)
        {
            _logger = logger;
        }

        public async Task<ConPtySession> CreateSessionAsync(string command, string? arguments, string? workingDirectory = null, int cols = 80, int rows = 24)
        {
            // Console.WriteLine($"[ConPtyService] CreateSessionAsync: {command}");
            
            try
            {
                return await Task.Run(() => new ConPtySession(command, arguments, workingDirectory, _logger, cols, rows));
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "Failed to create ConPTY session: {Message}", ex.Message);
                throw new InvalidOperationException($"ターミナルの初期化に失敗しました: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error creating ConPTY session");
                throw new InvalidOperationException("ターミナルの初期化中に予期しないエラーが発生しました。", ex);
            }
        }
    }

    public class ConPtySession : IDisposable
    {
        private Process? _process;
        private IntPtr _hPC = IntPtr.Zero;
        private IntPtr _hPipeIn = IntPtr.Zero;
        private IntPtr _hPipeOut = IntPtr.Zero;
        private readonly ILogger _logger;
        private StreamWriter? _writer;
        private StreamReader? _reader;
        private bool _disposed;

        private int _cols;
        private int _rows;
        private readonly SemaphoreSlim _readSemaphore = new(1, 1);

        public ConPtySession(string command, string? arguments, string? workingDirectory, ILogger logger, int cols = 80, int rows = 24)
        {
            // Console.WriteLine($"[ConPtySession] コンストラクタ開始");
            _logger = logger;
            _cols = cols;
            _rows = rows;
            InitializeConPty(command, arguments, workingDirectory);
            // Console.WriteLine($"[ConPtySession] コンストラクタ完了");
        }

        private void InitializeConPty(string command, string? arguments, string? workingDirectory)
        {
            // Console.WriteLine($"[ConPtySession] InitializeConPty開始");
            
            // ConPTYの初期化
            var startupInfo = new STARTUPINFOEX();
            startupInfo.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            // Console.WriteLine($"[ConPtySession] STARTUPINFOEX初期化完了");

            IntPtr hWritePipe = IntPtr.Zero;
            IntPtr hReadPipe = IntPtr.Zero;
            IntPtr processInfoHandle = IntPtr.Zero;
            IntPtr threadInfoHandle = IntPtr.Zero;
            
            try
            {
                // パイプの作成
                // Console.WriteLine($"[ConPtySession] パイプ作成開始");
                CreatePipe(out var hPipeIn, out hWritePipe, IntPtr.Zero, 0);
                CreatePipe(out hReadPipe, out var hPipeOut, IntPtr.Zero, 0);
                // Console.WriteLine($"[ConPtySession] パイプ作成完了");

                _hPipeIn = hPipeIn;
                _hPipeOut = hPipeOut;

            // ConPTYの作成
            // Console.WriteLine($"[ConPtySession] CreatePseudoConsole呼び出し開始");
            var size = new COORD { X = (short)_cols, Y = (short)_rows };
            var hr = CreatePseudoConsole(size, hPipeIn, hPipeOut, 0, out _hPC);
            if (hr != 0)
            {
                Console.WriteLine($"[ConPtySession] CreatePseudoConsole失敗: HRESULT={hr:X}");
                _logger.LogError($"CreatePseudoConsole failed with HRESULT: {hr:X}");
                throw new InvalidOperationException($"Failed to create pseudo console: {hr:X}");
            }
            // Console.WriteLine($"[ConPtySession] CreatePseudoConsole成功");

                // プロセス属性リストの初期化
                // Console.WriteLine($"[ConPtySession] プロセス属性リスト初期化開始");
                var lpSize = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref lpSize);
                startupInfo.lpAttributeList = Marshal.AllocHGlobal(lpSize);
                InitializeProcThreadAttributeList(startupInfo.lpAttributeList, 1, 0, ref lpSize);
                // Console.WriteLine($"[ConPtySession] プロセス属性リスト初期化完了");

            // 擬似コンソールの属性を設定
            // Console.WriteLine($"[ConPtySession] UpdateProcThreadAttribute呼び出し");
            UpdateProcThreadAttribute(
                startupInfo.lpAttributeList,
                0,
                (IntPtr)TerminalConstants.ProcThreadAttributePseudoConsole,
                _hPC,
                (IntPtr)IntPtr.Size,
                IntPtr.Zero,
                IntPtr.Zero);
            // Console.WriteLine($"[ConPtySession] UpdateProcThreadAttribute完了");

            // プロセスの作成
            var processInfo = new PROCESS_INFORMATION();
            // argumentsが空の場合は余分なスペースを追加しない
            var cmdline = string.IsNullOrWhiteSpace(arguments) ? command : $"{command} {arguments}";

            // Console.WriteLine($"[ConPtySession] CreateProcess: {cmdline}");
            _logger.LogInformation($"Creating process: {cmdline} in directory: {workingDirectory ?? "current"}");
            
            var result = CreateProcess(
                null,
                cmdline,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                TerminalConstants.ExtendedStartupinfoPresent,
                IntPtr.Zero,
                workingDirectory,
                ref startupInfo,
                out processInfo);
                
            if (!result)
            {
                var error = Marshal.GetLastWin32Error();
                Console.WriteLine($"[ConPtySession] CreateProcess失敗: Win32Error={error}");
                _logger.LogError($"CreateProcess failed with error: {error}");
                throw new InvalidOperationException($"Failed to create process: {error}");
            }

            // Console.WriteLine($"[ConPtySession] CreateProcess成功: PID={processInfo.dwProcessId}");
            _process = Process.GetProcessById((int)processInfo.dwProcessId);
            // Console.WriteLine($"[ConPtySession] Process取得完了");

                processInfoHandle = processInfo.hProcess;
                threadInfoHandle = processInfo.hThread;
                
                // ストリームの作成
                // Console.WriteLine($"[ConPtySession] ストリーム作成開始");
                Microsoft.Win32.SafeHandles.SafeFileHandle? pipeIn = null;
                Microsoft.Win32.SafeHandles.SafeFileHandle? pipeOut = null;
                
                try
                {
                    pipeIn = new Microsoft.Win32.SafeHandles.SafeFileHandle(hWritePipe, true);
                    pipeOut = new Microsoft.Win32.SafeHandles.SafeFileHandle(hReadPipe, true);

                    _writer = new StreamWriter(new FileStream(pipeIn, FileAccess.Write), Encoding.UTF8) { AutoFlush = true };
                    _reader = new StreamReader(new FileStream(pipeOut, FileAccess.Read), Encoding.UTF8);
                    // Console.WriteLine($"[ConPtySession] ストリーム作成完了");
                    
                    // SafeFileHandleに所有権を移したので、元のハンドルは無効化
                    hWritePipe = IntPtr.Zero;
                    hReadPipe = IntPtr.Zero;
                }
                catch
                {
                    // エラー時はSafeFileHandleを適切に破棄
                    pipeIn?.Dispose();
                    pipeOut?.Dispose();
                    throw;
                }

                // ハンドルのクリーンアップ
                // Console.WriteLine($"[ConPtySession] ハンドルクリーンアップ開始");
                CloseHandle(processInfoHandle);
                CloseHandle(threadInfoHandle);
                DeleteProcThreadAttributeList(startupInfo.lpAttributeList);
                Marshal.FreeHGlobal(startupInfo.lpAttributeList);
                // Console.WriteLine($"[ConPtySession] InitializeConPty完了");
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

        public async Task WriteAsync(string input)
        {
            if (_writer != null)
            {
                await _writer.WriteAsync(input);
                await _writer.FlushAsync();
            }
        }

        public async Task<string?> ReadLineAsync()
        {
            if (_disposed)
                return null;
                
            await _readSemaphore.WaitAsync();
            try
            {
                if (_disposed || _reader == null)
                    return null;
                    
                return await _reader.ReadLineAsync();
            }
            finally
            {
                if (!_disposed)
                    _readSemaphore.Release();
            }
        }

        public async Task<int> ReadAsync(char[] buffer, int offset, int count)
        {
            if (_disposed)
                return 0;
                
            await _readSemaphore.WaitAsync();
            try
            {
                if (_disposed || _reader == null)
                    return 0;
                    
                return await _reader.ReadAsync(buffer, offset, count);
            }
            finally
            {
                if (!_disposed)
                    _readSemaphore.Release();
            }
        }

        public Stream? GetOutputStream()
        {
            return _reader?.BaseStream;
        }

        public async Task<string> ReadAvailableOutputAsync(int timeoutMs = 2000)
        {
            if (_disposed || _reader == null)
                return string.Empty;

            const int maxOutputSize = 1024 * 1024; // 1MB制限
            var output = new StringBuilder();
            var buffer = new char[1024];
            var startTime = DateTime.Now;

            await _readSemaphore.WaitAsync();
            try
            {
                while ((DateTime.Now - startTime).TotalMilliseconds < timeoutMs)
                {
                    if (_disposed || _reader == null)
                        break;

                    // データが利用可能かチェック
                    if (_reader.BaseStream.CanRead && _reader.BaseStream.Length > 0)
                    {
                        var bytesRead = await _reader.ReadAsync(buffer, 0, buffer.Length);
                        if (bytesRead > 0)
                        {
                            // メモリ使用量の制限
                            if (output.Length + bytesRead > maxOutputSize)
                            {
                                // 古いデータを削除して新しいデータを追加
                                var removeLength = Math.Min(output.Length, (output.Length + bytesRead) - maxOutputSize);
                                output.Remove(0, removeLength);
                                _logger.LogWarning($"出力バッファサイズが上限({maxOutputSize}バイト)に達したため、古いデータを削除しました");
                            }
                            
                            output.Append(buffer, 0, bytesRead);
                            continue;
                        }
                    }

                    // 短い待機
                    await Task.Delay(100);
                }
            }
            catch (Exception ex)
            {
                // エラーが発生した場合は空文字を返す
                _logger.LogError(ex, "ReadAvailableOutputAsync でエラーが発生しました");
            }
            finally
            {
                if (!_disposed)
                    _readSemaphore.Release();
            }

            return output.ToString();
        }

        public void Resize(int cols, int rows)
        {
            if (_hPC != IntPtr.Zero)
            {
                _cols = cols;
                _rows = rows;
                var size = new COORD { X = (short)cols, Y = (short)rows };
                ResizePseudoConsole(_hPC, size);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            // まず破棄フラグを設定
            _disposed = true;

            // ストリームを破棄
            try { _writer?.Dispose(); } catch { }
            try { _reader?.Dispose(); } catch { }

            // プロセスを終了
            if (_process != null)
            {
                try
                {
                    // HasExitedプロパティへのアクセス自体が例外を投げる可能性があるため、try内で処理
                    if (!_process.HasExited)
                    {
                        _process.Kill();
                        _process.WaitForExit(1000); // 1秒待機
                    }
                }
                catch (InvalidOperationException)
                {
                    // プロセスが既に終了している場合
                    _logger.LogDebug("Process has already exited");
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

            try { _process?.Dispose(); } catch { }
            
            // 最後にセマフォを破棄
            try { _readSemaphore?.Dispose(); } catch { }
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