namespace EmqxLearning.Shared.Concurrency.Configurations;

public class RateLimiterOptions
{
    public string Name { get; set; }
    public long InitialLimit { get; set; }
}