using Microsoft.JSInterop;
using System.Text.Json;
using TerminalHub.Models;

namespace TerminalHub.Services
{
    public interface ILocalStorageService
    {
        Task SaveSessionsAsync(IEnumerable<SessionInfo> sessions);
        Task<List<SessionInfo>> LoadSessionsAsync();
        Task SaveActiveSessionIdAsync(string? sessionId);
        Task<string?> LoadActiveSessionIdAsync();
        Task ClearAsync();
    }

    public class LocalStorageService : ILocalStorageService
    {
        private readonly IJSRuntime _jsRuntime;
        private readonly JsonSerializerOptions _jsonOptions;
        private const string SessionsKey = "terminalHub_sessions";
        private const string ActiveSessionKey = "terminalHub_activeSession";

        public LocalStorageService(IJSRuntime jsRuntime)
        {
            _jsRuntime = jsRuntime;
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
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving sessions to localStorage: {ex.Message}");
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
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading sessions from localStorage: {ex.Message}");
                return new List<SessionInfo>();
            }
        }

        public async Task SaveActiveSessionIdAsync(string? sessionId)
        {
            try
            {
                if (sessionId == null)
                {
                    await _jsRuntime.InvokeVoidAsync("localStorage.removeItem", ActiveSessionKey);
                }
                else
                {
                    await _jsRuntime.InvokeVoidAsync("localStorage.setItem", ActiveSessionKey, sessionId);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving active session ID to localStorage: {ex.Message}");
            }
        }

        public async Task<string?> LoadActiveSessionIdAsync()
        {
            try
            {
                return await _jsRuntime.InvokeAsync<string?>("localStorage.getItem", ActiveSessionKey);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading active session ID from localStorage: {ex.Message}");
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
            catch (Exception ex)
            {
                Console.WriteLine($"Error clearing localStorage: {ex.Message}");
            }
        }
    }
}