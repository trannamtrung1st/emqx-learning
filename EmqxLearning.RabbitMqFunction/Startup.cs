using System.Threading;
using EmqxLearning.RabbitMqFunction;
using Microsoft.Azure.Functions.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;

[assembly: FunctionsStartup(typeof(Startup))]
namespace EmqxLearning.RabbitMqFunction;

public class Startup : FunctionsStartup
{
    public Startup()
    {
    }

    public override void Configure(IFunctionsHostBuilder builder)
    {
        SetMinThread(builder);
    }

    private const int DEFAULT_MIN_THREAD = 300;
    private static void SetMinThread(IFunctionsHostBuilder builder)
    {
        var configuration = builder.GetContext().Configuration;
        var minThread = configuration.GetValue<int?>("Dotnet:MinThreads") ?? DEFAULT_MIN_THREAD;
        ThreadPool.SetMinThreads(minThread, minThread);
    }
}