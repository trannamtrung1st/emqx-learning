using System.Collections.Concurrent;
using System.Text.Json;
using EmqxLearning.Shared.Models;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
using RabbitMQ.Client;
using DeviceId;
using MQTTnet.Formatter;
using Polly.Registry;
using Polly;
using EmqxLearning.Shared.Services.Abstracts;
using EmqxLearning.Shared.Exceptions;
using MQTTnet.Internal;

namespace EmqxLearning.MqttListener;

public class Worker : BackgroundService
{
    private const int DefaultMovingAverageRange = 20;
    private const int DefaultConcurrencyCollectorInterval = 250;
    private const int DefaultLockSeconds = 3;
    private const int AcceptedAvailableConcurrency = 10;
    private const int AcceptedQueueCount = 5;

    private SemaphoreSlim _concurrencyCollectorLock = new SemaphoreSlim(1);
    private Queue<int> _queueCounts = new Queue<int>();
    private Queue<int> _availableCounts = new Queue<int>();
    private System.Timers.Timer _concurrencyCollector;
    private readonly ILogger<Worker> _logger;
    private readonly IConfiguration _configuration;
    private readonly IRabbitMqConnectionManager _rabbitMqConnectionManager;
    private readonly ConcurrentBag<MqttClientWrapper> _mqttClients;
    private readonly ResiliencePipeline _connectionErrorsPipeline;
    private readonly ResiliencePipeline _transientErrorsPipeline;
    private bool _resourceMonitorSet = false;
    private readonly IResourceMonitor _resourceMonitor;
    private readonly IFuzzyThreadController _fuzzyThreadController;
    private readonly IDynamicRateLimiter _dynamicRateLimiter;
    private CancellationToken _stoppingToken;
    private CancellationTokenSource _circuitTokenSource;
    private static readonly SemaphoreSlim _circuitLock = new SemaphoreSlim(initialCount: 1);
    private bool _isCircuitOpen;

    public Worker(ILogger<Worker> logger,
        IConfiguration configuration,
        ResiliencePipelineProvider<string> resiliencePipelineProvider,
        IRabbitMqConnectionManager rabbitMqConnectionManager,
        IResourceMonitor resourceMonitor,
        IFuzzyThreadController fuzzyThreadController,
        IDynamicRateLimiter dynamicRateLimiter)
    {
        _logger = logger;
        _configuration = configuration;
        _resourceMonitor = resourceMonitor;
        _fuzzyThreadController = fuzzyThreadController;
        _dynamicRateLimiter = dynamicRateLimiter;
        _dynamicRateLimiter.SetLimit(_configuration.GetValue<int>("InitialConcurrencyLimit")).Wait();
        _rabbitMqConnectionManager = rabbitMqConnectionManager;
        _circuitTokenSource = new CancellationTokenSource();
        _mqttClients = new ConcurrentBag<MqttClientWrapper>();
        _connectionErrorsPipeline = resiliencePipelineProvider.GetPipeline(Constants.ResiliencePipelines.ConnectionErrors);
        _transientErrorsPipeline = resiliencePipelineProvider.GetPipeline(Constants.ResiliencePipelines.TransientErrors);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        SetupCancellationTokens();
        StartConcurrencyCollector();
        StartDynamicScalingWorker();

        var noOfConns = _configuration.GetValue<int>("NumberOfConnections");
        for (int i = 0; i < noOfConns; i++)
            await InitializeMqttClient(i);

        while (!stoppingToken.IsCancellationRequested)
            await Task.Delay(1000, stoppingToken);
    }

    private void SetupCancellationTokens()
    {
        _stoppingToken.Register(() => _circuitTokenSource.Cancel());
        _circuitTokenSource.Token.Register(() =>
        {
            foreach (var wrapper in _mqttClients)
                wrapper.TokenSource.TryCancel();
        });
    }

    private void StartDynamicScalingWorker()
    {
        if (!_resourceMonitorSet)
        {
            _resourceMonitorSet = true;
            var scaleFactor = _configuration.GetValue<int>("ScaleFactor");
            var initialConcurrencyLimit = _configuration.GetValue<int>("InitialConcurrencyLimit");
            _resourceMonitor.SetMonitor(async (cpu, mem) =>
            {
                try
                {
                    await ScaleConcurrency(cpu, mem, scaleFactor, initialConcurrencyLimit);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, ex.Message);
                }
            }, interval: _configuration.GetValue<int>("ScaleCheckInterval"));
        }
        _resourceMonitor.Start();
    }

    private async Task ScaleConcurrency(double cpu, double mem, int scaleFactor, int initialConcurrencyLimit)
    {
        var threadScale = _fuzzyThreadController.GetThreadScale(cpu, mem, factor: scaleFactor);
        if (threadScale == 0) return;
        var (queueCountAvg, availableCountAvg) = await GetConcurrencyStatistics();
        var (concurrencyLimit, _, _, _) = _dynamicRateLimiter.State;
        int newLimit;
        if (threadScale < 0)
            newLimit = concurrencyLimit + threadScale;
        else if (queueCountAvg <= AcceptedQueueCount && availableCountAvg > AcceptedAvailableConcurrency)
            newLimit = concurrencyLimit - threadScale / 2;
        else
            newLimit = concurrencyLimit + threadScale;
        if (newLimit < initialConcurrencyLimit) newLimit = initialConcurrencyLimit;
        await _dynamicRateLimiter.SetLimit(newLimit, cancellationToken: _circuitTokenSource.Token);
        _logger.LogWarning(
            "CPU: {Cpu} - Memory: {Memory}\n" +
            "Scale: {Scale} - Available count: {Available} - Queue count: {QueueCount}\n" +
            "New thread limit: {Limit}",
            cpu, mem, threadScale, availableCountAvg, queueCountAvg, newLimit);
    }

    private void StartConcurrencyCollector()
    {
        if (_concurrencyCollector == null)
        {
            _concurrencyCollector = new System.Timers.Timer(DefaultConcurrencyCollectorInterval);
            _concurrencyCollector.AutoReset = true;
            _concurrencyCollector.Elapsed += async (s, e) =>
            {
                await _concurrencyCollectorLock.WaitAsync(_circuitTokenSource.Token);
                try
                {
                    if (_queueCounts.Count == DefaultMovingAverageRange) _queueCounts.TryDequeue(out var _);
                    if (_availableCounts.Count == DefaultMovingAverageRange) _availableCounts.TryDequeue(out var _);
                    var (_, _, concurrencyAvailable, concurrencyQueueCount) = _dynamicRateLimiter.State;
                    _queueCounts.Enqueue(concurrencyQueueCount);
                    _availableCounts.Enqueue(concurrencyAvailable);
                }
                finally { _concurrencyCollectorLock.Release(); }
            };
        }
        _concurrencyCollector.Start();
    }

    private async Task<(int QueueCountAvg, int AvailableCountAvg)> GetConcurrencyStatistics()
    {
        int queueCountAvg;
        int availableCountAvg;
        await _concurrencyCollectorLock.WaitAsync(_circuitTokenSource.Token);
        try
        {
            queueCountAvg = _queueCounts.Count > 0 ? (int)_queueCounts.Average() : 0;
            availableCountAvg = _availableCounts.Count > 0 ? (int)_availableCounts.Average() : 0;
            return (queueCountAvg, availableCountAvg);
        }
        finally { _concurrencyCollectorLock.Release(); }
    }

    private async Task RestartMqttClients()
    {
        foreach (var wrapper in _mqttClients)
        {
            var mqttClient = wrapper.Client;
            await _connectionErrorsPipeline.ExecuteAsync(async (token) =>
            {
                try { await mqttClient.StartAsync(mqttClient.Options); }
                catch (Exception ex)
                {
                    _logger.LogWarning("Reconnecting MQTT client {ClientId} failed, reason: {Message}",
                        mqttClient.Options.ClientOptions.ClientId,
                        ex.Message);
                    throw;
                }
            });
        }
    }

    private async Task StopMqttClients()
    {
        foreach (var wrapper in _mqttClients)
        {
            wrapper.TokenSource.TryCancel();
            await wrapper.Client.StopAsync();
        }
    }

    private async Task InitializeMqttClient(int threadIdx)
    {
        var backgroundProcessing = _configuration.GetValue<bool>("BackgroundProcessing");
        var factory = new MqttFactory();
        var mqttClient = factory.CreateManagedMqttClient();
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

        var options = optionsBuilder.Build();
        var managedOptions = new ManagedMqttClientOptionsBuilder()
            .WithAutoReconnectDelay(TimeSpan.FromSeconds(_configuration.GetValue<int>("MqttClientOptions:ReconnectDelaySecs")))
            .WithClientOptions(options)
            .Build();
        var wrapper = new MqttClientWrapper(mqttClient);
        _mqttClients.Add(wrapper);

        mqttClient.ConnectedAsync += (e) => OnConnected(e, mqttClient);
        mqttClient.DisconnectedAsync += (e) => OnDisconnected(e, wrapper);
        mqttClient.ApplicationMessageReceivedAsync += backgroundProcessing
            ? ((e) => OnMessageReceivedBackground(e, wrapper))
            : OnMessageReceivedNormal;

        await _connectionErrorsPipeline.ExecuteAsync(async (token) =>
            await mqttClient.StartAsync(managedOptions),
            cancellationToken: _stoppingToken);
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
        _logger.LogInformation("Received message at {Time}", DateTime.Now);
    }

    private async Task OnMessageReceivedBackground(MqttApplicationMessageReceivedEventArgs e, MqttClientWrapper wrapper)
    {
        try
        {
            e.AutoAcknowledge = false;
            await _dynamicRateLimiter.Acquire(cancellationToken: wrapper.TokenSource.Token);
            var _ = Task.Run(async () =>
            {
                try { await HandleMessage(e, wrapper); }
                catch (DownstreamDisconnectedException ex)
                {
                    _logger.LogError(ex, ex.Message);
                    var _ = Task.Run(HandleOpenCircuit);
                }
                catch (Exception ex) { _logger.LogError(ex, ex.Message); }
                finally { await _dynamicRateLimiter.Release(); }
            });
            await Task.Delay(_configuration.GetValue<int>("ReceiveDelay"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, ex.Message);
        }
    }

    private async Task HandleOpenCircuit()
    {
        await OpenCircuit();
        var reconnectAfter = _configuration.GetValue<int>("ResilienceSettings:CircuitBreakerReconnectAfter");
        System.Timers.Timer closeTimer = new System.Timers.Timer(reconnectAfter);
        closeTimer.Elapsed += async (s, e) => await CloseCircuit();
        closeTimer.AutoReset = false;
        closeTimer.Start();
    }

    private async Task HandleMessage(MqttApplicationMessageReceivedEventArgs e, MqttClientWrapper wrapper)
    {
        await Task.Delay(_configuration.GetValue<int>("ProcessingTime"));
        var payload = JsonSerializer.Deserialize<Dictionary<string, object>>(e.ApplicationMessage.PayloadSegment);
        var ingestionMessage = new IngestionMessage(payload);
        SendIngestionMessage(ingestionMessage);

        await _transientErrorsPipeline.ExecuteAsync(
            async (token) =>
            {
                if (_isCircuitOpen) throw new CircuitOpenException();
                await e.AcknowledgeAsync(token);
            },
            cancellationToken: wrapper.TokenSource.Token);
    }

    private Task OnDisconnected(MqttClientDisconnectedEventArgs e, MqttClientWrapper wrapper)
    {
        wrapper.TokenSource.TryCancel();
        _logger.LogError(e.Exception, "### DISCONNECTED FROM SERVER ### {Event}",
            e.Exception == null ? JsonSerializer.Serialize(e) : string.Empty);
        return Task.CompletedTask;
    }

    private void SendIngestionMessage(IngestionMessage ingestionMessage)
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
        catch (Exception ex)
        {
            throw new DownstreamDisconnectedException(ex.Message, ex);
        }
    }

    private void StopConcurrencyCollector()
    {
        _concurrencyCollector.Stop();
        _queueCounts.Clear();
        _availableCounts.Clear();
    }

    private void StopDynamicScalingWorker() => _resourceMonitor.Stop();

    private async Task OpenCircuit()
    {
        var acquired = await _circuitLock.WaitAsync(TimeSpan.FromSeconds(DefaultLockSeconds));
        if (acquired)
        {
            try
            {
                if (_isCircuitOpen == true) return;
                _logger.LogWarning("Opening circuit breaker ...");
                StopDynamicScalingWorker();
                StopConcurrencyCollector();
                _circuitTokenSource.Cancel();
                await StopMqttClients();
                _rabbitMqConnectionManager.Close();
                await _dynamicRateLimiter.SetLimit(_configuration.GetValue<int>("InitialConcurrencyLimit"));
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
                _circuitTokenSource?.Dispose();
                _circuitTokenSource = new CancellationTokenSource();
                _connectionErrorsPipeline.Execute(() =>
                {
                    try { _rabbitMqConnectionManager.Connect(); }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Reconnecting RabbitMQ failed, reason: {Message}", ex.Message);
                        throw;
                    }
                });
                StartConcurrencyCollector();
                StartDynamicScalingWorker();
                await RestartMqttClients();
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
        foreach (var wrapper in _mqttClients)
        {
            if (wrapper.Client.IsConnected)
            {
                // [TODO] graceful shutdown
                wrapper.Client.InternalClient.DisconnectAsync(new MqttClientDisconnectOptions
                {
                    SessionExpiryInterval = 1
                }).Wait();
            }
            wrapper.Client.Dispose();
        }
        _mqttClients.Clear();
        // [TODO] dispose others
    }
}

internal class MqttClientWrapper
{
    private CancellationTokenSource _tokenSource;
    private readonly IManagedMqttClient _client;

    public MqttClientWrapper(IManagedMqttClient client)
    {
        _client = client;
        ResetTokenSource();
    }

    public IManagedMqttClient Client => _client;
    public CancellationTokenSource TokenSource => _tokenSource;

    public void ResetTokenSource()
    {
        _tokenSource?.Dispose();
        _tokenSource = new CancellationTokenSource();
        _tokenSource.Token.Register(() => ResetTokenSource());
    }
}