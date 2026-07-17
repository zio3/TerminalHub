using System.Collections.Generic;
using System.Threading.Tasks;

namespace TerminalHub.Services
{
    public interface IInputHistoryService
    {
        void AddToHistory(string text);
        string? NavigateHistory(int direction);
        Task LoadHistoryAsync();
    }
}