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

        private sealed class RunContext
        {
            private RunContext(string targetDirectoryPath, string targetHash)
            {
                this.TargetDirectoryPath = targetDirectoryPath;
                this.TargetHash = targetHash;
            }

            internal string TargetDirectoryPath { get; private set; }

            internal string TargetHash { get; private set; }

            internal string TempRootDirectoryPath { get; private set; }

            internal static RunContext Create(
                string targetDirectoryPath,
                ISafePathService safePathService,
                ISyncStateStore syncStateStore)
            {
                if (safePathService == null) throw new ArgumentNullException(nameof(safePathService));
                if (syncStateStore == null) throw new ArgumentNullException(nameof(syncStateStore));

                string normalizedTargetDirectoryPath = safePathService.GetFullPath(targetDirectoryPath);
                string targetHash = syncStateStore.GetTargetHash(normalizedTargetDirectoryPath);

                return new RunContext(normalizedTargetDirectoryPath, targetHash);
            }

            internal void CreateTempRootDirectory()
            {
                this.TempRootDirectoryPath = Path.Combine(Path.GetTempPath(), "UpdateClientSync_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(this.TempRootDirectoryPath);
            }
        }

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

            RunContext context = null;
            SyncMutexHandle mutexHandle = null;
            RepositoryTarget selectedTarget = null;
            this.TryInitializeLogging(targetDirectoryPath, args);

            try
            {
                string selectedBranch;
                if (!this.startupMenu.ShowStartupPrompt(targetDirectoryPath, out selectedTarget, out selectedBranch))
                {
                    return this.ExitWithoutSynchronization();
                }

                this.WriteLogOnlyLine("Selected repository: " + selectedTarget.DisplayName + ".");
                context = RunContext.Create(targetDirectoryPath, this.safePathService, this.syncStateStore);
                mutexHandle = SyncMutexHandle.Acquire(context.TargetHash);
                context.CreateTempRootDirectory();

                RepositoryTreeResult preparedTree;
                RepositoryRemoteKind remoteKind;
                if (!this.TryPrepareRepositoryTree(selectedTarget, selectedBranch, context.TempRootDirectoryPath, out preparedTree, out remoteKind))
                {
                    return this.ExitWithoutSynchronization();
                }

                SyncSummary summary = this.SynchronizeRepository(context, selectedTarget, preparedTree, remoteKind);
                return this.CompleteRun(selectedTarget, summary);
            }
            catch (Exception exception)
            {
                return this.FailRun(exception, selectedTarget);
            }
            finally
            {
                if (mutexHandle != null)
                {
                    mutexHandle.Dispose();
                }

                CleanupRun(context);
                this.ShutdownLogging();
            }
        }

        private int ExitWithoutSynchronization()
        {
            this.startupMenu.PauseBeforeExit();
            return 0;
        }

        private SyncSummary SynchronizeRepository(
            RunContext context,
            RepositoryTarget target,
            RepositoryTreeResult preparedTree,
            RepositoryRemoteKind remoteKind)
        {
            return this.repositorySynchronizer.Synchronize(
                target,
                preparedTree,
                remoteKind,
                context.TargetDirectoryPath,
                context.TargetHash,
                context.TempRootDirectoryPath,
                this.activeLog);
        }

        private int CompleteRun(RepositoryTarget target, SyncSummary summary)
        {
            Console.WriteLine();
            Console.WriteLine(string.Format("Sync complete for {0}.", target.DisplayName));
            Console.WriteLine(string.Format("Added: {0}", summary.Added));
            Console.WriteLine(string.Format("Updated: {0}", summary.Updated));
            Console.WriteLine(string.Format("Removed: {0}", summary.Removed));
            Console.WriteLine(string.Format("Unchanged: {0}", summary.Unchanged));
            this.startupMenu.PauseBeforeExit();
            return 0;
        }

        private int FailRun(Exception exception, RepositoryTarget target)
        {
            Console.WriteLine();
            Console.WriteLine(target == null ? "Sync failed." : "Sync failed for " + target.DisplayName + ".");
            Console.WriteLine(exception.Message);
            this.LogException(exception);
            this.startupMenu.PauseBeforeExit();
            return 1;
        }

        private static void CleanupRun(RunContext context)
        {
            if (context == null || string.IsNullOrWhiteSpace(context.TempRootDirectoryPath) || !Directory.Exists(context.TempRootDirectoryPath))
            {
                return;
            }

            try
            {
                Directory.Delete(context.TempRootDirectoryPath, true);
            }
            catch
            {
            }
        }

        private bool TryPrepareRepositoryTree(RepositoryTarget target, string branch, string tempRootDirectoryPath, out RepositoryTreeResult treeResult, out RepositoryRemoteKind remoteKind)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (branch == null && !ReferenceEquals(target, AppOptions.Get5))
                throw new ArgumentException("Default branch lookup is only available for Get5.", nameof(branch));
            if (branch != null && string.IsNullOrWhiteSpace(branch))
                throw new ArgumentException("Value cannot be empty.", nameof(branch));
            if (string.IsNullOrWhiteSpace(tempRootDirectoryPath)) throw new ArgumentException("Value cannot be empty.", nameof(tempRootDirectoryPath));

            treeResult = null;
            remoteKind = RepositoryRemoteKind.Github;
            this.WriteLogOnlyLine("Selected sync branch: " + (branch ?? "repository default") + ".");

            try
            {
                treeResult = branch == null
                    ? this.remoteRepositoryClient.PrepareRepositoryTree(target, tempRootDirectoryPath, remoteKind)
                    : this.remoteRepositoryClient.PrepareRepositoryTree(target, branch, tempRootDirectoryPath, remoteKind);
                this.WriteLogOnlyLine("Resolved sync branch: " + treeResult.Branch + ".");
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
                treeResult = branch == null
                    ? this.remoteRepositoryClient.PrepareRepositoryTree(target, tempRootDirectoryPath, remoteKind)
                    : this.remoteRepositoryClient.PrepareRepositoryTree(target, branch, tempRootDirectoryPath, remoteKind);
                this.WriteLogOnlyLine("Resolved sync branch: " + treeResult.Branch + ".");
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
                LogArchiveService.TryArchivePreviousLogs(
                    targetDirectoryPath,
                    this.activeLog.CurrentLogPath,
                    this.safePathService,
                    this.activeLog.WriteLogOnlyLine);
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
