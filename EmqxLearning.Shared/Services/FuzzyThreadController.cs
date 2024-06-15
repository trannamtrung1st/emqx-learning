using EmqxLearning.Shared.Services.Abstracts;
using FLS;
using FLS.Rules;

namespace EmqxLearning.Shared.Services;

public class FuzzyThreadController : IFuzzyThreadController
{
    private readonly IFuzzyEngine _fuzzyEngine;

    public FuzzyThreadController()
    {

        var cpu = new LinguisticVariable("Cpu");
        var cVeryLow = cpu.MembershipFunctions.AddTrapezoid("VeryLow", 0, 0, 0.2, 0.4);
        var cLow = cpu.MembershipFunctions.AddTrapezoid("Low", 0, 0.2, 0.4, 0.6);
        var cMedium = cpu.MembershipFunctions.AddTrapezoid("Medium", 0.2, 0.4, 0.6, 0.8);
        var cHigh = cpu.MembershipFunctions.AddTrapezoid("High", 0.4, 0.6, 0.8, 1);
        var cVeryHigh = cpu.MembershipFunctions.AddTrapezoid("VeryHigh", 0.6, 0.8, 1, 1);
        var cpuRules = new[] { cVeryLow, cLow, cMedium, cHigh, cVeryHigh };

        var mem = new LinguisticVariable("Memory");
        var mVeryLow = mem.MembershipFunctions.AddTrapezoid("VeryLow", 0, 0, 0.2, 0.4);
        var mLow = mem.MembershipFunctions.AddTrapezoid("Low", 0, 0.2, 0.4, 0.6);
        var mMedium = mem.MembershipFunctions.AddTrapezoid("Medium", 0.2, 0.4, 0.6, 0.8);
        var mHigh = mem.MembershipFunctions.AddTrapezoid("High", 0.4, 0.6, 0.8, 1);
        var mVeryHigh = mem.MembershipFunctions.AddTrapezoid("VeryHigh", 0.6, 0.8, 1, 1);
        var memRules = new[] { mVeryLow, mLow, mMedium, mHigh, mVeryHigh };

        var overall = new LinguisticVariable("Overall");
        var oVeryLow = overall.MembershipFunctions.AddTrapezoid("VeryLow", 0, 0, 0.2, 0.4);
        var oLow = overall.MembershipFunctions.AddTrapezoid("Low", 0, 0.2, 0.4, 0.6);
        var oMedium = overall.MembershipFunctions.AddTrapezoid("Medium", 0.2, 0.4, 0.6, 0.8);
        var oHigh = overall.MembershipFunctions.AddTrapezoid("High", 0.4, 0.6, 0.8, 1);
        var oVeryHigh = overall.MembershipFunctions.AddTrapezoid("VeryHigh", 0.6, 0.8, 1, 1);

        FLS.MembershipFunctions.IMembershipFunction[][] ruleMatrix = new[] {
            new[] { oVeryLow, oLow, oMedium, oHigh, oVeryHigh },
            new[] { oLow, oLow, oMedium, oHigh, oVeryHigh },
            new[] { oMedium, oMedium, oMedium, oHigh, oVeryHigh },
            new[] { oHigh, oHigh, oHigh, oHigh, oVeryHigh },
            new[] { oVeryHigh, oVeryHigh, oVeryHigh, oVeryHigh, oVeryHigh },
        };

        _fuzzyEngine = new FuzzyEngineFactory().Default();

        for (var i = 0; i < cpuRules.Length; i++)
        {
            for (int j = 0; j < memRules.Length; j++)
            {
                _fuzzyEngine.Rules.Add(Rule.If(
                    cpu.Is(cpuRules[i])
                    .And(mem.Is(memRules[j])))
                    .Then(overall.Is(ruleMatrix[i][j])));
            }
        }
    }

    public int GetThreadScale(double cpu, double memory, double ideal, int factor = 10)
    {
        var threadScale = (int)Math.Round((ideal - _fuzzyEngine.Defuzzify(new
        {
            Cpu = cpu > 1 ? 1 : cpu,
            Memory = memory > 1 ? 1 : memory
        })) * factor);
        return threadScale;
    }
}
