using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using EmqxLearning.Shared.Models;
using Microsoft.Extensions.Configuration;

namespace EmqxLearning.RabbitMqFunction.RabbitMQ;

public class ProcessRabbitMQMessage
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<ProcessRabbitMQMessage> _logger;

    public ProcessRabbitMQMessage(
        IConfiguration configuration,
        ILogger<ProcessRabbitMQMessage> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    [FunctionName("ProcessRabbitMQMessage")]
    public async Task RunAsync([RabbitMQTrigger("ingestion", ConnectionStringSetting = "RabbitMQ")] byte[] data)
    {
        var ingestionMessage = JsonSerializer.Deserialize<ReadIngestionMessage>(data);
        _logger.LogInformation("Metrics count {Count}", ingestionMessage.RawData.Count);
        await Task.Delay(_configuration.GetValue<int>("ProcessingTime"));
    }
}
