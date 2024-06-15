namespace EmqxLearning.Shared.Services.Abstracts;

public interface IFuzzyThreadController
{
    int GetThreadScale(double cpu, double memory, double ideal, int factor = 10);
}