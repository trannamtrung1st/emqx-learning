namespace EmqxLearning.Shared.Concurrency.Abstracts;

public interface IResourceBasedFuzzyRateScaler
{
    int GetRateScale(double cpu, double memory, double ideal, int factor = 10, double incFactor = 1, double decFactor = 3);
}