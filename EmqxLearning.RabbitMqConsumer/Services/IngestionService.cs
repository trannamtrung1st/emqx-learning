using System.Text.Json;
using Dapper;
using EmqxLearning.RabbitMqConsumer.Services.Abstracts;
using EmqxLearning.Shared.Models;
using EmqxLearning.Shared.Services.Abstracts;
using Npgsql;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace EmqxLearning.RabbitMqConsumer.Services;

public class IngestionService : IIngestionService
{
    private readonly IRabbitMqConnectionManager _rabbitMqConnectionManager;
    private readonly NpgsqlDataSource _dataSource;
    private readonly IConfiguration _configuration;
    private readonly ILogger<IngestionService> _logger;
    public IngestionService(
        IRabbitMqConnectionManager rabbitMqConnectionManager,
        ILogger<IngestionService> logger,
        IConfiguration configuration)
    {
        _rabbitMqConnectionManager = rabbitMqConnectionManager;
        _logger = logger;
        _configuration = configuration;
        _dataSource = NpgsqlDataSource.Create(_configuration.GetConnectionString("DeviceDb"));
    }

    public async Task HandleMessage(BasicDeliverEventArgs e, CancellationToken cancellationToken)
    {
        var ingestionMessage = JsonSerializer.Deserialize<ReadIngestionMessage>(e.Body.ToArray());
        _logger.LogInformation("Metrics count {Count}", ingestionMessage.RawData.Count);
        var values = ConvertToSeriesRecords(ingestionMessage);
        await InsertToDb(values);
        _rabbitMqConnectionManager.Channel.BasicAck(e.DeliveryTag, multiple: false);
    }

    private static IEnumerable<object> ConvertToSeriesRecords(ReadIngestionMessage message)
    {
        var data = message.RawData;
        var deviceId = data["deviceId"].ToString();
        data.Remove("timestamp");
        data.Remove("deviceId");
        var values = new List<object>();
        foreach (var kvp in message.RawData)
        {
            values.Add(new
            {
                Timestamp = DateTime.Now,
                DeviceId = deviceId,
                MetricKey = kvp.Key,
                Value = Random.Shared.NextDouble(),
                RetentionDays = 90
            });
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

    public void Configure(Func<Task> reconnectConsumer)
    {
        // [NOTE] not implemented
    }
}