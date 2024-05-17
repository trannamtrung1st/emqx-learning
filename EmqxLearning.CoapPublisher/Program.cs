using System.Text.Json;
using CoAPnet;
using CoAPnet.Client;
using EmqxLearning.Shared.Helpers;

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
var coapServer = ConsoleHelper.GetRawEnv("CoapClientOptions__CoapServer");
var topicFormat = ConsoleHelper.GetRawEnv("CoapClientOptions__TopicFormat");
var port = ConsoleHelper.GetArgument<int?>(args, "p") ?? 5683;
var noOfMetrics = ConsoleHelper.GetArgument<int?>(args, "m") ?? 10;
var qos = ConsoleHelper.GetArgument<int>(args, "q");
var coapFactory = new CoapFactory();

Console.WriteLine("Setup ...");

var clients = new List<ICoapClient>();

for (int i = 0; i < numOfDevices; i++)
{
    var coapClient = coapFactory.CreateClient();
    var connectOptions = new CoapClientConnectOptionsBuilder()
        .WithHost(coapServer)
        .WithPort(port)
        .Build();
    connectOptions.CommunicationTimeout = TimeSpan.FromSeconds(60);
    await coapClient.ConnectAsync(connectOptions, cancellationToken);
    clients.Add(coapClient);
}

Console.WriteLine("Running ...");

Parallel.ForEach(clients, async (coapClient, _, i) =>
{
    while (!cancellationToken.IsCancellationRequested)
    {
        var dict = new Dictionary<string, object>();
        dict["deviceId"] = $"device-coap-{i}";
        dict["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (int m = 0; m < noOfMetrics; m++)
            dict[$"numeric_{i}_{m}"] = Random.Shared.NextDouble();
        var messagePayload = JsonSerializer.SerializeToUtf8Bytes(dict);
        var topic = string.Format(topicFormat, i, i);
        var path = $"/ps/{topic}";

        var request = new CoapRequestBuilder()
            .WithMethod(CoapRequestMethod.Post)
            .WithPath(path)
            .WithQuery(new[] { $"qos={qos}" })
            .WithPayload(messagePayload)
            .Build();
        await coapClient.RequestAsync(request, cancellationToken);
        await Task.Delay(interval, cancellationToken);
    }
});

while (!cancellationToken.IsCancellationRequested)
    await Task.Delay(1000);
