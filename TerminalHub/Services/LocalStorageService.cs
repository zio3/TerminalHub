using Microsoft.JSInterop;
using System.Text.Json;
using TerminalHub.Models;
using Microsoft.Extensions.Logging;

namespace TerminalHub.Services
{
    public interface ILocalStorageService
    {
        Task SaveSessionsAsync(IEnumerable<SessionInfo> sessions);
        Task<List<SessionInfo>> LoadSessionsAsync();
        Task SaveActiveSessionIdAsync(Guid? sessionId);
        Task<Guid?> LoadActiveSessionIdAsync();
        Task ClearAsync();
        Task<T?> GetAsync<T>(string key);
        Task SetAsync<T>(string key, T value);
        Task SaveSessionExpandedStatesAsync(Dictionary<Guid, bool> expandedStates);
        Task<Dictionary<Guid, bool>> LoadSessionExpandedStatesAsync();
    }

    public class LocalStorageService : ILocalStorageService
    {
        private readonly IJSRuntime _jsRuntime;
        private readonly ILogger<LocalStorageService> _logger;
        private readonly JsonSerializerOptions _jsonOptions;
        private const string SessionsKey = "terminalHub_sessions";
        private const string ActiveSessionKey = "terminalHub_activeSession";
        private const string ExpandedStatesKey = "terminalHub_expandedStates";

        public LocalStorageService(IJSRuntime jsRuntime, ILogger<LocalStorageService> logger)
        {
            _jsRuntime = jsRuntime;
            _logger = logger;
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false
            };
        }

        public async Task SaveSessionsAsync(IEnumerable<SessionInfo> sessions)
        {
            try
            {
                var json = JsonSerializer.Serialize(sessions, _jsonOptions);
                await _jsRuntime.InvokeVoidAsync("localStorage.setItem", SessionsKey, json);
            }
            catch (InvalidOperationException)
            {
                // JavaScript interopが利用できない場合は無視（プリレンダリング中など）
            }
            catch (JSDisconnectedException)
            {
                // JavaScript接続が切断されている場合は無視
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving sessions to localStorage");
            }
        }

        public async Task<List<SessionInfo>> LoadSessionsAsync()
        {
            try
            {
                _logger.LogInformation("LoadSessionsAsync: 開始");
                var json = await _jsRuntime.InvokeAsync<string?>("localStorage.getItem", SessionsKey);
                _logger.LogInformation("LoadSessionsAsync: LocalStorageから取得完了, length={Length}", json?.Length ?? 0);

                if (string.IsNullOrEmpty(json))
                {
                    _logger.LogInformation("LoadSessionsAsync: データなし");
                    return new List<SessionInfo>();
                }

                _logger.LogInformation("LoadSessionsAsync: デシリアライズ開始");
                var sessions = JsonSerializer.Deserialize<List<SessionInfo>>(json, _jsonOptions);
                _logger.LogInformation("LoadSessionsAsync: デシリアライズ完了, count={Count}", sessions?.Count ?? 0);
                return sessions ?? new List<SessionInfo>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LoadSessionsAsync: エラー発生");
                return new List<SessionInfo>();
            }
        }

        public async Task SaveActiveSessionIdAsync(Guid? sessionId)
        {
            try
            {
                if (sessionId == null)
                {
                    await _jsRuntime.InvokeVoidAsync("localStorage.removeItem", ActiveSessionKey);
                }
                else
                {
                    await _jsRuntime.InvokeVoidAsync("localStorage.setItem", ActiveSessionKey, sessionId.ToString());
                }
            }
            catch (InvalidOperationException)
            {
                // JavaScript interopが利用できない場合は無視（プリレンダリング中など）
            }
            catch (JSDisconnectedException)
            {
                // JavaScript接続が切断されている場合は無視
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving active session ID to localStorage");
            }
        }

        public async Task<Guid?> LoadActiveSessionIdAsync()
        {
            try
            {
                var sessionIdString = await _jsRuntime.InvokeAsync<string?>("localStorage.getItem", ActiveSessionKey);
                if (string.IsNullOrEmpty(sessionIdString))
                {
                    return null;
                }
                
                if (Guid.TryParse(sessionIdString, out var sessionId))
                {
                    return sessionId;
                }
                
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading active session ID from localStorage");
                return null;
            }
        }

        public async Task ClearAsync()
        {
            try
            {
                await _jsRuntime.InvokeVoidAsync("localStorage.removeItem", SessionsKey);
                await _jsRuntime.InvokeVoidAsync("localStorage.removeItem", ActiveSessionKey);
            }
            catch (InvalidOperationException)
            {
                // JavaScript interopが利用できない場合は無視（プリレンダリング中など）
            }
            catch (JSDisconnectedException)
            {
                // JavaScript接続が切断されている場合は無視
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error clearing localStorage");
            }
        }
        
        public async Task<T?> GetAsync<T>(string key)
        {
            try
            {
                var json = await _jsRuntime.InvokeAsync<string?>("localStorage.getItem", key);
                if (string.IsNullOrEmpty(json))
                    return default;
                
                return JsonSerializer.Deserialize<T>(json, _jsonOptions);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading {Key} from localStorage", key);
                return default;
            }
        }
        
        public async Task SetAsync<T>(string key, T value)
        {
            try
            {
                var json = JsonSerializer.Serialize(value, _jsonOptions);
                await _jsRuntime.InvokeVoidAsync("localStorage.setItem", key, json);
            }
            catch (InvalidOperationException)
            {
                // JavaScript interopが利用できない場合は無視（プリレンダリング中など）
            }
            catch (JSDisconnectedException)
            {
                // JavaScript接続が切断されている場合は無視
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving {Key} to localStorage", key);
            }
        }

        public async Task SaveSessionExpandedStatesAsync(Dictionary<Guid, bool> expandedStates)
        {
            try
            {
                var json = JsonSerializer.Serialize(expandedStates, _jsonOptions);
                await _jsRuntime.InvokeVoidAsync("localStorage.setItem", ExpandedStatesKey, json);
            }
            catch (InvalidOperationException)
            {
                // JavaScript interopが利用できない場合は無視（プリレンダリング中など）
            }
            catch (JSDisconnectedException)
            {
                // JavaScript接続が切断されている場合は無視
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving expanded states to localStorage");
            }
        }

        public async Task<Dictionary<Guid, bool>> LoadSessionExpandedStatesAsync()
        {
            try
            {
                var json = await _jsRuntime.InvokeAsync<string>("localStorage.getItem", ExpandedStatesKey);
                if (string.IsNullOrEmpty(json))
                {
                    return new Dictionary<Guid, bool>();
                }

                return JsonSerializer.Deserialize<Dictionary<Guid, bool>>(json, _jsonOptions) ?? new Dictionary<Guid, bool>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading expanded states from localStorage");
                return new Dictionary<Guid, bool>();
            }
        }
    }
}