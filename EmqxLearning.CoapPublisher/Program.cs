using System.Text.Json;
using CoAP;

CoapPublish("127.0.0.1", 5684, "test-coap-mqtt", 5000);

static void CoapPublish(string host, int port, string topic, int delayInMilliseconds, CancellationToken cancellationToken = default)
{
    var clientId = "COAPPUB";
    var client = new CoapClient(new Uri($"coap://{host}:{port}/mqtt/connection?clientid={clientId}"));
    var response = client.Post("");
    var token = response.ResponseText;
    Console.WriteLine($"Connection token {token}");
    client.Uri = new Uri($"coap://{host}:{port}/mqtt/{topic}?clientid={clientId}&token={token}&qos=1");

    double minTemperature = 20;
    double minHumidity = 60;
    int messageId = 1;
    Random rand = new Random();

    while (!cancellationToken.IsCancellationRequested)
    {
        double currentTemperature = minTemperature + rand.NextDouble() * 15;
        double currentHumidity = minHumidity + rand.NextDouble() * 20;
        int random = rand.Next(1, 10);
        var payload = GetPayload(messageId, currentTemperature, currentHumidity, random);
        var serializedPayload = JsonSerializer.Serialize(payload);

        var clientResponse = client.Post(serializedPayload);
        Console.WriteLine($"Sent topic:{topic}");
        Console.WriteLine($"Sent payload: {serializedPayload} \nStatus code: {clientResponse.CodeString}");
        Thread.Sleep(delayInMilliseconds);
    }
}

static object GetPayload(
    int messageId,
    double currentTemperature,
    double currentHumidity,
    int random,
    string deviceId = "device")
{
    var payload = new
    {
        messageId = messageId++,
        temperature = currentTemperature,
        humidity = currentHumidity,
        deviceId = deviceId,
        timestamp = ConvertToUnixTimestamp(),
        ack = (random % 2) == 0 ? false : true,
        snr = random,
        txt = random.ToString() + "txt",
        intValue = random
    };

    return payload;
}

static long ConvertToUnixTimestamp() => DateTimeOffset.Now.ToUnixTimeMilliseconds();