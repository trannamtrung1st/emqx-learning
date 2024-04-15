using System.Text.Json;
using EmqxLearning.Shared.Models;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace EmqxLearning.RabbitMqConsumer;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IConfiguration _configuration;
    private readonly IConnection _rabbitMqConnection;
    private readonly IModel _rabbitMqChannel;
    private CancellationToken _stoppingToken;

    public Worker(ILogger<Worker> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;

        var rabbitMqClientOptions = configuration.GetSection("RabbitMqClient");
        var factory = rabbitMqClientOptions.Get<ConnectionFactory>();
        _rabbitMqConnection = factory.CreateConnection();
        _rabbitMqChannel = _rabbitMqConnection.CreateModel();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var consumer = new EventingBasicConsumer(_rabbitMqChannel);
        consumer.Received += OnMessageReceived;
        _rabbitMqChannel.BasicConsume(queue: "ingestion", autoAck: true, consumer: consumer);

        while (!stoppingToken.IsCancellationRequested)
            await Task.Delay(1000, stoppingToken);
    }

    private void OnMessageReceived(object sender, BasicDeliverEventArgs e)
    {
        var ingestionMessage = JsonSerializer.Deserialize<ReadIngestionMessage>(e.Body.ToArray());
        _logger.LogInformation("Metrics count {Count}", ingestionMessage.RawData.Count);
        Thread.Sleep(_configuration.GetValue<int>("ProcessingTime"));
    }

    public override void Dispose()
    {
        base.Dispose();
        _rabbitMqChannel?.Dispose();
        _rabbitMqConnection?.Dispose();
    }
}
