using System;
using System.IO;
using UpdateClient.Config;
using UpdateClient.ConsoleUi;
using UpdateClient.FileSystem;
using UpdateClient.Logging;
using UpdateClient.Remote;
using UpdateClient.Security;
using UpdateClient.State;
using UpdateClient.Sync;

namespace UpdateClient.App
{
    internal sealed class UpdateClientApplication
    {
        private readonly StartupMenu startupMenu;
        private readonly ISafePathService safePathService;
        private readonly ISyncStateStore syncStateStore;
        private readonly IRepositorySynchronizer repositorySynchronizer;
        private LogSession activeLog;

        public UpdateClientApplication()
        {
            ISafePathService safePathService = new SafePathService();
            IGitBlobHasher gitBlobHasher = new GitBlobHasher();
            IRepositoryUrlBuilder repositoryUrlBuilder = new RepositoryUrlBuilder();
            IRemoteRepositoryClient remoteRepositoryClient = new RemoteRepositoryClient(repositoryUrlBuilder, gitBlobHasher);
            IAtomicFileWriter atomicFileWriter = new AtomicFileWriter();
            ISyncStateStore syncStateStore = new SyncStateStore(safePathService);
            IRepositorySynchronizer repositorySynchronizer = new RepositorySynchronizer(
                remoteRepositoryClient,
                safePathService,
                atomicFileWriter,
                syncStateStore,
                gitBlobHasher);

            this.startupMenu = new StartupMenu();
            this.safePathService = safePathService;
            this.syncStateStore = syncStateStore;
            this.repositorySynchronizer = repositorySynchronizer;
        }

        public UpdateClientApplication(
            StartupMenu startupMenu,
            ISafePathService safePathService,
            ISyncStateStore syncStateStore,
            IRepositorySynchronizer repositorySynchronizer)
        {
            if (startupMenu == null) throw new ArgumentNullException(nameof(startupMenu));
            if (safePathService == null) throw new ArgumentNullException(nameof(safePathService));
            if (syncStateStore == null) throw new ArgumentNullException(nameof(syncStateStore));
            if (repositorySynchronizer == null) throw new ArgumentNullException(nameof(repositorySynchronizer));

            this.startupMenu = startupMenu;
            this.safePathService = safePathService;
            this.syncStateStore = syncStateStore;
            this.repositorySynchronizer = repositorySynchronizer;
        }

        public int Run(string[] args)
        {
            string targetDirectoryPath = this.safePathService.GetFullPath(
                AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            string tempRootDirectoryPath = null;
            SyncMutexHandle mutexHandle = null;
            this.TryInitializeLogging(targetDirectoryPath, args);

            try
            {
                if (!this.startupMenu.ShowStartupPrompt(targetDirectoryPath, AppOptions.Betterbot))
                {
                    this.startupMenu.PauseBeforeExit();
                    return 0;
                }

                string targetHash = this.syncStateStore.GetTargetHash(targetDirectoryPath);
                mutexHandle = SyncMutexHandle.Acquire(targetHash);

                tempRootDirectoryPath = Path.Combine(Path.GetTempPath(), "UpdateClientSync_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempRootDirectoryPath);

                SyncSummary summary = this.repositorySynchronizer.Synchronize(
                    AppOptions.Betterbot,
                    targetDirectoryPath,
                    targetHash,
                    tempRootDirectoryPath,
                    this.activeLog);

                Console.WriteLine();
                Console.WriteLine("Sync complete.");
                Console.WriteLine(string.Format("Added: {0}", summary.Added));
                Console.WriteLine(string.Format("Updated: {0}", summary.Updated));
                Console.WriteLine(string.Format("Removed: {0}", summary.Removed));
                Console.WriteLine(string.Format("Unchanged: {0}", summary.Unchanged));
                this.startupMenu.PauseBeforeExit();
                return 0;
            }
            catch (Exception exception)
            {
                Console.WriteLine();
                Console.WriteLine("Sync failed.");
                Console.WriteLine(exception.Message);
                this.LogException(exception);
                this.startupMenu.PauseBeforeExit();
                return 1;
            }
            finally
            {
                if (mutexHandle != null)
                {
                    mutexHandle.Dispose();
                }

                if (!string.IsNullOrWhiteSpace(tempRootDirectoryPath) && Directory.Exists(tempRootDirectoryPath))
                {
                    try
                    {
                        Directory.Delete(tempRootDirectoryPath, true);
                    }
                    catch
                    {
                    }
                }

                this.ShutdownLogging();
            }
        }

        private void TryInitializeLogging(string targetDirectoryPath, string[] args)
        {
            if (this.activeLog != null)
            {
                return;
            }

            try
            {
                this.activeLog = LogSession.Create(targetDirectoryPath, this.safePathService);
                this.activeLog.Attach();
                this.activeLog.WriteSessionStart(targetDirectoryPath, args);
                Console.WriteLine(string.Format("Log file: {0}", this.activeLog.CurrentLogPath));
            }
            catch
            {
                if (this.activeLog != null)
                {
                    try
                    {
                        this.activeLog.Dispose();
                    }
                    catch
                    {
                    }

                    this.activeLog = null;
                }
            }
        }

        private void ShutdownLogging()
        {
            if (this.activeLog == null)
            {
                return;
            }

            try
            {
                this.activeLog.Dispose();
            }
            catch
            {
            }
            finally
            {
                this.activeLog = null;
            }
        }

        private void LogException(Exception exception)
        {
            if (this.activeLog == null || exception == null)
            {
                return;
            }

            try
            {
                this.activeLog.WriteLogOnlyLine("Unhandled exception:");
                this.activeLog.WriteLogOnlyLine(exception.ToString());
            }
            catch
            {
            }
        }

    }
}
