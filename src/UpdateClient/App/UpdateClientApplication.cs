using System;
using System.IO;
using UpdateClient.Config;
using UpdateClient.ConsoleUi;
using UpdateClient.FileSystem;
using UpdateClient.Logging;
using UpdateClient.Remote;
using UpdateClient.Remote.Models;
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
        private readonly IRemoteRepositoryClient remoteRepositoryClient;
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
            this.remoteRepositoryClient = remoteRepositoryClient;
            this.repositorySynchronizer = repositorySynchronizer;
        }

        public UpdateClientApplication(
            StartupMenu startupMenu,
            ISafePathService safePathService,
            ISyncStateStore syncStateStore,
            IRemoteRepositoryClient remoteRepositoryClient,
            IRepositorySynchronizer repositorySynchronizer)
        {
            if (startupMenu == null) throw new ArgumentNullException(nameof(startupMenu));
            if (safePathService == null) throw new ArgumentNullException(nameof(safePathService));
            if (syncStateStore == null) throw new ArgumentNullException(nameof(syncStateStore));
            if (remoteRepositoryClient == null) throw new ArgumentNullException(nameof(remoteRepositoryClient));
            if (repositorySynchronizer == null) throw new ArgumentNullException(nameof(repositorySynchronizer));

            this.startupMenu = startupMenu;
            this.safePathService = safePathService;
            this.syncStateStore = syncStateStore;
            this.remoteRepositoryClient = remoteRepositoryClient;
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

                RepositoryTreeResult preparedTree;
                RepositoryRemoteKind remoteKind;
                if (!this.TryPrepareRepositoryTree(AppOptions.Betterbot, tempRootDirectoryPath, out preparedTree, out remoteKind))
                {
                    this.startupMenu.PauseBeforeExit();
                    return 0;
                }

                SyncSummary summary = this.repositorySynchronizer.Synchronize(
                    AppOptions.Betterbot,
                    preparedTree,
                    remoteKind,
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

        private bool TryPrepareRepositoryTree(RepositoryTarget target, string tempRootDirectoryPath, out RepositoryTreeResult treeResult, out RepositoryRemoteKind remoteKind)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (string.IsNullOrWhiteSpace(tempRootDirectoryPath)) throw new ArgumentException("Value cannot be empty.", nameof(tempRootDirectoryPath));

            treeResult = null;
            remoteKind = RepositoryRemoteKind.Github;

            try
            {
                treeResult = this.remoteRepositoryClient.PrepareRepositoryTree(target, tempRootDirectoryPath, remoteKind);
                this.WriteLogOnlyLine("Selected remote source: GitHub.");
                return true;
            }
            catch (Exception githubException)
            {
                this.WriteLogOnlyLine("GitHub sync preparation failed:");
                this.WriteLogOnlyLine(githubException.ToString());

                if (!target.HasMirror)
                {
                    throw;
                }

                if (!this.startupMenu.ShowMirrorConfirmation(target, githubException.Message))
                {
                    this.WriteLogOnlyLine("Mirror sync canceled by user.");
                    return false;
                }

                remoteKind = RepositoryRemoteKind.Mirror;
                treeResult = this.remoteRepositoryClient.PrepareRepositoryTree(target, tempRootDirectoryPath, remoteKind);
                this.WriteLogOnlyLine("Selected remote source: Gitee mirror.");
                return true;
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

        private void WriteLogOnlyLine(string message)
        {
            if (this.activeLog == null || string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            try
            {
                this.activeLog.WriteLogOnlyLine(message);
            }
            catch
            {
            }
        }
    }
}
