using System.Text;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;

namespace EmqxLearning.MqttListener;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IConfiguration _configuration;
    private IManagedMqttClient _mqttClient;
    private MqttClientOptions _options;
    private ManagedMqttClientOptions _managedOptions;
    private CancellationToken _stoppingToken;

    public Worker(ILogger<Worker> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        await Initialize();
        while (!stoppingToken.IsCancellationRequested)
            await Task.Delay(1000, stoppingToken);
    }

    private async Task Initialize()
    {
        var factory = new MqttFactory();
        _mqttClient = factory.CreateManagedMqttClient();
        _options = new MqttClientOptionsBuilder()
            .WithClientId(_configuration["MqttClientOptions:ClientId"])
            .WithTcpServer(_configuration["MqttClientOptions:TcpServer"])
            .WithCleanSession(value: _configuration.GetValue<bool>("MqttClientOptions:CleanSession"))
            .Build();
        _managedOptions = new ManagedMqttClientOptionsBuilder()
            .WithAutoReconnectDelay(TimeSpan.FromSeconds(_configuration.GetValue<int>("MqttClientOptions:ReconnectDelaySecs")))
            .WithClientOptions(_options)
            .Build();

        _mqttClient.ConnectedAsync += OnConnected;
        _mqttClient.DisconnectedAsync += OnDisconnected;
        _mqttClient.ApplicationMessageReceivedAsync += OnMessageReceived;
        await _mqttClient.StartAsync(_managedOptions);
    }

    private async Task OnConnected(MqttClientConnectedEventArgs e)
    {
        _logger.LogInformation("### CONNECTED WITH SERVER ###");

        var topic = _configuration["MqttClientOptions:Topic"];
        var qos = _configuration.GetValue<MQTTnet.Protocol.MqttQualityOfServiceLevel>("MqttClientOptions:QoS");
        await _mqttClient.SubscribeAsync(topic: topic, qualityOfServiceLevel: qos);

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
        _logger.LogInformation("### DISCONNECTED FROM SERVER ### {0}", e);
        return Task.CompletedTask;
    }

    public override void Dispose()
    {
        base.Dispose();

        if (_mqttClient != null)
            _mqttClient.Dispose();
    }
}
