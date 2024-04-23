using RabbitMQ.Client.Events;

namespace EmqxLearning.RabbitMqConsumer.Services.Abstracts;

public interface IIngestionService
{
    Task HandleMessage(BasicDeliverEventArgs e, CancellationToken cancellationToken);
}