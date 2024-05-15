using System.Text.Json;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (s, e) =>
{
    Console.WriteLine("Canceling ...");
    cts.Cancel();
    e.Cancel = true;
};
var cancellationToken = cts.Token;
var numOfDevices = GetArgument<int>(args, "n");
var interval = GetArgument<int>(args, "I");
var tcpServer = GetRawEnv("MqttClientOptions__TcpServer");
var topicFormat = GetRawEnv("MqttClientOptions__TopicFormat");
var noOfMetrics = GetArgument<int?>(args, "m") ?? 10;
var qos = GetArgument<MqttQualityOfServiceLevel>(args, "q");
var factory = new MqttFactory();
Console.WriteLine("Setup ...");

var clients = new List<IMqttClient>();

for (int i = 0; i < numOfDevices; i++)
{
    var mqttClient = factory.CreateMqttClient();
    var options = new MqttClientOptionsBuilder()
        .WithTcpServer(tcpServer)
        .Build();
    await mqttClient.ConnectAsync(options, cancellationToken);
    clients.Add(mqttClient);
}

Console.WriteLine("Running ...");

Parallel.ForEach(clients, async (mqttClient, _, i) =>
{
    while (!cancellationToken.IsCancellationRequested)
    {
        var dict = new Dictionary<string, object>();
        dict["deviceId"] = $"device-{i}";
        dict["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (int m = 0; m < noOfMetrics; m++)
            dict[$"numeric_{i}_{m}"] = Random.Shared.NextDouble();
        var messagePayload = JsonSerializer.SerializeToUtf8Bytes(dict);
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(string.Format(topicFormat, i, i))
            .WithPayload(messagePayload)
            .WithQualityOfServiceLevel(qos)
            .Build();
        await mqttClient.PublishAsync(message, cancellationToken);
        await Task.Delay(interval, cancellationToken);
    }
});

while (!cancellationToken.IsCancellationRequested)
    await Task.Delay(1000);

// static T GetEnv<T>(string varName) => JsonSerializer.Deserialize<T>(GetRawEnv(varName));

static string GetRawEnv(string varName) => Environment.GetEnvironmentVariable(varName);

static T GetArgument<T>(string[] args, string argName)
{
    var value = GetRawArgument(args, argName);
    if (value == null) return default;
    return JsonSerializer.Deserialize<T>(value);
}

static string GetRawArgument(string[] args, string argName)
{
    var arg = args.FirstOrDefault(a => a.StartsWith($"-{argName}="));
    if (arg == null) return null;
    var value = arg[(arg.IndexOf('=') + 1)..];
    return value;
}