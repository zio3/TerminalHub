// ConPTY が VS16 合成絵文字（基底文字+U+FE0F）を何セル幅で扱うかを実測するプローブ。
//
// 仕組み: ConPTY 内で PowerShell を起動し、行頭(CR)から被験文字を書いた直後に
// [Console]::CursorLeft（= GetConsoleScreenBufferInfo）を読む。返る X 座標が
// そのまま「ConPTY(conhost) が何桁カーソルを進めたか」＝ConPTY の幅の意見になる。
// あわせて ConPTY が親へ再送出した生 VT 列もファイルに保存する（再描画時の桁配置の検証用）。
using System.Runtime.InteropServices;
using System.Text;

internal static class Program
{
    private const int TestCols = 100;
    private const int TestRows = 40;

    private static int Main()
    {
        // 被験文字: name / コードポイント列
        // ConvertFromUtf32 でビルドし、ソースファイルのエンコーディングに依存しない
        var tests = new (string Name, int[] CodePoints)[]
        {
            ("ascii_A",        new[] { 0x41 }),                 // 基準: 幅1のはず
            ("kanji_nichi",    new[] { 0x65E5 }),               // 基準: 幅2のはず（日）
            ("check_2705",     new[] { 0x2705 }),               // ✅ 生まれつき全角(EAW=W)
            ("mic_1F3A4",      new[] { 0x1F3A4 }),              // 🎤 生まれつき全角
            ("sun_2600",       new[] { 0x2600 }),               // ☀ 基底のみ(EAW=N)
            ("sun_2600_FE0F",  new[] { 0x2600, 0xFE0F }),       // ☀️ VS16型
            ("warn_26A0_FE0F", new[] { 0x26A0, 0xFE0F }),       // ⚠️ VS16型
            ("pause_23F8_FE0F",new[] { 0x23F8, 0xFE0F }),       // ⏸️ VS16型
            ("play_25B6_FE0F", new[] { 0x25B6, 0xFE0F }),       // ▶️ VS16型
            ("scale_2696_FE0F",new[] { 0x2696, 0xFE0F }),       // ⚖️ VS16型
        };

        // PowerShell スクリプトを組み立てて -EncodedCommand で渡す（エンコーディング事故防止）。
        // 親プロセス（このプローブ）が非コンソール環境だと子の std ハンドルが汚染され
        // [Console]::CursorLeft が使えないため、CONOUT$ を直接開いて conhost に聞く。
        var sb = new StringBuilder();
        sb.AppendLine("""
            Add-Type -TypeDefinition @'
            using System;
            using System.Runtime.InteropServices;
            public static class Probe {
              [StructLayout(LayoutKind.Sequential)] public struct COORD { public short X; public short Y; }
              [StructLayout(LayoutKind.Sequential)] public struct SMALL_RECT { public short L; public short T; public short R; public short B; }
              [StructLayout(LayoutKind.Sequential)] public struct CSBI { public COORD dwSize; public COORD dwCursorPosition; public short wAttributes; public SMALL_RECT srWindow; public COORD dwMaximumWindowSize; }
              [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)] public static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);
              [DllImport("kernel32.dll", SetLastError=true)] public static extern bool GetConsoleScreenBufferInfo(IntPtr h, out CSBI info);
              [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)] public static extern bool WriteConsoleW(IntPtr h, string s, int n, out int written, IntPtr r);
              public static IntPtr H = CreateFileW("CONOUT$", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
              public static void W(string s){ int n; WriteConsoleW(H, s, s.Length, out n, IntPtr.Zero); }
              public static int X(){ CSBI i; GetConsoleScreenBufferInfo(H, out i); return i.dwCursorPosition.X; }
            }
            '@
            """);
        foreach (var (name, cps) in tests)
        {
            var expr = string.Join(" + ", cps.Select(cp => $"[char]::ConvertFromUtf32(0x{cp:X})"));
            sb.AppendLine($"$s = {expr}");
            sb.AppendLine("[Probe]::W([string][char]13)");      // CR で桁0へ
            sb.AppendLine("[Probe]::W($s)");
            sb.AppendLine("$x = [Probe]::X()");
            sb.AppendLine($"[Probe]::W(\"`r`nRESULT {name} width=$x`r`n\")");
        }
        sb.AppendLine("[Probe]::W(\"PROBE_DONE`r`n\")");
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(sb.ToString()));
        var cmdline = $"powershell.exe -NoProfile -NoLogo -ExecutionPolicy Bypass -EncodedCommand {encoded}";

        var raw = RunInConPty(cmdline, out var exitCode);

        var rawPath = Path.Combine(Path.GetTempPath(), "conpty-width-probe-raw.bin");
        File.WriteAllBytes(rawPath, raw);

        var text = Encoding.UTF8.GetString(raw);
        Console.WriteLine("=== RESULT lines (ConPTY=conhost のカーソル前進量) ===");
        foreach (var line in text.Split('\n'))
        {
            var t = line.Trim('\r', ' ');
            if (t.StartsWith("RESULT ") || t == "PROBE_DONE")
            {
                Console.WriteLine(t);
            }
        }
        Console.WriteLine($"=== raw VT output: {raw.Length} bytes -> {rawPath} ===");
        Console.WriteLine($"child exit code: {exitCode}");
        return 0;
    }

    /// <summary>ConPTY 内でコマンドを実行し、親側パイプに流れてきた生バイト列を返す。</summary>
    private static byte[] RunInConPty(string cmdline, out uint exitCode)
    {
        CreatePipe(out var inRead, out var inWrite, IntPtr.Zero, 0);
        CreatePipe(out var outRead, out var outWrite, IntPtr.Zero, 0);

        var size = new COORD { X = TestCols, Y = TestRows };
        var hr = CreatePseudoConsole(size, inRead, outWrite, 0, out var hPC);
        if (hr != 0)
        {
            throw new InvalidOperationException($"CreatePseudoConsole failed: 0x{hr:X}");
        }
        // ConPTY 側へ渡した端は内部で複製されるため親では閉じる
        CloseHandle(inRead);
        CloseHandle(outWrite);

        var lpSize = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref lpSize);
        var attrList = Marshal.AllocHGlobal(lpSize);
        if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref lpSize))
        {
            throw new InvalidOperationException("InitializeProcThreadAttributeList failed");
        }
        if (!UpdateProcThreadAttribute(attrList, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                hPC, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
        {
            throw new InvalidOperationException("UpdateProcThreadAttribute failed");
        }

        var si = new STARTUPINFOEX();
        si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
        si.lpAttributeList = attrList;

        if (!CreateProcessW(null, cmdline, IntPtr.Zero, IntPtr.Zero, false,
                EXTENDED_STARTUPINFO_PRESENT, IntPtr.Zero, null, ref si, out var pi))
        {
            throw new InvalidOperationException($"CreateProcess failed: {Marshal.GetLastWin32Error()}");
        }

        // 出力読み取り（子の終了後、パイプが枯れるまで読み切る）
        var ms = new MemoryStream();
        var readerDone = new ManualResetEventSlim(false);
        var reader = new Thread(() =>
        {
            var buf = new byte[4096];
            using var fs = new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(outRead, ownsHandle: false), FileAccess.Read);
            try
            {
                int n;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                {
                    lock (ms) { ms.Write(buf, 0, n); }
                }
            }
            catch (IOException) { /* パイプ切断 = 正常終了 */ }
            readerDone.Set();
        });
        reader.IsBackground = true;
        reader.Start();

        WaitForSingleObject(pi.hProcess, 30_000);
        GetExitCodeProcess(pi.hProcess, out exitCode);

        // ConPTY を閉じるとパイプが切断され reader が抜ける
        ClosePseudoConsole(hPC);
        readerDone.Wait(5_000);
        CloseHandle(outRead);
        CloseHandle(inWrite);
        CloseHandle(pi.hProcess);
        CloseHandle(pi.hThread);
        DeleteProcThreadAttributeList(attrList);
        Marshal.FreeHGlobal(attrList);

        lock (ms) { return ms.ToArray(); }
    }

    private const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x20016;
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD { public short X; public short Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, IntPtr lpPipeAttributes, int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
