using System.Globalization;

namespace TerminalHub.Services
{
    /// <summary>
    /// 入力欄へドロップされたファイルの受け皿。
    /// ブラウザはドロップされたファイルのフルパスを渡さない（取れるのは名前と中身だけ）ため、
    /// 中身を受け取ってアプリ設定フォルダ配下（<c>%LOCALAPPDATA%\TerminalHub\drops\yyyyMMdd\</c>）へ保存し、
    /// その保存先パスを入力欄へ挿入してもらう。保存先は日付フォルダで区切り、7 日より古い日付フォルダは掃除する。
    /// 作業フォルダには書かない（利用者のリポジトリに見慣れないファイルを増やさないため）。
    /// </summary>
    public static class DroppedFileStore
    {
        /// <summary>1 ファイルの上限（バイト）。これより大きいものは受け取らず 413 を返す。</summary>
        public const long MaxBytes = 20L * 1024 * 1024;

        /// <summary>日付フォルダを保持する日数。</summary>
        public const int KeepDays = 7;

        private const string DateFolderFormat = "yyyyMMdd";

        /// <summary>
        /// ブラウザから届いたファイル名を Windows のファイル名として安全な形に整える。
        /// パス区切りより後ろだけを使い、使えない文字は '_' に置き換え、末尾のドットと空白を落とす。
        /// 空になったら "file"。長すぎる名前は拡張子を残して切る。
        /// </summary>
        public static string SanitizeFileName(string? name)
        {
            var s = (name ?? string.Empty).Trim();
            var cut = s.LastIndexOfAny(new[] { '\\', '/' });
            if (cut >= 0)
            {
                s = s[(cut + 1)..];
            }

            var invalid = Path.GetInvalidFileNameChars();
            var chars = s.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (Array.IndexOf(invalid, chars[i]) >= 0 || char.IsControl(chars[i]))
                {
                    chars[i] = '_';
                }
            }
            s = new string(chars).TrimEnd('.', ' ');

            if (s.Length == 0 || s == "_" || s.All(c => c == '.'))
            {
                s = "file";
            }

            const int maxLength = 120;
            if (s.Length > maxLength)
            {
                var ext = Path.GetExtension(s);
                if (ext.Length > 20) ext = string.Empty;
                s = s[..(maxLength - ext.Length)] + ext;
            }
            return s;
        }

        /// <summary>
        /// 同名があれば "name-2.ext", "name-3.ext" … と番号を付けて、まだ存在しないパスを返す。
        /// </summary>
        public static string ReserveUniquePath(string folder, string fileName)
        {
            var candidate = Path.Combine(folder, fileName);
            if (!File.Exists(candidate))
            {
                return candidate;
            }
            var stem = Path.GetFileNameWithoutExtension(fileName);
            var ext = Path.GetExtension(fileName);
            for (var n = 2; ; n++)
            {
                candidate = Path.Combine(folder, $"{stem}-{n}{ext}");
                if (!File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        /// <summary>
        /// 本文ストリームを日付フォルダへ保存し、保存先のフルパスを返す。
        /// 申告サイズ（Content-Length）が上限を超えていれば読む前に、申告が無い／嘘でも
        /// 実際に上限を超えた時点で <see cref="DroppedFileTooLargeException"/> を投げ、書きかけは消す。
        /// </summary>
        public static async Task<string> SaveAsync(string root, string? name, Stream body, long? contentLength, DateTime now, CancellationToken ct)
        {
            if (contentLength.HasValue && contentLength.Value > MaxBytes)
            {
                throw new DroppedFileTooLargeException();
            }

            var folder = Path.Combine(root, now.ToString(DateFolderFormat, CultureInfo.InvariantCulture));
            Directory.CreateDirectory(folder);
            var fileName = SanitizeFileName(name);

            // 同名の同時ドロップに備え、CreateNew で開けるまで番号を進める（ReserveUniquePath は存在確認だけなので）
            for (var attempt = 0; attempt < 100; attempt++)
            {
                var path = ReserveUniquePath(folder, fileName);
                FileStream fs;
                try
                {
                    fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                }
                catch (IOException) when (File.Exists(path))
                {
                    // 他のドロップが先に同名を作った。ReserveUniquePath が次の番号を返すのでやり直す
                    continue;
                }

                try
                {
                    await using (fs)
                    {
                        await CopyWithLimitAsync(body, fs, MaxBytes, ct);
                    }
                    return path;
                }
                catch
                {
                    try { File.Delete(path); } catch { /* 書きかけの削除失敗は無視 */ }
                    throw;
                }
            }
            throw new IOException("保存先のファイル名を確保できませんでした");
        }

        private static async Task CopyWithLimitAsync(Stream source, Stream target, long limit, CancellationToken ct)
        {
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                total += read;
                if (total > limit)
                {
                    throw new DroppedFileTooLargeException();
                }
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }

        /// <summary>
        /// <see cref="KeepDays"/> より古い日付フォルダを削除する。日付として読めないフォルダは触らない。失敗は無視。
        /// </summary>
        public static void CleanupOldFolders(string root, DateTime now)
        {
            if (!Directory.Exists(root)) return;
            var threshold = now.Date.AddDays(-KeepDays);
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var dirName = Path.GetFileName(dir);
                if (!DateTime.TryParseExact(dirName, DateFolderFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    continue;
                }
                if (date < threshold)
                {
                    try { Directory.Delete(dir, recursive: true); } catch { /* 掃除の失敗は無視（次回また試す） */ }
                }
            }
        }
    }

    /// <summary>ドロップされたファイルが <see cref="DroppedFileStore.MaxBytes"/> を超えた。</summary>
    public sealed class DroppedFileTooLargeException : Exception
    {
        public DroppedFileTooLargeException() : base("ドロップされたファイルが上限を超えています") { }
    }
}
