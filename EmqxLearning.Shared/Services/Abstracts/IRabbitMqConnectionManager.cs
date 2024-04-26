using RabbitMQ.Client;

namespace EmqxLearning.Shared.Services.Abstracts;

public interface IRabbitMqConnectionManager
{
    IConnection Connection { get; }
    IModel Channel { get; }

    void ConfigureConnection(ConnectionFactory connectionFactory, Action<IConnection> configure);
    void ConfigureChannel(Action<IModel> configure);
    void Connect();
    void Close();
}