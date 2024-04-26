using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using EmqxLearning.Shared.Models;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
using RabbitMQ.Client;
using EmqxLearning.Shared.Extensions;
using DeviceId;
using MQTTnet.Formatter;
using Polly.Registry;
using Polly;

namespace EmqxLearning.MqttListener;

public class Worker : BackgroundService
{
    private static long _messageCount = 0;
    private readonly ILogger<Worker> _logger;
    private readonly IConfiguration _configuration;
    private readonly IModel _rabbitMqChannel;
    private readonly ConcurrentBag<IManagedMqttClient> _mqttClients;
    private MqttClientOptions _options;
    private ManagedMqttClientOptions _managedOptions;
    private readonly RateLimiter _rateLimiter;
    private CancellationToken _stoppingToken;
    private readonly ResiliencePipeline _connectionErrorsPipeline;
    private readonly ResiliencePipeline _transientErrorsPipeline;

    public Worker(ILogger<Worker> logger,
        IConfiguration configuration,
        ResiliencePipelineProvider<string> resiliencePipelineProvider,
        IModel rabbitMqChannel)
    {
        _logger = logger;
        _configuration = configuration;
        _mqttClients = new ConcurrentBag<IManagedMqttClient>();

        _rabbitMqChannel = rabbitMqChannel;
        _rateLimiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions
        {
            PermitLimit = _configuration.GetValue<int>("ConcurrencyLimit"),
            QueueLimit = 0
        });

        _connectionErrorsPipeline = resiliencePipelineProvider.GetPipeline(Constants.ResiliencePipelines.ConnectionErrors);
        _transientErrorsPipeline = resiliencePipelineProvider.GetPipeline(Constants.ResiliencePipelines.TransientErrors);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        var maxThreads = _configuration.GetValue<int>("ProcessingThreads");
        for (int i = 0; i < maxThreads; i++)
            await Initialize(i);
        while (!stoppingToken.IsCancellationRequested)
            await Task.Delay(1000, stoppingToken);
    }

    private async Task Initialize(int threadIdx)
    {
        var backgroundProcessing = _configuration.GetValue<bool>("BackgroundProcessing");
        var factory = new MqttFactory();
        var mqttClient = factory.CreateManagedMqttClient();
        _mqttClients.Add(mqttClient);
        var mqttClientConfiguration = _configuration.GetSection("MqttClientOptions");
        var optionsBuilder = new MqttClientOptionsBuilder()
            .WithTcpServer(mqttClientConfiguration["TcpServer"])
            .WithCleanSession(value: mqttClientConfiguration.GetValue<bool>("CleanSession"))
            .WithSessionExpiryInterval(mqttClientConfiguration.GetValue<uint>("SessionExpiryInterval"))
            .WithProtocolVersion(MqttProtocolVersion.V500);
        string deviceId = new DeviceIdBuilder()
            .AddMachineName()
            .AddOsVersion()
            .ToString();

        var clientId = _configuration["MqttClientOptions:ClientId"] != null
            ? $"{_configuration["MqttClientOptions:ClientId"]}_{threadIdx}"
            : $"mqtt-listener_{deviceId}_{threadIdx}";
        optionsBuilder = optionsBuilder.WithClientId(clientId);

        _options = optionsBuilder.Build();
        _managedOptions = new ManagedMqttClientOptionsBuilder()
            .WithAutoReconnectDelay(TimeSpan.FromSeconds(_configuration.GetValue<int>("MqttClientOptions:ReconnectDelaySecs")))
            .WithClientOptions(_options)
            .Build();

        mqttClient.ConnectedAsync += (e) => OnConnected(e, mqttClient);
        mqttClient.DisconnectedAsync += OnDisconnected;
        mqttClient.ApplicationMessageReceivedAsync += backgroundProcessing ? OnMessageReceivedBackground : OnMessageReceivedNormal;
        await mqttClient.StartAsync(_managedOptions);
    }

    private async Task OnConnected(MqttClientConnectedEventArgs e, IManagedMqttClient mqttClient)
    {
        _logger.LogInformation("### CONNECTED WITH SERVER - ClientId: {0} ###", mqttClient.Options.ClientOptions.ClientId);
        var topic = _configuration["MqttClientOptions:Topic"];
        var qos = _configuration.GetValue<MQTTnet.Protocol.MqttQualityOfServiceLevel>("MqttClientOptions:QoS");

        await _connectionErrorsPipeline.ExecuteAsync(async (token) =>
        {
            try
            {
                await mqttClient.SubscribeAsync(topic: topic, qualityOfServiceLevel: qos);
                _logger.LogInformation("### SUBSCRIBED topic {0} - qos {1} ###", topic, qos);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, ex.Message);
                throw;
            }
        }, cancellationToken: _stoppingToken);
    }

    private async Task OnMessageReceivedNormal(MqttApplicationMessageReceivedEventArgs e)
    {
        await Task.Delay(_configuration.GetValue<int>("ProcessingTime"));
        Interlocked.Increment(ref _messageCount);
        _logger.LogInformation("{messageCount}", _messageCount);
    }

    private async Task OnMessageReceivedBackground(MqttApplicationMessageReceivedEventArgs e)
    {
        try
        {
            e.AutoAcknowledge = false;
            var lease = await _rateLimiter.WaitToAcquire(_stoppingToken);
            var _ = Task.Run(() => HandleMessage(e, lease));
            await Task.Delay(_configuration.GetValue<int>("ReceiveDelay"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, ex.Message);
        }
    }

    private async Task HandleMessage(MqttApplicationMessageReceivedEventArgs e, RateLimitLease lease)
    {
        try
        {
            await Task.Delay(_configuration.GetValue<int>("ProcessingTime"));
            var payloadStr = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);
            var payload = JsonSerializer.Deserialize<Dictionary<string, object>>(payloadStr);
            var ingestionMessage = new IngestionMessage(payload);
            SendIngestionMessage(ingestionMessage);
            Interlocked.Increment(ref _messageCount);
            _logger.LogInformation("{messageCount}", _messageCount);

            await _transientErrorsPipeline.ExecuteAsync(
                async (token) => await e.AcknowledgeAsync(token),
                _stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, ex.Message);
        }
        finally
        {
            lease.Dispose();
        }
    }

    private Task OnDisconnected(MqttClientDisconnectedEventArgs e)
    {
        _logger.LogError(e.Exception, "### DISCONNECTED FROM SERVER ### {Event}",
            e.Exception == null ? JsonSerializer.Serialize(e) : string.Empty);
        return Task.CompletedTask;
    }

    private void SendIngestionMessage(IngestionMessage ingestionMessage)
    {
        var properties = _rabbitMqChannel.CreateBasicProperties();
        properties.Persistent = true;
        properties.ContentType = "application/json";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(ingestionMessage);
        _transientErrorsPipeline.Execute(() =>
        {
            _rabbitMqChannel.BasicPublish(exchange: ingestionMessage.TopicName,
                routingKey: "all",
                basicProperties: properties,
                body: bytes);
        });
    }

    public override void Dispose()
    {
        base.Dispose();

        foreach (var mqttClient in _mqttClients)
            mqttClient.Dispose();
    }
}
