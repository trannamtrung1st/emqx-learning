using System.Text.Json;
using Dapper;
using EmqxLearning.Shared.Models;
using Npgsql;
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
            await Task.Delay(1000, _stoppingToken);
    }

    private void OnMessageReceived(object sender, BasicDeliverEventArgs e)
    {
        var ingestionMessage = JsonSerializer.Deserialize<ReadIngestionMessage>(e.Body.ToArray());
        _logger.LogInformation("Metrics count {Count}", ingestionMessage.RawData.Count);
        var values = ConvertToSeriesRecords(ingestionMessage);
        InsertToDb(values).Wait();
        Thread.Sleep(_configuration.GetValue<int>("ProcessingTime"));
    }

    private static IEnumerable<object> ConvertToSeriesRecords(ReadIngestionMessage message)
    {
        var data = message.RawData;
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(data["timestamp"].ToString()));
        var deviceId = (string)data["deviceId"];
        data.Remove("timestamp");
        data.Remove("deviceId");
        var values = new List<object>();
        foreach (var kvp in message.RawData)
        {
            if (double.TryParse(kvp.Value.ToString(), out var numericValue))
            {
                values.Add(new
                {
                    Timestamp = timestamp,
                    DeviceId = deviceId,
                    MetricKey = kvp.Key,
                    Value = numericValue,
                    RetentionDays = 90
                });
            }
        }
        return values;
    }

    private async Task InsertToDb(IEnumerable<object> values)
    {
        await using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(_configuration.GetConnectionString("DeviceDb"));
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync();
        var inserted = await connection.ExecuteAsync(@"INSERT INTO device_metric_series(_ts, device_id, metric_key, value, retention_days)
                                                       VALUES (@Timestamp, @DeviceId, @MetricKey, @Value, @RetentionDays);", values);
        _logger.LogInformation("Records count: {Count}", inserted);
    }

    public override void Dispose()
    {
        base.Dispose();
        _rabbitMqChannel?.Dispose();
        _rabbitMqConnection?.Dispose();
    }
}
