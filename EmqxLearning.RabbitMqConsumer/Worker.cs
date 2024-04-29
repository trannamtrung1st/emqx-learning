using EmqxLearning.RabbitMqConsumer.Services.Abstracts;
using EmqxLearning.Shared.Services.Abstracts;
using Polly;
using Polly.Registry;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace EmqxLearning.RabbitMqConsumer;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IConfiguration _configuration;
    private readonly IRabbitMqConnectionManager _rabbitMqConnectionManager;
    private readonly IIngestionService _ingestionService;
    private CancellationToken _stoppingToken;
    private readonly ResiliencePipeline _connectionErrorsPipeline;

    public Worker(ILogger<Worker> logger,
        IConfiguration configuration,
        IRabbitMqConnectionManager rabbitMqConnectionManager,
        IIngestionService ingestionService,
        ResiliencePipelineProvider<string> resiliencePipelineProvider)
    {
        _ingestionService = ingestionService;
        _rabbitMqConnectionManager = rabbitMqConnectionManager;
        _logger = logger;
        _configuration = configuration;

        _connectionErrorsPipeline = resiliencePipelineProvider.GetPipeline(Constants.ResiliencePipelines.ConnectionErrors);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;

        _ingestionService.Configure(reconnectConsumer: SetupConsumers);
        await SetupConsumers();

        while (!stoppingToken.IsCancellationRequested)
            await Task.Delay(1000, _stoppingToken);
    }

    private Task SetupConsumers()
    {
        var channel = _rabbitMqConnectionManager.Channel;
        var consumerCount = _configuration.GetValue<int>("ConsumerCount");
        for (int i = 0; i < consumerCount; i++)
        {
            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.Received += OnMessageReceived;
            _connectionErrorsPipeline.Execute(() =>
                channel.BasicConsume(queue: "ingestion", autoAck: false, consumer: consumer));
        }
        return Task.CompletedTask;
    }

    private Task OnMessageReceived(object sender, BasicDeliverEventArgs e)
        => _ingestionService.HandleMessage(e, _stoppingToken);
}
