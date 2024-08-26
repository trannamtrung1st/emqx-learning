using System.Text.Json;
using EmqxLearning.Shared.Helpers;
using RabbitMQ.Client;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (s, e) =>
{
    Console.WriteLine("Canceling ...");
    cts.Cancel();
    e.Cancel = true;
};
var cancellationToken = cts.Token;
var numOfDevices = ConsoleHelper.GetArgument<int?>(args, "n") ?? 1;
var interval = ConsoleHelper.GetArgument<int?>(args, "I") ?? 1000;
var hostName = ConsoleHelper.GetRawEnv("RabbitMqClient__HostName") ?? "localhost";
var exchangeName = ConsoleHelper.GetRawEnv("RabbitMqClient__ExchangeName") ?? "ingestion-exchange";
var factory = new ConnectionFactory()
{
    HostName = hostName,
    UserName = "rabbitmq",
    Password = "Pass1234!"
};

Console.WriteLine("Setup ...");

var clients = new List<IConnection>();
var channels = new List<IModel>();

for (int i = 0; i < numOfDevices; i++)
{
    var conn = factory.CreateConnection();
    var model = conn.CreateModel();
    clients.Add(conn);
    channels.Add(model);
}

Console.WriteLine("Running ...");

Parallel.ForEach(channels, async (channel, _, i) =>
{
    var properties = channel.CreateBasicProperties();
    properties.Persistent = true;
    properties.ContentType = "application/json";

    while (!cancellationToken.IsCancellationRequested)
    {
        var payload = new
        {
            message = new
            {
                topicName = "ingestion-exchange",
                rawData = new
                {
                    eui = "00-00-64-ff-fe-a3-8f-e0",
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    zVelocity = Random.Shared.Next(maxValue: 100),
                    tenantId = "0779433e-f36b-1410-8650-00f91313348c",
                    subscriptionId = "0e79433e-f36b-1410-8650-00f91313348c",
                    projectId = "34e5ee62-429c-4724-b3d0-3891bd0a08c9",
                    brokerId = "e705f30a-3e12-4da7-6c47-08dcc5d354ed"
                },
                actionType = 1
            }
        };
        ReadOnlyMemory<byte> bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        channel.BasicPublish(exchangeName, routingKey: "all", mandatory: true, basicProperties: properties, body: bytes);
        await Task.Delay(interval, cancellationToken);
    }
});

while (!cancellationToken.IsCancellationRequested)
    await Task.Delay(1000);
