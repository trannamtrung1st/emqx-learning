using EmqxLearning.MqttListener;
using Polly;
using EmqxLearning.Shared.Extensions;

IHost host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((context, services) =>
    {
        var configuration = context.Configuration;
        var resilienceSettings = configuration.GetSection("ResilienceSettings");
        services.AddHostedService<Worker>();

        const string ConnectionErrorsKey = Constants.ResiliencePipelines.ConnectionErrors;
        services.AddResiliencePipeline<string, object>(ConnectionErrorsKey, builder =>
        {
            builder.AddDefaultRetry(
                retryAttempts: resilienceSettings.GetValue<int?>($"{ConnectionErrorsKey}:RetryAttempts") ?? int.MaxValue,
                delaySecs: resilienceSettings.GetValue<int>($"{ConnectionErrorsKey}:DelaySecs")
            );
        });
        const string TransientErrorsKey = Constants.ResiliencePipelines.TransientErrors;
        services.AddResiliencePipeline<string, object>(TransientErrorsKey, builder =>
        {
            builder.AddDefaultRetry(
                retryAttempts: resilienceSettings.GetValue<int>($"{TransientErrorsKey}:RetryAttempts"),
                delaySecs: resilienceSettings.GetValue<int>($"{TransientErrorsKey}:DelaySecs")
            );
        });
    })
    .Build();

await host.RunAsync();
