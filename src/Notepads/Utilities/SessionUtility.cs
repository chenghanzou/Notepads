
namespace Notepads.Utilities
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading.Tasks;
    using Notepads.Core;
    using Windows.Storage;

    internal static class SessionUtility
    {
        private static readonly ConcurrentDictionary<INotepadsCore, ISessionManager> _sessionManagers = new ConcurrentDictionary<INotepadsCore, ISessionManager>();

        public static ISessionManager GetSessionManager(INotepadsCore notepadCore)
        {
            if (!_sessionManagers.TryGetValue(notepadCore, out ISessionManager sessionManager))
            {
                sessionManager = new SessionManager(notepadCore);

                if (!_sessionManagers.TryAdd(notepadCore, sessionManager))
                {
                    sessionManager = _sessionManagers[notepadCore];
                }
            }

            return sessionManager;
        }

        public static async Task<IReadOnlyList<StorageFile>> GetAllBackupFilesAsync()
        {
            StorageFolder backupFilesFolder = await GetBackupFilesFolderAsync();
            return await backupFilesFolder.GetFilesAsync();
        }

        public static async Task<string> GetFullFilePathAsync(string fileName)
        {
            StorageFolder backupFilesFolder = await GetBackupFilesFolderAsync();
            return Path.Combine(backupFilesFolder.Path, fileName);
        }

        public static async Task<StorageFile> CreateNewBackupFileAsync(string fileName)
        {
            StorageFolder backupFilesFolder = await GetBackupFilesFolderAsync();
            return await backupFilesFolder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
        }

        private static async Task<StorageFolder> GetBackupFilesFolderAsync()
        {
            StorageFolder localFolder = ApplicationData.Current.LocalFolder;
            return await localFolder.CreateFolderAsync("BackupFiles", CreationCollisionOption.OpenIfExists);
        }
    }
}
