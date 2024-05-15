using EmqxLearning.Shared.Services.Abstracts;
using FLS;
using RabbitMQ.Client;

namespace EmqxLearning.Shared.Services;

public class RabbitMqConnectionManager : IRabbitMqConnectionManager, IDisposable
{
    private static readonly object _connectionLock = new object();
    private static readonly object _channelLock = new object();
    private ConnectionFactory _connectionFactory;
    private Action<IConnection> _configureConnection;
    private IConnection _currentConnection;
    private readonly Dictionary<string, Action<IModel>> _configureChannels;
    private readonly Dictionary<string, IModel> _channels;

    public IConnection Connection { get { lock (_connectionLock) { return _currentConnection; } } }
    public IModel GetChannel(string channelId) => _channels[channelId];

    public RabbitMqConnectionManager()
    {
        _configureChannels = new Dictionary<string, Action<IModel>>();
        _channels = new Dictionary<string, IModel>();
    }

    public void ConfigureConnection(ConnectionFactory connectionFactory, Action<IConnection> configure)
    {
        _connectionFactory = connectionFactory;
        _configureConnection = configure;
    }

    public void ConfigureChannel(string channelId, Action<IModel> configure)
    {
        _configureChannels[channelId] = configure;
    }

    public void Connect()
    {
        try
        {
            CreateNewConnection();
            _configureChannels.Keys.ForEach(key => CreateNewChannel(key));
        }
        catch
        {
            DisposeChannel();
            DisposeConnection();
            throw;
        }
    }

    public void Close()
    {
        DisposeChannel();
        DisposeConnection();
    }

    private void CreateNewConnection()
    {
        lock (_connectionLock)
        {
            _currentConnection = _connectionFactory.CreateConnection();
            if (_configureConnection != null) _configureConnection(_currentConnection);
        }
    }

    private bool CreateNewChannel(string channelId)
    {
        if (Connection == null) throw new ArgumentException(nameof(Connection));
        if (!Connection.IsOpen) return false;
        lock (_channelLock)
        {
            var channel = Connection.CreateModel();
            _channels[channelId] = channel;
            _configureChannels[channelId](channel);
        }
        return true;
    }

    public void Dispose()
    {
        DisposeChannel();
        DisposeConnection();
    }

    private void DisposeChannel()
    {
        _channels.Values.ForEach(ch =>
        {
            if (ch?.IsClosed != true)
            {
                ch?.Close();
                ch?.Dispose();
            }
        });
    }

    private void DisposeConnection()
    {
        if (_currentConnection?.IsOpen == true)
        {
            _currentConnection?.Close();
            _currentConnection?.Dispose();
        }
    }
}