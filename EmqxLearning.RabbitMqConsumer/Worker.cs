using EmqxLearning.RabbitMqConsumer.Services.Abstracts;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace EmqxLearning.RabbitMqConsumer;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IConfiguration _configuration;
    private readonly IModel _rabbitMqChannel;
    private readonly IIngestionService _ingestionService;
    private CancellationToken _stoppingToken;

    public Worker(ILogger<Worker> logger,
        IConfiguration configuration,
        IModel rabbitMqChannel,
        IIngestionService ingestionService)
    {
        _ingestionService = ingestionService;
        _rabbitMqChannel = rabbitMqChannel;
        _logger = logger;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;

        var consumerCount = _configuration.GetValue<int>("ConsumerCount");
        for (int i = 0; i < consumerCount; i++)
        {
            var consumer = new AsyncEventingBasicConsumer(_rabbitMqChannel);
            consumer.Received += OnMessageReceived;
            _rabbitMqChannel.BasicConsume(queue: "ingestion", autoAck: false, consumer: consumer);
        }

        while (!stoppingToken.IsCancellationRequested)
            await Task.Delay(1000, _stoppingToken);
    }

    private Task OnMessageReceived(object sender, BasicDeliverEventArgs e)
        => _ingestionService.HandleMessage(e, _stoppingToken);
}
