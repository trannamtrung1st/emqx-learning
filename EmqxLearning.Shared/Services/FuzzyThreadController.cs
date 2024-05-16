using EmqxLearning.Shared.Services.Abstracts;
using FLS;
using FLS.Rules;

namespace EmqxLearning.Shared.Services;

public class FuzzyThreadController : IFuzzyThreadController
{
    public const double DefaultIdealUsage = 0.6;
    private readonly IFuzzyEngine _fuzzyEngine;

    public FuzzyThreadController()
    {

        var cpu = new LinguisticVariable("Cpu");
        var cVeryLow = cpu.MembershipFunctions.AddTrapezoid("VeryLow", 0, 0, 0.2, 0.4);
        var cLow = cpu.MembershipFunctions.AddTrapezoid("Low", 0, 0.2, 0.4, 0.6);
        var cMedium = cpu.MembershipFunctions.AddTrapezoid("Medium", 0.2, 0.4, 0.6, 0.8);
        var cHigh = cpu.MembershipFunctions.AddTrapezoid("High", 0.4, 0.8, 1, 1);
        var cVeryHigh = cpu.MembershipFunctions.AddTrapezoid("VeryHigh", 0.8, 1, 1, 1);
        var cpuRules = new[] { cVeryLow, cLow, cMedium, cHigh, cVeryHigh };

        var mem = new LinguisticVariable("Memory");
        var mVeryLow = mem.MembershipFunctions.AddTrapezoid("VeryLow", 0, 0, 0.2, 0.4);
        var mLow = mem.MembershipFunctions.AddTrapezoid("Low", 0, 0.2, 0.4, 0.6);
        var mMedium = mem.MembershipFunctions.AddTrapezoid("Medium", 0.2, 0.4, 0.6, 0.8);
        var mHigh = mem.MembershipFunctions.AddTrapezoid("High", 0.4, 0.8, 1, 1);
        var mVeryHigh = mem.MembershipFunctions.AddTrapezoid("VeryHigh", 0.8, 1, 1, 1);
        var memRules = new[] { mVeryLow, mLow, mMedium, mHigh, mVeryHigh };

        var change = new LinguisticVariable("Change");
        var chIncFast = change.MembershipFunctions.AddTrapezoid("IncFast", 0, 0, 0.2, 0.4);
        var chIncNormal = change.MembershipFunctions.AddTrapezoid("IncNormal", 0.2, 0.4, 0.6, 0.8);
        var chNoChange = change.MembershipFunctions.AddTrapezoid("NoChange", 0.7, 0.75, 0.85, 0.9);
        var chDecNormal = change.MembershipFunctions.AddTrapezoid("DecNormal", 0.8, 0.85, 0.9, 0.95);
        var chDecFast = change.MembershipFunctions.AddTrapezoid("DecFast", 0.9, 0.95, 1, 1);

        FLS.MembershipFunctions.IMembershipFunction[][] ruleMatrix = new[] {
            new[] { chIncFast, chIncFast, chIncNormal, chNoChange, chDecFast },
            new[] { chIncFast, chIncFast, chIncNormal, chNoChange, chDecFast },
            new[] { chIncNormal, chIncNormal, chIncNormal, chNoChange, chDecFast },
            new[] { chNoChange, chNoChange, chNoChange, chDecNormal, chDecFast },
            new[] { chDecFast, chDecFast, chDecFast, chDecFast, chDecFast },
        };

        _fuzzyEngine = new FuzzyEngineFactory().Default();

        for (var i = 0; i < cpuRules.Length; i++)
        {
            for (int j = 0; j < memRules.Length; j++)
            {
                _fuzzyEngine.Rules.Add(Rule.If(
                    cpu.Is(cpuRules[i])
                    .And(mem.Is(memRules[j])))
                    .Then(change.Is(ruleMatrix[i][j])));
            }
        }
    }

    public int GetThreadScale(double cpu, double memory, int factor = 10)
    {
        var threadScale = (int)Math.Round((DefaultIdealUsage - _fuzzyEngine.Defuzzify(new
        {
            Cpu = cpu > 1 ? 1 : cpu,
            Memory = memory > 1 ? 1 : memory
        })) * factor);
        return threadScale;
    }
}