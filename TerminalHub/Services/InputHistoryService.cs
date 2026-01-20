using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace TerminalHub.Services
{
    public class InputHistoryService : IInputHistoryService
    {
        private readonly ISessionRepository _repository;
        private readonly ILogger<InputHistoryService> _logger;
        private List<string> _inputHistory = new();
        private int _historyIndex = -1;
        private const int MaxHistorySize = 100;
        private bool _initialized = false;

        public InputHistoryService(
            ISessionRepository repository,
            ILogger<InputHistoryService> logger)
        {
            _repository = repository;
            _logger = logger;
        }

        public List<string> GetHistory()
        {
            return new List<string>(_inputHistory);
        }

        public int GetCurrentIndex()
        {
            return _historyIndex;
        }

        public void AddToHistory(string text)
        {
            // 空白やnullは履歴に追加しない
            if (string.IsNullOrWhiteSpace(text))
                return;

            // 同じテキストが連続する場合は追加しない
            if (_inputHistory.Count > 0 && _inputHistory[_inputHistory.Count - 1] == text)
                return;

            _inputHistory.Add(text);

            // 履歴の最大数を制限
            if (_inputHistory.Count > MaxHistorySize)
            {
                _inputHistory.RemoveAt(0);
            }

            // 履歴インデックスをリセット
            _historyIndex = -1;

            // SQLiteにも保存（非同期で実行、エラーは無視）
            _ = Task.Run(async () =>
            {
                try
                {
                    await _repository.AddInputHistoryAsync(text);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "入力履歴のSQLite保存に失敗");
                }
            });
        }

        public string? NavigateHistory(int direction)
        {
            if (_inputHistory.Count == 0)
                return null;

            // 初回の履歴操作時
            if (_historyIndex == -1)
            {
                if (direction < 0) // 上矢印（古い履歴へ）
                {
                    _historyIndex = _inputHistory.Count - 1;
                    return _inputHistory[_historyIndex];
                }
                return null;
            }

            // 履歴を移動
            var newIndex = _historyIndex + direction;

            if (newIndex >= 0 && newIndex < _inputHistory.Count)
            {
                _historyIndex = newIndex;
                return _inputHistory[_historyIndex];
            }
            else if (newIndex < 0)
            {
                // 最古の履歴より前に行こうとした場合
                _historyIndex = 0;
                return _inputHistory[_historyIndex];
            }
            else if (newIndex >= _inputHistory.Count)
            {
                // 最新の履歴より後に行こうとした場合は空にする
                _historyIndex = -1;
                return "";
            }

            return null;
        }

        public void ResetIndex()
        {
            _historyIndex = -1;
        }

        public async Task SaveHistoryAsync()
        {
            // SQLiteは AddToHistory で即座に保存されるため、何もしない
            await Task.CompletedTask;
        }

        public async Task LoadHistoryAsync()
        {
            if (_initialized)
                return;

            try
            {
                // SQLiteから読み込み
                var savedHistory = await _repository.GetInputHistoryAsync(MaxHistorySize);
                if (savedHistory != null && savedHistory.Count > 0)
                {
                    _inputHistory = savedHistory;
                    _historyIndex = -1;
                    _logger.LogInformation("入力履歴をSQLiteから読み込み: {Count}件", _inputHistory.Count);
                }
                _initialized = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "入力履歴の読み込みに失敗");
            }
        }
    }
}
