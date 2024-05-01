namespace EmqxLearning.Shared.Services.Abstracts;

public interface IFuzzyThreadController
{
    int GetThreadScale(double cpu, double memory, int factor = 10);
}