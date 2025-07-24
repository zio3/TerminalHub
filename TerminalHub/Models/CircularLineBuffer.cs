using System.Collections;

namespace TerminalHub.Models
{
    /// <summary>
    /// 固定サイズの循環バッファでターミナル出力行を効率的に管理
    /// </summary>
    public class CircularLineBuffer : IEnumerable<string>
    {
        private readonly string[] _buffer;
        private readonly int _maxSize;
        private readonly object _lock = new();
        
        private int _head = 0;  // 次に書き込む位置
        private int _tail = 0;  // 最古のデータ位置
        private int _count = 0; // 現在の要素数

        public CircularLineBuffer(int maxSize)
        {
            if (maxSize <= 0)
                throw new ArgumentException("Buffer size must be positive", nameof(maxSize));
                
            _maxSize = maxSize;
            _buffer = new string[maxSize];
        }

        /// <summary>
        /// 現在のバッファ内要素数
        /// </summary>
        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _count;
                }
            }
        }

        /// <summary>
        /// バッファの最大容量
        /// </summary>
        public int Capacity => _maxSize;

        /// <summary>
        /// バッファが満杯かどうか
        /// </summary>
        public bool IsFull
        {
            get
            {
                lock (_lock)
                {
                    return _count == _maxSize;
                }
            }
        }

        /// <summary>
        /// バッファが空かどうか
        /// </summary>
        public bool IsEmpty
        {
            get
            {
                lock (_lock)
                {
                    return _count == 0;
                }
            }
        }

        /// <summary>
        /// 新しい行をバッファに追加
        /// </summary>
        /// <param name="line">追加する行</param>
        public void AddLine(string line)
        {
            if (line == null)
                return;

            lock (_lock)
            {
                _buffer[_head] = line;
                _head = (_head + 1) % _maxSize;

                if (_count < _maxSize)
                {
                    _count++;
                }
                else
                {
                    // バッファが満杯の場合、最古のデータを上書き
                    _tail = (_tail + 1) % _maxSize;
                }
            }
        }

        /// <summary>
        /// 複数の行を一度に追加
        /// </summary>
        /// <param name="lines">追加する行のコレクション</param>
        public void AddLines(IEnumerable<string> lines)
        {
            if (lines == null)
                return;

            foreach (var line in lines)
            {
                AddLine(line);
            }
        }

        /// <summary>
        /// バッファ内の全ての行を取得（古い順）
        /// </summary>
        /// <returns>バッファ内の行</returns>
        public IEnumerable<string> GetLines()
        {
            lock (_lock)
            {
                var result = new string[_count];
                for (int i = 0; i < _count; i++)
                {
                    int index = (_tail + i) % _maxSize;
                    result[i] = _buffer[index];
                }
                return result;
            }
        }

        /// <summary>
        /// 最新のN行を取得
        /// </summary>
        /// <param name="count">取得する行数</param>
        /// <returns>最新のN行</returns>
        public IEnumerable<string> GetLastLines(int count)
        {
            if (count <= 0)
                return Enumerable.Empty<string>();

            lock (_lock)
            {
                int actualCount = Math.Min(count, _count);
                var result = new string[actualCount];
                
                for (int i = 0; i < actualCount; i++)
                {
                    int index = (_tail + _count - actualCount + i) % _maxSize;
                    result[i] = _buffer[index];
                }
                
                return result;
            }
        }

        /// <summary>
        /// バッファを空にする
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                _head = 0;
                _tail = 0;
                _count = 0;
                Array.Clear(_buffer, 0, _buffer.Length);
            }
        }

        /// <summary>
        /// バッファの内容を文字列として結合
        /// </summary>
        /// <param name="separator">行間の区切り文字（デフォルト：改行）</param>
        /// <returns>結合された文字列</returns>
        public string GetAllText(string separator = "\n")
        {
            var lines = GetLines();
            return string.Join(separator, lines);
        }

        /// <summary>
        /// デバッグ用：バッファの状態を文字列で取得
        /// </summary>
        /// <returns>バッファの状態情報</returns>
        public string GetDebugInfo()
        {
            lock (_lock)
            {
                return $"CircularLineBuffer: Count={_count}, Capacity={_maxSize}, Head={_head}, Tail={_tail}, IsFull={IsFull}";
            }
        }

        /// <summary>
        /// IEnumerable&lt;string&gt;の実装
        /// </summary>
        public IEnumerator<string> GetEnumerator()
        {
            return GetLines().GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}