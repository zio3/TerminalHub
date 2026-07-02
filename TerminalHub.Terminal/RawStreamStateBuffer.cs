using System.Text;

namespace TerminalHub.Terminal;

/// <summary>
/// 生ストリーム方式のターミナル状態バッファ（従来挙動）。
/// ConPTY 出力を無加工で貯め、復元時はそのまま書き戻す。上限を超えたら古い方から切り詰める。
/// </summary>
/// <remarks>
/// この方式はリサイズ/再読込で ConPTY が repaint（全画面再描画）を再送すると、それも追記されるため、
/// 復元時にスクロールバックが二重化する既知の不具合を持つ。エミュレータ方式（<see cref="ITerminalStateBuffer"/>
/// の別実装）が安定するまでの既定/フォールバックとして温存する。
/// </remarks>
public sealed class RawStreamStateBuffer : ITerminalStateBuffer
{
    private readonly StringBuilder _buffer = new();
    private readonly object _lock = new();
    private readonly int _maxSize;

    /// <summary>既定の上限サイズ（文字数）。従来の SessionInfo 実装と同じ 2MB 相当。</summary>
    public const int DefaultMaxSize = 2 * 1024 * 1024;

    public RawStreamStateBuffer(int maxSize = DefaultMaxSize)
    {
        _maxSize = maxSize;
    }

    public void Append(string data)
    {
        if (string.IsNullOrEmpty(data))
        {
            return;
        }

        lock (_lock)
        {
            // 上限チェック（超過分を古い方から削る）
            if (_buffer.Length + data.Length > _maxSize)
            {
                var overflow = _buffer.Length + data.Length - _maxSize;
                if (overflow < _buffer.Length)
                {
                    _buffer.Remove(0, overflow);
                }
                else
                {
                    _buffer.Clear();
                }
            }
            _buffer.Append(data);
        }
    }

    public string SerializeForReplay()
    {
        lock (_lock)
        {
            return _buffer.ToString();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _buffer.Clear();
        }
    }

    public int Size
    {
        get
        {
            lock (_lock)
            {
                return _buffer.Length;
            }
        }
    }
}
