using RabbitMQ.Client.Events;

namespace EmqxLearning.RabbitMqConsumer.Services.Abstracts;

public interface IIngestionService
{
    void Configure(Func<Task> reconnectConsumer);
    Task HandleMessage(string channelId, BasicDeliverEventArgs e, CancellationToken cancellationToken);
}