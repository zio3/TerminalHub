using System.Collections.Generic;
using System.Threading.Tasks;

namespace TerminalHub.Services
{
    public interface IInputHistoryService
    {
        List<string> GetHistory();
        int GetCurrentIndex();
        void AddToHistory(string text);
        string? NavigateHistory(int direction);
        void ResetIndex();
        Task SaveHistoryAsync();
        Task LoadHistoryAsync();
    }
}