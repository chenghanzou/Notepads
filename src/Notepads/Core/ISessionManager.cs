
namespace Notepads.Core
{
    using System.Threading.Tasks;

    internal interface ISessionManager
    {
        Task LoadLastSessionAsync();

        Task SaveSessionAsync();

        void StartSessionBackup();

        void StopSessionBackup();

        void ClearSessionData();
    }
}