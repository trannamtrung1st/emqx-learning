using System.Text.Json;

namespace EmqxLearning.Shared.Helpers;

public static class ConsoleHelper
{
    public static T GetEnv<T>(string varName) => JsonSerializer.Deserialize<T>(GetRawEnv(varName));

    public static string GetRawEnv(string varName) => Environment.GetEnvironmentVariable(varName);

    public static T GetArgument<T>(string[] args, string argName)
    {
        var value = GetRawArgument(args, argName);
        if (value == null) return default;
        return JsonSerializer.Deserialize<T>(value);
    }

    public static string GetRawArgument(string[] args, string argName)
    {
        var arg = args.FirstOrDefault(a => a.StartsWith($"-{argName}="));
        if (arg == null) return null;
        var value = arg[(arg.IndexOf('=') + 1)..];
        return value;
    }
}