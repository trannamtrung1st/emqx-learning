namespace EmqxLearning.Shared.Concurrency.Abstracts;

public interface ISyncAsyncTaskRunner
{
    Task RunSyncAsync(IDisposable asyncScope, Func<IAsyncDisposable, Task> task, bool longRunning = true);
    Task RunSyncAsync(IAsyncDisposable asyncScope, Func<IAsyncDisposable, Task> task, bool longRunning = true);
}