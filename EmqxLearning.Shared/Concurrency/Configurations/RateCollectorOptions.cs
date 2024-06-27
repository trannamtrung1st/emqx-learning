namespace EmqxLearning.Shared.Concurrency.Configurations;

public class RateCollectorOptions
{
    public int MovingAverageRange { get; set; }
    public double Interval { get; set; }
}