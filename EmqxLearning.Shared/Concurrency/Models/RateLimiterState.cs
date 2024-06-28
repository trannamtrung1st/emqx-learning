namespace EmqxLearning.Shared.Concurrency.Models;

public readonly record struct RateLimiterState(int Limit, int Acquired, int Available);
