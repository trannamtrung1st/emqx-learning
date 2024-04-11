using System.Collections.Concurrent;
using System.Text;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;

namespace EmqxLearning.MqttListener;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IConfiguration _configuration;
    private readonly ConcurrentBag<IManagedMqttClient> _mqttClients;
    private MqttClientOptions _options;
    private ManagedMqttClientOptions _managedOptions;
    private CancellationToken _stoppingToken;

    public Worker(ILogger<Worker> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
        _mqttClients = new ConcurrentBag<IManagedMqttClient>();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        var maxThreads = _configuration.GetValue<int>("MqttClientOptions:ProcessingThreads");
        for (int i = 0; i < maxThreads; i++)
            await Initialize(i);
        while (!stoppingToken.IsCancellationRequested)
            await Task.Delay(1000, stoppingToken);
    }

    private async Task Initialize(int threadIdx)
    {
        var factory = new MqttFactory();
        var clientId = $"{_configuration["MqttClientOptions:ClientId"]}_{threadIdx}";
        var mqttClient = factory.CreateManagedMqttClient();
        _mqttClients.Add(mqttClient);
        _options = new MqttClientOptionsBuilder()
            .WithClientId(clientId)
            .WithTcpServer(_configuration["MqttClientOptions:TcpServer"])
            .WithCleanSession(value: _configuration.GetValue<bool>("MqttClientOptions:CleanSession"))
            .Build();
        _managedOptions = new ManagedMqttClientOptionsBuilder()
            .WithAutoReconnectDelay(TimeSpan.FromSeconds(_configuration.GetValue<int>("MqttClientOptions:ReconnectDelaySecs")))
            .WithClientOptions(_options)
            .Build();

        mqttClient.ConnectedAsync += (e) => OnConnected(e, mqttClient);
        mqttClient.DisconnectedAsync += OnDisconnected;
        mqttClient.ApplicationMessageReceivedAsync += OnMessageReceived;
        await mqttClient.StartAsync(_managedOptions);
    }

    private async Task OnConnected(MqttClientConnectedEventArgs e, IManagedMqttClient mqttClient)
    {
        _logger.LogInformation("### CONNECTED WITH SERVER - ClientId: {0} ###", mqttClient.Options.ClientOptions.ClientId);

        var topic = _configuration["MqttClientOptions:Topic"];
        var qos = _configuration.GetValue<MQTTnet.Protocol.MqttQualityOfServiceLevel>("MqttClientOptions:QoS");
        await mqttClient.SubscribeAsync(topic: topic, qualityOfServiceLevel: qos);

        _logger.LogInformation("### SUBSCRIBED topic {0} - qos {1} ###", topic, qos);
    }

    private async Task OnMessageReceived(MqttApplicationMessageReceivedEventArgs e)
    {
        _logger.LogInformation($"### RECEIVED APPLICATION MESSAGE ###\n"
        + $"+ Topic = {e.ApplicationMessage.Topic}\n"
        + $"+ Payload = {Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment)}\n"
        + $"+ QoS = {e.ApplicationMessage.QualityOfServiceLevel}\n"
        + $"+ Retain = {e.ApplicationMessage.Retain}\n");

        await Task.Delay(_configuration.GetValue<int>("MqttClientOptions:ProcessingTime"));

        _logger.LogInformation("Message processed done!");
    }

    private Task OnDisconnected(MqttClientDisconnectedEventArgs e)
    {
        _logger.LogInformation("### DISCONNECTED FROM SERVER ### {0}", e.Exception);
        return Task.CompletedTask;
    }

    public override void Dispose()
    {
        base.Dispose();

        foreach (var mqttClient in _mqttClients)
            mqttClient.Dispose();
    }
}
