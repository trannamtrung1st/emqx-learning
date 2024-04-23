using System.Collections.Concurrent;
using System.Text.Json;
using EmqxLearning.RabbitMqConsumer.Extensions;
using EmqxLearning.RabbitMqConsumer.Services.Abstracts;
using EmqxLearning.Shared.Models;
using Npgsql;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace EmqxLearning.RabbitMqConsumer.Services;

public class BatchIngestionService : IIngestionService
{
    private readonly IModel _rabbitMqChannel;
    private readonly NpgsqlDataSource _dataSource;
    private readonly IConfiguration _configuration;
    private readonly ILogger<IngestionService> _logger;
    private readonly ConcurrentQueue<(ReadIngestionMessage Payload, BasicDeliverEventArgs EventArgs)> _messages;
    private CancellationToken _stoppingToken;
    public BatchIngestionService(
        IModel rabbitMqChannel,
        ILogger<IngestionService> logger,
        IConfiguration configuration)
    {
        _rabbitMqChannel = rabbitMqChannel;
        _logger = logger;
        _configuration = configuration;
        _messages = new ConcurrentQueue<(ReadIngestionMessage message, BasicDeliverEventArgs eventArgs)>();
        _dataSource = NpgsqlDataSource.Create(_configuration.GetConnectionString("DeviceDb"));
        SetupWorkerThreads();
    }

    public Task HandleMessage(BasicDeliverEventArgs e, CancellationToken cancellationToken)
    {
        _stoppingToken = cancellationToken;
        var ingestionMessage = JsonSerializer.Deserialize<ReadIngestionMessage>(e.Body.ToArray());
        _logger.LogInformation("Metrics count {Count}", ingestionMessage.RawData.Count);
        _messages.Enqueue((ingestionMessage, e));
        return Task.CompletedTask;
    }

    private void SetupWorkerThreads()
    {
        var workerThreadCount = _configuration.GetValue<int>("BatchSettings:WorkerThreadCount");
        var batchInterval = _configuration.GetValue<int>("BatchSettings:BatchInterval");
        var batchSize = _configuration.GetValue<int>("BatchSettings:BatchSize");
        var comparer = new IngestionMessageComparer();
        for (int i = 0; i < workerThreadCount; i++)
        {
            var aTimer = new System.Timers.Timer(batchInterval);
            aTimer.Elapsed += async (s, e) =>
            {
                try
                {
                    if (_messages.Count == 0) return;
                    var batch = new List<ReadIngestionMessage>();
                    var deliveryTags = new List<ulong>();
                    while (batch.Count < batchSize && _messages.TryDequeue(out var message))
                    {
                        batch.Add(message.Payload);
                        deliveryTags.Add(message.EventArgs.DeliveryTag);
                    }
                    batch.Sort(comparer);
                    await HandleBatch(batch, deliveryTags);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, ex.Message);
                }
            };
            aTimer.AutoReset = true;
            aTimer.Enabled = true;
        }
    }

    private async Task HandleBatch(List<ReadIngestionMessage> batch, List<ulong> deliveryTags)
    {
        await InsertToDb(batch);
        foreach (var tag in deliveryTags)
            _rabbitMqChannel.BasicAck(tag, multiple: false);
    }

    const string SeriesTable = "device_metric_series";
    const string SeriesColumns = "_ts, device_id, metric_key, value, retention_days";
    private async Task InsertToDb(IEnumerable<ReadIngestionMessage> messages)
    {
        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync();
        using NpgsqlBinaryImporter writer = connection.BeginBinaryImport($"COPY {SeriesTable} ({SeriesColumns}) FROM STDIN (FORMAT BINARY)");

        foreach (var message in messages)
        {
            var data = message.RawData;
            var deviceId = data["deviceId"].ToString();
            data.Remove("timestamp");
            data.Remove("deviceId");
            var values = new List<object>();
            foreach (var kvp in message.RawData)
            {
                writer.StartRow();
                writer.Write(DateTime.Now, NpgsqlTypes.NpgsqlDbType.Timestamp);
                writer.Write(deviceId);
                writer.Write(kvp.Key);
                writer.Write(Random.Shared.NextDouble(), NpgsqlTypes.NpgsqlDbType.Numeric);
                writer.WriteNullable(90);
            }
        }

        await writer.CompleteAsync();
    }
}

class IngestionMessageComparer : IComparer<ReadIngestionMessage>
{
    public int Compare(ReadIngestionMessage x, ReadIngestionMessage y)
    {
        var xTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(x.RawData["timestamp"].ToString()));
        var yTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(y.RawData["timestamp"].ToString()));
        if (xTimestamp > yTimestamp) return 1;
        if (xTimestamp < yTimestamp) return -1;
        return 0;
    }
}