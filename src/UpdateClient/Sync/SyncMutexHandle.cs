using System;
using System.Threading;
using UpdateClient.Config;

namespace UpdateClient.Sync
{
    internal sealed class SyncMutexHandle : IDisposable
    {
        private readonly Mutex mutex;
        private bool disposed;

        private SyncMutexHandle(Mutex mutexInstance)
        {
            this.mutex = mutexInstance;
        }

        public static SyncMutexHandle Acquire(string targetHash)
        {
            if (string.IsNullOrWhiteSpace(targetHash)) throw new ArgumentException("Value cannot be empty.", nameof(targetHash));

            Mutex mutex = new Mutex(false, AppOptions.MutexNamePrefix + targetHash);
            bool acquired = false;

            try
            {
                acquired = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                mutex.Dispose();
                throw new InvalidOperationException("Another UpdateClient sync is already running for this folder.");
            }

            return new SyncMutexHandle(mutex);
        }

        public void Dispose()
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            try
            {
                this.mutex.ReleaseMutex();
            }
            catch
            {
            }

            this.mutex.Dispose();
        }
    }
}
