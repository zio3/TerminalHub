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
    }

    public class LocalStorageService : ILocalStorageService
    {
        private readonly IJSRuntime _jsRuntime;
        private readonly ILogger<LocalStorageService> _logger;
        private readonly JsonSerializerOptions _jsonOptions;
        private const string SessionsKey = "terminalHub_sessions";
        private const string ActiveSessionKey = "terminalHub_activeSession";

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
            catch (JSException jsEx)
            {
                _logger.LogError(jsEx, "JavaScriptエラー: セッション情報の保存に失敗しました");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "セッション情報の保存に失敗しました");
            }
        }

        public async Task<List<SessionInfo>> LoadSessionsAsync()
        {
            try
            {
                var json = await _jsRuntime.InvokeAsync<string?>("localStorage.getItem", SessionsKey);
                if (string.IsNullOrEmpty(json))
                {
                    return new List<SessionInfo>();
                }

                var sessions = JsonSerializer.Deserialize<List<SessionInfo>>(json, _jsonOptions);
                return sessions ?? new List<SessionInfo>();
            }
            catch (JSException jsEx)
            {
                _logger.LogError(jsEx, "JavaScriptエラー: localStorageからセッション情報の読み込みに失敗しました");
                return new List<SessionInfo>();
            }
            catch (JsonException jsonEx)
            {
                _logger.LogError(jsonEx, "JSONパースエラー: セッション情報のデシリアライズに失敗しました");
                return new List<SessionInfo>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "localStorageからセッション情報の読み込みに失敗しました");
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
            catch (JSException jsEx)
            {
                _logger.LogError(jsEx, "JavaScriptエラー: アクティブセッションIDの保存に失敗しました");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "アクティブセッションIDの保存に失敗しました");
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
            catch (JSException jsEx)
            {
                _logger.LogError(jsEx, "JavaScriptエラー: アクティブセッションIDの読み込みに失敗しました");
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "アクティブセッションIDの読み込みに失敗しました");
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
            catch (JSException jsEx)
            {
                _logger.LogError(jsEx, "JavaScriptエラー: localStorageのクリアに失敗しました");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "localStorageのクリアに失敗しました");
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
            catch (JSException jsEx)
            {
                _logger.LogError(jsEx, "JavaScriptエラー: {Key}のlocalStorageからの読み込みに失敗しました", key);
                return default;
            }
            catch (JsonException jsonEx)
            {
                _logger.LogError(jsonEx, "JSONパースエラー: {Key}のデシリアライズに失敗しました", key);
                return default;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Key}のlocalStorageからの読み込みに失敗しました", key);
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
            catch (JSException jsEx)
            {
                _logger.LogError(jsEx, "JavaScriptエラー: {Key}のlocalStorageへの保存に失敗しました", key);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Key}のlocalStorageへの保存に失敗しました", key);
            }
        }
    }
}