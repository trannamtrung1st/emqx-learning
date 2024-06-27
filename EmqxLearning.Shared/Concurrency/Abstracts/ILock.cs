namespace EmqxLearning.Shared.Concurrency.Abstracts;

public interface ILock : IDisposable
{
    string Key { get; }
}