
namespace Notepads.Core
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Newtonsoft.Json;
    using Notepads.Controls.TextEditor;
    using Notepads.Utilities;
    using Windows.Storage;
    using Windows.Storage.AccessCache;

    internal class SessionManager : ISessionManager
    {
        private const string _sessionDataKey = "NotepadsSessionData";
        private const int _saveIntervalInSeconds = 7;

        private INotepadsCore _notepadsCore;
        private bool _loaded, _allowBackups;
        private SemaphoreSlim _semaphoreSlim;
        private HashSet<Guid> _freshBackups;

        private CancellationTokenSource _cancellationTokenSource;

        /// <summary>
        /// Do not call this constructor directly. Use <see cref="SessionUtility.GetSessionManager(INotepadsCore)" instead./>
        /// </summary>
        public SessionManager(INotepadsCore notepadsCore)
        {
            _notepadsCore = notepadsCore;
            _loaded = false;
            _allowBackups = false;
            _semaphoreSlim = new SemaphoreSlim(1, 1);
            _freshBackups = new HashSet<Guid>();
        }

        public async Task LoadLastSessionAsync()
        {
            if (_loaded)
            {
                return; // Already loaded
            }

            if (!ApplicationData.Current.LocalSettings.Values.TryGetValue(_sessionDataKey, out object data))
            {
                return; // No session data found
            }

            NotepadsSessionData sessionData;

            try
            {
                sessionData = JsonConvert.DeserializeObject<NotepadsSessionData>((string)data);
            }
            catch
            {
                return; // Failed to load the session data
            }

            TextEditor selectedTextEditor = null;

            for (int i = 0; i < sessionData.TextEditors.Count; i++)
            {
                try
                {
                    TextEditorSessionData textEditorData = sessionData.TextEditors[i];
                    StorageFile originalFile = await FileSystemUtility.GetFileFromFutureAccessList(ToToken(textEditorData.Id));
                    StorageFile backupFile = await FileSystemUtility.GetFile(textEditorData.BackupPath);
                    TextEditor textEditor = null;

                    if (originalFile == null) // i.e. Untitled.txt
                    {
                        if (backupFile != null)
                        {
                            textEditor = await _notepadsCore.OpenNewTextEditor(backupFile, textEditorData.Id, true);
                            _freshBackups.Add(textEditor.Id);
                        }
                    }
                    else
                    {
                        textEditor = await _notepadsCore.OpenNewTextEditor(originalFile, textEditorData.Id, false);

                        if (backupFile != null) // There were unsaved changes
                        {
                            DateTimeOffset originalfileDateModified = await FileSystemUtility.GetDateModified(originalFile);
                            DateTimeOffset backupFileDateModified = await FileSystemUtility.GetDateModified(backupFile);

                            if (originalfileDateModified <= backupFileDateModified) // The backup file was more recent - swap the content
                            {
                                TextFile contentWithUnsavedChanges = await FileSystemUtility.ReadFile(backupFile);
                                textEditor.SetText(contentWithUnsavedChanges.Content);
                                _freshBackups.Add(textEditor.Id);
                            }
                        }
                    }

                    if (textEditor != null)
                    {
                        textEditor.TextChanging += OnTextChanging;

                        if (textEditor.Id == sessionData.SelectedTextEditor)
                        {
                            selectedTextEditor = textEditor;
                        }
                    }
                }
                catch
                {
                    // Silently move on to the next text editor
                }
            }

            if (selectedTextEditor != null)
            {
                _notepadsCore.SwitchTo(selectedTextEditor);
            }

            _loaded = true;
        }

        public async Task SaveSessionAsync()
        {
            if (!_allowBackups)
            {
                return;
            }

            // Serialize saves
            await _semaphoreSlim.WaitAsync();

            NotepadsSessionData sessionData = new NotepadsSessionData();
            HashSet<string> backupFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            TextEditor[] textEditors = _notepadsCore.GetAllTextEditors();
            TextEditor selected = _notepadsCore.GetSelectedTextEditor();

            for (int i = 0; i < textEditors.Length; i++)
            {
                TextEditor textEditor = textEditors[i];
                TextEditorSessionData textEditorData = new TextEditorSessionData { Id = textEditor.Id };
                bool isEmpty = true;

                if (textEditor.EditingFile != null)
                {
                    // Add the opened file to FutureAccessList so we can access it next launch
                    StorageApplicationPermissions.FutureAccessList.AddOrReplace(ToToken(textEditorData.Id), textEditor.EditingFile);
                    isEmpty = false;
                }

                if (!textEditor.Saved) // There are unsaved changes
                {
                    string fileName = ToFileName(textEditorData.Id);

                    if (_freshBackups.Contains(textEditor.Id)) // The current backup is fresh - simply set the backup path
                    {
                        textEditorData.BackupPath = await SessionUtility.GetFullFilePathAsync(fileName);
                    }
                    else
                    {
                        StorageFile backupFile;

                        try
                        {
                            backupFile = await SessionUtility.CreateNewBackupFileAsync(fileName);
                            await textEditor.SaveToFileOnly(backupFile);
                        }
                        catch
                        {
                            continue; // Drop the text editor in the backup if we cannot save it
                        }

                        textEditorData.BackupPath = backupFile.Path;
                        _freshBackups.Add(textEditor.Id);
                    }

                    backupFileNames.Add(fileName);
                    isEmpty = false;
                }

                if (!isEmpty)
                {
                    sessionData.TextEditors.Add(textEditorData);

                    if (textEditor == selected)
                    {
                        sessionData.SelectedTextEditor = textEditor.Id;
                    }
                }
            }

            try
            {
                string sessionJson = JsonConvert.SerializeObject(sessionData);
                ApplicationData.Current.LocalSettings.Values[_sessionDataKey] = sessionJson;
            }
            catch
            {
                return; // Failed to save the session - do not proceed to delete backup files
            }

            // Clean up stale backup files
            foreach (StorageFile backupFile in await SessionUtility.GetAllBackupFilesAsync())
            {
                if (!backupFileNames.Contains(backupFile.Name))
                {
                    try
                    {
                        await backupFile.DeleteAsync();
                    }
                    catch
                    {
                        // Best effort
                    }
                }
            }

            _semaphoreSlim.Release();
        }

        public void StartSessionBackup()
        {
            _allowBackups = true; // Semi-hacky :)

            if (_cancellationTokenSource != null)
            {
                return; // Backup thread already started
            }

            try
            {
                CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();

                Task.Run(
                    async () =>
                    {
                        while (true)
                        {
                            Thread.Sleep(_saveIntervalInSeconds * 1000);
                            await SaveSessionAsync();
                        }
                    },
                    cancellationTokenSource.Token);

                _cancellationTokenSource = cancellationTokenSource;
            }
            catch
            {
                // Best effort
            }
        }

        public void StopSessionBackup()
        {
            try
            {
                if (_cancellationTokenSource != null)
                {
                    _cancellationTokenSource.Cancel();
                    _cancellationTokenSource.Dispose();
                    _cancellationTokenSource = null;
                }
            }
            catch
            {
                // Best effort
            }
        }

        public void ClearSessionData()
        {
            ApplicationData.Current.LocalSettings.Values.Remove(_sessionDataKey);
        }

        private void OnTextChanging(object sender, bool isContentChanging)
        {
            if (sender is TextEditor textEditor && isContentChanging)
            {
                _freshBackups.Remove(textEditor.Id);
            }
        }

        private string ToToken(Guid textEditorId)
        {
            return textEditorId.ToString("N");
        }

        private string ToFileName(Guid textEditorId)
        {
            return textEditorId.ToString("N");
        }

        private class NotepadsSessionData
        {
            public NotepadsSessionData()
            {
                TextEditors = new List<TextEditorSessionData>();
            }

            public Guid SelectedTextEditor { get; set; }

            public List<TextEditorSessionData> TextEditors { get; }
        }

        private class TextEditorSessionData
        {
            public Guid Id { get; set; }

            public string BackupPath { get; set; }
        }
    }
}
