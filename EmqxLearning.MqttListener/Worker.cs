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
using StackExchange.Redis;
using EmqxLearning.Shared.Helpers;
using TNT.Boilerplates.Concurrency.Abstracts;
using TNT.Boilerplates.Diagnostic.Abstracts;
using TNT.Boilerplates.Common.Disposable;

namespace EmqxLearning.MqttListener;

public class Worker : BackgroundService
{
    private readonly SemaphoreSlim _circuitLock = new(initialCount: 1);
    private readonly ILogger<Worker> _logger;
    private readonly IConfiguration _configuration;
    private readonly IRabbitMqConnectionManager _rabbitMqConnectionManager;
    private readonly ConcurrentBag<MqttClientWrapper> _mqttClients;
    private readonly ResiliencePipeline _connectionErrorsPipeline;
    private readonly ResiliencePipeline _transientErrorsPipeline;
    private readonly ISyncAsyncTaskRunner _taskRunner;
    private readonly IMultiRateLimiters _rateLimiters;
    private readonly IRateScalingController _rateScalingController;
    private readonly IResourceMonitor _resourceMonitor;
    private readonly IDatabase _cacheDb;
    private CancellationToken _stoppingToken;
    private int _cacheHitCount = 0;
    private bool _isCircuitOpen;
    private bool _shouldBreak;

    public Worker(ILogger<Worker> logger,
        IConfiguration configuration,
        ResiliencePipelineProvider<string> resiliencePipelineProvider,
        IRabbitMqConnectionManager rabbitMqConnectionManager,
        IRateScalingController rateScalingController,
        ConnectionMultiplexer connectionMultiplexer,
        ISyncAsyncTaskRunner taskRunner,
        IMultiRateLimiters rateLimiters,
        IResourceMonitor resourceMonitor)
    {
        _logger = logger;
        _configuration = configuration;
        _rateScalingController = rateScalingController;
        _resourceMonitor = resourceMonitor;
        _rabbitMqConnectionManager = rabbitMqConnectionManager;
        _taskRunner = taskRunner;
        _rateLimiters = rateLimiters;
        _cacheDb = connectionMultiplexer.GetDatabase();
        _mqttClients = new ConcurrentBag<MqttClientWrapper>();
        _connectionErrorsPipeline = resiliencePipelineProvider.GetPipeline(Constants.ResiliencePipelines.ConnectionErrors);
        _transientErrorsPipeline = resiliencePipelineProvider.GetPipeline(Constants.ResiliencePipelines.TransientErrors);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        SetupCancellationTokens();

        var noOfConns = _configuration.GetValue<int>("AppSettings:NumberOfConnections");
        for (int i = 0; i < noOfConns; i++)
            InitializeMqttClient(i);

        var resourceMonitorInterval = _configuration.GetValue<int>("AppSettings:ResourceMonitorInterval");
        var maxMemoryUsage = _configuration.GetValue<double>("AppSettings:MaxMemoryUsage");
        _resourceMonitor.Start(interval: resourceMonitorInterval);
        _resourceMonitor.Collected += (o, e) =>
        {
            var (cpu, mem) = e;
            _logger.LogInformation("===== Resource consumption =====\nCPU: {Cpu} - Memory: {Memory}", cpu, mem);
            _shouldBreak = mem > maxMemoryUsage;
        };
        _rateScalingController.StartRateCollector(rateLimiters: _rateLimiters.RateLimiters);
        _rateScalingController.Start(rateLimiters: _rateLimiters.RateLimiters);
        await StartMqttClients();

        while (!stoppingToken.IsCancellationRequested)
            await Task.Delay(1000, stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        var topic = _configuration["MqttClientOptions:Topic"];
        foreach (var wrapper in _mqttClients)
        {
            if (wrapper.Client.IsConnected)
                await wrapper.Client.UnsubscribeAsync(topic);
        }

        var checkLastMessageRangeInSecs = _configuration.GetValue<int>("AppSettings:CheckLastMessageRangeInSecs");
        var processingTasks = new List<Task>();
        foreach (var wrapper in _mqttClients)
        {
            var tcs = new TaskCompletionSource();
            _ = Task.Factory.StartNew(async () =>
            {
                try
                {
                    bool completed = false;
                    do
                    {
                        var hasMessageIncoming = DateTime.UtcNow.AddSeconds(-checkLastMessageRangeInSecs) <= wrapper.LastMessageTime;
                        completed = !_isCircuitOpen && !hasMessageIncoming && wrapper.BatchCount == 0 && wrapper.BatchId == null && !wrapper.LastBatchFlushing;
                        _logger.LogDebug("Client: {Id} - Incoming: {Incoming} - BatchCount: {BatchCount} - BatchId: {BatchId} - Flushing: {Flushing}",
                            wrapper.ChannelId, hasMessageIncoming, wrapper.BatchCount, wrapper.BatchId, wrapper.LastBatchFlushing);
                        if (!completed) await Task.Delay(1000);
                    } while (!completed);

                    await wrapper.Client.InternalClient.DisconnectAsync(new MqttClientDisconnectOptions
                    {
                        SessionExpiryInterval = 1,
                        Reason = MqttClientDisconnectOptionsReason.NormalDisconnection
                    });
                    await wrapper.Client.StopAsync();
                    tcs.SetResult();
                }
                catch (Exception ex) { tcs.SetException(ex); }
            }, creationOptions: TaskCreationOptions.LongRunning);
            processingTasks.Add(tcs.Task);
        }

        await Task.WhenAll(processingTasks);
        await Task.Delay(_configuration.GetValue<int>("AppSettings:ShutdownWait"));
        await base.StopAsync(cancellationToken);
    }

    private void SetupCancellationTokens()
    {
        CancellationTokenRegistration stoppingReg = default;
        stoppingReg = _stoppingToken.Register(() =>
        {
            using (stoppingReg) { OpenCircuit().Wait(); }
        });
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
            await StopMqttClient(wrapper);
    }

    private static async Task StopMqttClient(MqttClientWrapper wrapper)
    {
        wrapper.FlushBatchTimer.Stop();
        wrapper.TokenSource.TryCancel();
        await wrapper.SafeAccessBatch((batch) =>
        {
            batch.Clear();
            wrapper.BatchId = null;
            wrapper.LastBatchFlushing = false;
        });
        await wrapper.Client.StopAsync();
    }

    private void InitializeMqttClient(int threadIdx)
    {
        var backgroundProcessing = _configuration.GetValue<bool>("AppSettings:BackgroundProcessing");
        var factory = new MqttFactory();
        var mqttClient = factory.CreateManagedMqttClient();
        var flushBatchInterval = _configuration.GetValue<int>("AppSettings:FlushBatchInterval");
        var mqttClientConfiguration = _configuration.GetSection("MqttClientOptions");
        var optionsBuilder = new MqttClientOptionsBuilder()
            .WithTcpServer(options => mqttClientConfiguration.GetSection("Tcp").Bind(options))
            .WithKeepAlivePeriod(value: mqttClientConfiguration.GetValue<TimeSpan>("KeepAlivePeriod"))
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
        var wrapper = new MqttClientWrapper(mqttClient, managedOptions, threadIdx.ToString(), _stoppingToken, flushBatchInterval);
        _mqttClients.Add(wrapper);

        mqttClient.ConnectedAsync += (e) => OnConnected(e, mqttClient);
        mqttClient.DisconnectedAsync += (e) => OnDisconnected(e, wrapper);
        mqttClient.ApplicationMessageReceivedAsync += backgroundProcessing
            ? ((e) => OnMessageReceivedBackground(e, wrapper))
            : OnMessageReceivedNormal;

        _rabbitMqConnectionManager.ConfigureChannel(wrapper.ChannelId, SetupRabbitMqChannel(wrapper.ChannelId));
    }

    private async Task OnConnected(MqttClientConnectedEventArgs e, IManagedMqttClient mqttClient)
    {
        if (e.ConnectResult.ResultCode != MqttClientConnectResultCode.Success)
            throw new Exception($"Cannot connect, result code: {e.ConnectResult.ResultCode}");

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
        await Task.Delay(_configuration.GetValue<int>("AppSettings:ProcessingTime"));
        _logger.LogInformation("Received message at {Time}", DateTime.Now);
    }

    private async Task OnMessageReceivedBackground(MqttApplicationMessageReceivedEventArgs e, MqttClientWrapper wrapper)
    {
        try
        {
            if (_shouldBreak)
            {
                _ = Task.Run(HandleOpenCircuit);
                return;
            }

            wrapper.LastMessageTime = DateTime.UtcNow;
            e.AutoAcknowledge = false;
            var payload = e.ApplicationMessage.PayloadSegment.Array;

            var taskScope = _rateLimiters.TaskLimiter.TryAcquire(count: 1);
            var sizeScope = taskScope != null ? _rateLimiters.SizeLimiter.TryAcquire(count: payload.Length) : null;
            var rateScope = sizeScope != null ? new SimpleScope(taskScope, sizeScope) : null;
            if (rateScope == null && taskScope != null)
                taskScope.Dispose();

            await _taskRunner.RunSyncAsync(rateScope, async (scope) =>
            {
                await using var _ = scope;
                try
                {
                    var payloadHash = Hashing.Md5Hash(payload);
                    var duplicated = await CheckAndProcessDuplicates(e, wrapper, payloadHash);
                    if (duplicated) return;
                    await HandleMessage(new(e, payloadHash), wrapper);
                }
                catch (DownstreamDisconnectedException ex)
                {
                    _logger.LogError(ex, ex.Message);
                    var _1 = Task.Run(HandleOpenCircuit);
                }
                catch (Exception ex) { _logger.LogError(ex, ex.Message); }
            }, longRunning: false);

            await Task.Delay(_configuration.GetValue<int>("AppSettings:ReceiveDelay"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, ex.Message);
        }
    }

    private async Task<bool> CheckAndProcessDuplicates(MqttApplicationMessageReceivedEventArgs e, MqttClientWrapper wrapper, byte[] payloadHash)
    {
        var hashExpiryTime = _configuration.GetValue<TimeSpan>("AppSettings:HashExpiryTime");
        if (_configuration.GetValue<bool>("AppSettings:CheckDuplicates"))
        {
            var cacheHit = await _cacheDb.StringGetAsync(payloadHash);
            if (cacheHit.HasValue)
            {
                Interlocked.Increment(ref _cacheHitCount);
                _logger.LogWarning("Cache hit {Count}", _cacheHitCount);
                await AcknowledgeAsync(e, cancellationToken: wrapper.TokenSource.Token);
                return true;
            }
        }
        await _cacheDb.StringSetAsync(key: payloadHash, value: true, expiry: hashExpiryTime);
        return false;
    }

    private async Task RemoveCaches(IEnumerable<MessageWrapper> messages)
    {
        var batch = _cacheDb.CreateBatch();
        var tasks = new List<Task>();
        foreach (var message in messages)
            tasks.Add(batch.KeyDeleteAsync(message.PayloadHash));
        batch.Execute();
        await Task.WhenAll(tasks);
    }

    private async Task HandleOpenCircuit()
    {
        var opened = await OpenCircuit();
        if (!opened) return;
        var closeAfter = _configuration.GetValue<int>("ResilienceSettings:CloseCircuitBreakerAfter");
        System.Timers.Timer closeTimer = new System.Timers.Timer(closeAfter);
        closeTimer.Elapsed += async (s, e) =>
        {
            while (_shouldBreak)
                await Task.Delay(closeAfter);
            await CloseCircuit();
        };
        closeTimer.AutoReset = false;
        closeTimer.Start();
    }

    private async Task HandleMessage(MessageWrapper message, MqttClientWrapper wrapper)
    {
        await Task.Delay(_configuration.GetValue<int>("AppSettings:ProcessingTime"));
        await ProcessBatch(wrapper, message: message, isFlushInterval: false);
    }

    private async Task ProcessBatch(MqttClientWrapper wrapper, MessageWrapper message, bool isFlushInterval)
    {
        var batchSize = _configuration.GetValue<int>("RabbitMqChannel:PublisherConfirmBatchSize");
        var channel = _rabbitMqConnectionManager.GetChannel(wrapper.ChannelId);
        List<MessageWrapper> confirmBatch = null;
        Guid? flushBatchId = null;
        await wrapper.SafeAccessBatch((batch) =>
        {
            if (batch.Count > 0 && (isFlushInterval || batch.Count >= batchSize))
            {
                confirmBatch = new List<MessageWrapper>();
                while (batch.TryDequeue(out var message))
                    confirmBatch.Add(message);
                flushBatchId = wrapper.BatchId;
                wrapper.LastBatchFlushing = true;
            }
            if (message != null)
            {
                if (wrapper.LastBatchFlushing || wrapper.BatchId == null)
                {
                    wrapper.BatchId = Guid.NewGuid();
                    wrapper.LastBatchFlushing = false;
                }
                batch.Enqueue(message);
            }
        });

        if (confirmBatch == null) return;
        var confirmTimeout = _configuration.GetValue<TimeSpan>("RabbitMqChannel:PublisherConfirmTimeout");
        bool confirmed;
        try
        {
            var publishBatch = channel.CreateBasicPublishBatch();
            foreach (var item in confirmBatch)
            {
                var payload = JsonSerializer.Deserialize<Dictionary<string, object>>(item.EventArgs.ApplicationMessage.PayloadSegment);
                var ingestionMessage = new IngestionMessage(payload);
                AddIngestionMessage(channel, publishBatch, ingestionMessage);
            }

            _transientErrorsPipeline.Execute(
                (token) => publishBatch.Publish(),
                cancellationToken: wrapper.TokenSource.Token);

            confirmed = _transientErrorsPipeline.Execute(
                (token) => channel.WaitForConfirms(confirmTimeout),
                cancellationToken: wrapper.TokenSource.Token);
        }
        catch (Exception ex)
        {
            await RemoveCaches(confirmBatch);
            throw new DownstreamDisconnectedException(ex.Message, innerException: ex);
        }

        // [TODO] handle when confirmed == false
        foreach (var item in confirmBatch)
            await AcknowledgeAsync(item.EventArgs, wrapper.TokenSource.Token);

        await wrapper.SafeAccessBatch((batch) =>
        {
            if (wrapper.BatchId == flushBatchId && wrapper.LastBatchFlushing)
            {
                wrapper.BatchId = null;
                wrapper.LastBatchFlushing = false;
            }
        });
    }

    private async Task AcknowledgeAsync(MqttApplicationMessageReceivedEventArgs e, CancellationToken cancellationToken)
    {
        try
        {
            await _transientErrorsPipeline.ExecuteAsync(
                async (token) =>
                {
                    if (_isCircuitOpen) throw new CircuitOpenException();
                    await e.AcknowledgeAsync(cancellationToken: cancellationToken);
                },
                cancellationToken: cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // [NOTE] due to internal client issues, message can be acknowledged multiple times
        }
    }

    private Task OnDisconnected(MqttClientDisconnectedEventArgs e, MqttClientWrapper wrapper)
    {
        wrapper.TokenSource.TryCancel();
        var isNormal = e.Reason == MqttClientDisconnectReason.NormalDisconnection;
        _logger.Log(logLevel: isNormal ? LogLevel.Information : LogLevel.Error,
            exception: e.Exception, message: "### DISCONNECTED FROM SERVER ### {Event}",
            JsonSerializer.Serialize(new
            {
                e.ClientWasConnected,
                e.ConnectResult,
                e.Reason,
                e.ReasonString,
                e.UserProperties
            }));
        return Task.CompletedTask;
    }

    private void AddIngestionMessage(IModel rabbitMqChannel, IBasicPublishBatch batch, IngestionMessage ingestionMessage)
    {
        ReadOnlyMemory<byte> bytes = JsonSerializer.SerializeToUtf8Bytes(ingestionMessage);
        var properties = rabbitMqChannel.CreateBasicProperties();
        properties.Persistent = true;
        properties.ContentType = "application/json";
        batch.Add(
            exchange: ingestionMessage.TopicName,
            routingKey: "all",
            mandatory: true,
            properties: properties,
            body: bytes);
    }

    private async Task<bool> OpenCircuit()
    {
        var lockTimeout = _configuration.GetValue<TimeSpan>("AppSettings:CircuitLockTimeout");
        var acquired = await _circuitLock.WaitAsync(lockTimeout);
        if (acquired)
        {
            try
            {
                if (_isCircuitOpen == true) return false;
                _logger.LogWarning("Opening circuit breaker ...");
                _rateScalingController.Stop();
                _rateScalingController.StopRateCollector();
                await StopMqttClients();
                _rabbitMqConnectionManager.Close();
                foreach (var rateLimiter in _rateLimiters.RateLimiters)
                    rateLimiter.ResetLimit();
                _isCircuitOpen = true;
                _logger.LogWarning("Circuit breaker is now open");
            }
            finally { _circuitLock.Release(); }
        }
        return acquired;
    }

    private async Task CloseCircuit()
    {
        var lockTimeout = _configuration.GetValue<TimeSpan>("AppSettings:CircuitLockTimeout");
        var acquired = await _circuitLock.WaitAsync(lockTimeout);
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
                        _logger.LogWarning("Reconnecting RabbitMQ failed, reason: {Message}", ex.Message);
                        throw;
                    }
                });
                _rateScalingController.StartRateCollector(rateLimiters: _rateLimiters.RateLimiters);
                _rateScalingController.Start(rateLimiters: _rateLimiters.RateLimiters);
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

    private async Task StartMqttClients()
    {
        _connectionErrorsPipeline.Execute(() => _rabbitMqConnectionManager.Connect());

        foreach (var wrapper in _mqttClients)
        {
            await _connectionErrorsPipeline.ExecuteAsync(async (token) =>
                await wrapper.Client.StartAsync(wrapper.ManagedOptions),
                cancellationToken: _stoppingToken);

            wrapper.FlushBatchTimer.Elapsed += async (s, e) =>
            {
                try { await ProcessBatch(wrapper, message: null, isFlushInterval: true); }
                catch (DownstreamDisconnectedException ex)
                {
                    _logger.LogError(ex, ex.Message);
                    var _ = Task.Run(HandleOpenCircuit);
                }
                catch (Exception ex) { _logger.LogError(ex, ex.Message); }
            };
            wrapper.FlushBatchTimer.Start();
        }
    }

    private Action<IModel> SetupRabbitMqChannel(string channelId)
    {
        void ConfigureChannel(IModel channel)
        {
            channel.ContinuationTimeout = _configuration.GetValue<TimeSpan?>("RabbitMqChannel:ContinuationTimeout") ?? channel.ContinuationTimeout;
            channel.ModelShutdown += (sender, e) => OnModelShutdown(sender, e, channelId);
            channel.ConfirmSelect();
        }
        return ConfigureChannel;
    }

    private void OnModelShutdown(object sender, ShutdownEventArgs e, string channelId)
    {
        if (e.Exception != null)
            _logger.LogError(e.Exception, "RabbitMQ channel {ChannelId} shutdown reason: {Reason} | Message: {Message}", channelId, e.Cause, e.Exception?.Message);
        else
            _logger.LogInformation("RabbitMQ channel {ChannelId} shutdown reason: {Reason}", channelId, e.Cause);
    }

    public override void Dispose()
    {
        base.Dispose();
        GC.SuppressFinalize(this);
        foreach (var wrapper in _mqttClients)
            wrapper.Client.Dispose();
        _mqttClients.Clear();
    }
}

internal class MessageWrapper
{
    public MqttApplicationMessageReceivedEventArgs EventArgs { get; }
    public byte[] PayloadHash { get; }

    public MessageWrapper(MqttApplicationMessageReceivedEventArgs eventArgs, byte[] payloadHash)
    {
        EventArgs = eventArgs;
        PayloadHash = payloadHash;
    }
}

internal class MqttClientWrapper
{
    private CancellationTokenSource _tokenSource;
    private readonly IManagedMqttClient _client;
    private readonly CancellationToken _stoppingToken;
    private readonly ManagedMqttClientOptions _managedOptions;
    private readonly SemaphoreSlim _batchLock;
    private readonly Queue<MessageWrapper> _batch;

    public MqttClientWrapper(
        IManagedMqttClient client,
        ManagedMqttClientOptions managedOptions,
        string channelId,
        CancellationToken stoppingToken,
        int flushBatchInterval)
    {
        _client = client;
        _managedOptions = managedOptions;
        _stoppingToken = stoppingToken;
        ChannelId = channelId;
        _batchLock = new SemaphoreSlim(1);
        _batch = new Queue<MessageWrapper>();
        FlushBatchTimer = new System.Timers.Timer(flushBatchInterval)
        {
            AutoReset = true
        };
        ResetTokenSource();
    }

    public DateTime LastMessageTime { get; set; }
    public IManagedMqttClient Client => _client;
    public ManagedMqttClientOptions ManagedOptions => _managedOptions;
    public CancellationTokenSource TokenSource => _tokenSource;
    public System.Timers.Timer FlushBatchTimer { get; }
    public int BatchCount => _batch.Count;
    public Guid? BatchId { get; set; }
    public bool LastBatchFlushing { get; set; }
    public string ChannelId { get; }

    public async Task SafeAccessBatch(Action<Queue<MessageWrapper>> action)
    {
        await _batchLock.WaitAsync();
        try { action(_batch); }
        finally { _batchLock.Release(); }
    }

    public void ResetTokenSource()
    {
        _tokenSource?.Dispose();
        if (_stoppingToken.IsCancellationRequested) return;
        _tokenSource = new CancellationTokenSource();
        CancellationTokenRegistration reg = default;
        reg = _tokenSource.Token.Register(() =>
        {
            using (reg) { ResetTokenSource(); }
        });
    }
}