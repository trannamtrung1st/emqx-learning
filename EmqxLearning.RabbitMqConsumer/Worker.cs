using System.Diagnostics;
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
    private readonly NpgsqlDataSource _dataSource;
    private CancellationToken _stoppingToken;

    public Worker(ILogger<Worker> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;

        _dataSource = NpgsqlDataSource.Create(_configuration.GetConnectionString("DeviceDb"));

        var rabbitMqClientOptions = configuration.GetSection("RabbitMqClient");
        var factory = rabbitMqClientOptions.Get<ConnectionFactory>();
        _rabbitMqConnection = factory.CreateConnection();
        _rabbitMqChannel = _rabbitMqConnection.CreateModel();
        var rabbitMqChannelOptions = _configuration.GetSection("RabbitMqChannel");
        _rabbitMqChannel.BasicQos(
            prefetchSize: 0, // RabbitMQ not implemented
            prefetchCount: rabbitMqChannelOptions.GetValue<ushort>("PrefetchCount"),
            global: false);
        // _rabbitMqChannel.BasicQos(
        //     prefetchSize: 0, // RabbitMQ not implemented
        //     prefetchCount: rabbitMqChannelOptions.GetValue<ushort>("GlobalPrefetchCount"),
        //     global: true); // does not support in 'quorum' queue [TBD]
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

    private async Task OnMessageReceived(object sender, BasicDeliverEventArgs e)
    {
        var sw = Stopwatch.StartNew();
        var ingestionMessage = JsonSerializer.Deserialize<ReadIngestionMessage>(e.Body.ToArray());
        _logger.LogInformation("Metrics count {Count}", ingestionMessage.RawData.Count);
        var values = ConvertToSeriesRecords(ingestionMessage);
        var startInsert = sw.ElapsedMilliseconds;
        await InsertToDb(values);
        var insertTime = sw.ElapsedMilliseconds - startInsert;
        // Console.WriteLine($"Insert to DB: {insertTime}");
        _rabbitMqChannel.BasicAck(e.DeliveryTag, false);
        sw.Stop();
        // Console.WriteLine($"Total consuming time: {sw.ElapsedMilliseconds}");
    }

    private static IEnumerable<object> ConvertToSeriesRecords(ReadIngestionMessage message)
    {
        var data = message.RawData;
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(data["timestamp"].ToString()));
        var deviceId = data["deviceId"].ToString();
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
        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync();
        if (_configuration.GetValue<bool>("InsertDb"))
        {
            var inserted = await connection.ExecuteAsync(@"INSERT INTO device_metric_series(_ts, device_id, metric_key, value, retention_days)
                                                       VALUES (@Timestamp, @DeviceId, @MetricKey, @Value, @RetentionDays);", values);
            _logger.LogInformation("Records count: {Count}", inserted);
        }
        else
        {
            await Task.Delay(_configuration.GetValue<int>("ProcessingTime"));
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        _dataSource?.Dispose();
        _rabbitMqChannel?.Dispose();
        _rabbitMqConnection?.Dispose();
    }
}
