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
using EmqxLearning.Shared.Services.Abstracts;

namespace EmqxLearning.MqttListener;

public class Worker : BackgroundService
{
    private const int DefaultLockSeconds = 3;
    private static long _messageCount = 0;
    private readonly ILogger<Worker> _logger;
    private readonly IConfiguration _configuration;
    private readonly IRabbitMqConnectionManager _rabbitMqConnectionManager;
    private readonly ConcurrentBag<IManagedMqttClient> _mqttClients;
    private MqttClientOptions _options;
    private ManagedMqttClientOptions _managedOptions;
    private readonly RateLimiter _rateLimiter;
    private CancellationToken _stoppingToken;
    private readonly ResiliencePipeline _connectionErrorsPipeline;
    private readonly ResiliencePipeline _transientErrorsPipeline;

    private static readonly SemaphoreSlim _circuitLock = new SemaphoreSlim(initialCount: 1);
    private bool _isCircuitOpen;

    public Worker(ILogger<Worker> logger,
        IConfiguration configuration,
        ResiliencePipelineProvider<string> resiliencePipelineProvider,
        IRabbitMqConnectionManager rabbitMqConnectionManager)
    {
        _logger = logger;
        _configuration = configuration;
        _mqttClients = new ConcurrentBag<IManagedMqttClient>();

        _rabbitMqConnectionManager = rabbitMqConnectionManager;
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
            await InitializeMqttClient(i);

        while (!stoppingToken.IsCancellationRequested)
            await Task.Delay(1000, stoppingToken);
    }

    private async Task StartMqttClients()
    {
        foreach (var mqttClient in _mqttClients)
        {
            await _connectionErrorsPipeline.ExecuteAsync(async (token) =>
            {
                try { await mqttClient.StartAsync(mqttClient.Options); }
                catch (Exception ex)
                {
                    _logger.LogWarning("Restarting MQTT client {ClientId} failed, reason: {Message}",
                        mqttClient.Options.ClientOptions.ClientId,
                        ex.Message);
                }
            });
        }
    }

    private async Task StopMqttClients()
    {
        foreach (var mqttClient in _mqttClients)
            await mqttClient.StopAsync();
    }

    private async Task InitializeMqttClient(int threadIdx)
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
        await _connectionErrorsPipeline.ExecuteAsync(async (token) =>
            await mqttClient.StartAsync(_managedOptions));
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
                _logger.LogError(ex, "Subscribing MQTT topic failed, reason: {Message}", ex.Message);
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
            var _ = Task.Run(async () =>
            {
                try { await HandleMessage(e); }
                catch (Exception ex) { _logger.LogError(ex, ex.Message); }
                finally { lease.Dispose(); }
            });
            await Task.Delay(_configuration.GetValue<int>("ReceiveDelay"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, ex.Message);
        }
    }

    private async Task HandleMessage(MqttApplicationMessageReceivedEventArgs e)
    {
        await Task.Delay(_configuration.GetValue<int>("ProcessingTime"));
        var payloadStr = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);
        var payload = JsonSerializer.Deserialize<Dictionary<string, object>>(payloadStr);
        var ingestionMessage = new IngestionMessage(payload);
        await SendIngestionMessage(ingestionMessage);
        Interlocked.Increment(ref _messageCount);
        _logger.LogInformation("{messageCount}", _messageCount);

        await _transientErrorsPipeline.ExecuteAsync(
            async (token) => await e.AcknowledgeAsync(token),
            _stoppingToken);
    }

    private Task OnDisconnected(MqttClientDisconnectedEventArgs e)
    {
        _logger.LogError(e.Exception, "### DISCONNECTED FROM SERVER ### {Event}",
            e.Exception == null ? JsonSerializer.Serialize(e) : string.Empty);
        return Task.CompletedTask;
    }

    private async Task SendIngestionMessage(IngestionMessage ingestionMessage)
    {
        try
        {
            var rabbitMqChannel = _rabbitMqConnectionManager.Channel;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(ingestionMessage);
            _transientErrorsPipeline.Execute(() =>
            {
                var properties = rabbitMqChannel.CreateBasicProperties();
                properties.Persistent = true;
                properties.ContentType = "application/json";
                rabbitMqChannel.BasicPublish(exchange: ingestionMessage.TopicName,
                    routingKey: "all",
                    basicProperties: properties,
                    body: bytes);
            });
        }
        catch
        {
            await _connectionErrorsPipeline.ExecuteAsync(async (token) =>
            {
                await OpenCircuit();
                var _ = Task.Run(async () =>
                {
                    var reconnectAfter = _configuration.GetValue<int>("ResilienceSettings:CircuitBreakerReconnectAfter");
                    await Task.Delay(reconnectAfter);
                    await CloseCircuit();
                });
            });
            throw;
        }
    }

    private async Task OpenCircuit()
    {
        var acquired = await _circuitLock.WaitAsync(TimeSpan.FromSeconds(DefaultLockSeconds));
        if (acquired)
        {
            try
            {
                if (_isCircuitOpen == true) return;
                _logger.LogWarning("Opening circuit breaker ...");
                await StopMqttClients();
                _rabbitMqConnectionManager.Close();
                _isCircuitOpen = true;
                _logger.LogWarning("Circuit breaker is now open");
            }
            finally { _circuitLock.Release(); }
        }
    }

    private async Task CloseCircuit()
    {
        var acquired = await _circuitLock.WaitAsync(TimeSpan.FromSeconds(DefaultLockSeconds));
        if (acquired)
        {
            try
            {
                if (_isCircuitOpen == false) return;
                _logger.LogWarning("Try closing circuit breaker ...");
                _connectionErrorsPipeline.Execute(() =>
                {
                    try { _rabbitMqConnectionManager.Connect(); }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Restarting RabbitMQ failed, reason: {Message}", ex.Message);
                        throw;
                    }
                });
                await StartMqttClients();
                _isCircuitOpen = false;
                _logger.LogWarning("Circuit breaker is now closed");
            }
            finally
            {
                _circuitLock.Release();
            }
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        foreach (var mqttClient in _mqttClients)
            mqttClient.Dispose();
        _mqttClients.Clear();
    }
}
