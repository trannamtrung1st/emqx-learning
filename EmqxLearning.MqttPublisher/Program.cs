using System.Text.Json;
using EmqxLearning.Shared.Helpers;
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
var numOfDevices = ConsoleHelper.GetArgument<int>(args, "n");
var interval = ConsoleHelper.GetArgument<int>(args, "I");
var tcpServer = ConsoleHelper.GetRawEnv("MqttClientOptions__TcpServer");
var topicFormat = ConsoleHelper.GetRawEnv("MqttClientOptions__TopicFormat");
var noOfMetrics = ConsoleHelper.GetArgument<int?>(args, "m") ?? 10;
var qos = ConsoleHelper.GetArgument<MqttQualityOfServiceLevel>(args, "q");
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
