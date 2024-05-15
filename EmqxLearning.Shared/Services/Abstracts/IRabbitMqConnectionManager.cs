using RabbitMQ.Client;

namespace EmqxLearning.Shared.Services.Abstracts;

public interface IRabbitMqConnectionManager
{
    IConnection Connection { get; }
    IModel GetChannel(string channelId);

    void ConfigureConnection(ConnectionFactory connectionFactory, Action<IConnection> configure);
    void ConfigureChannel(string channelId, Action<IModel> configure);
    void Connect();
    void Close();
}