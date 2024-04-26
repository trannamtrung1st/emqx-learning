using EmqxLearning.Shared.Services.Abstracts;
using RabbitMQ.Client;

namespace EmqxLearning.Shared.Services;

public class RabbitMqConnectionManager : IRabbitMqConnectionManager, IDisposable
{
    private static readonly object _connectionLock = new object();
    private static readonly object _channelLock = new object();
    private ConnectionFactory _connectionFactory;
    private Action<IConnection> _configureConnection;
    private Action<IModel> _configureChannel;
    private IConnection _currentConnection;
    private IModel _currentChannel;

    public IConnection Connection { get { lock (_connectionLock) { return _currentConnection; } } }
    public IModel Channel { get { lock (_channelLock) { return _currentChannel; } } }

    public RabbitMqConnectionManager()
    {
    }

    public void ConfigureConnection(ConnectionFactory connectionFactory, Action<IConnection> configure)
    {
        _connectionFactory = connectionFactory;
        _configureConnection = configure;
    }

    public void ConfigureChannel(Action<IModel> configure)
    {
        _configureChannel = configure;
    }

    public void Connect()
    {
        try
        {
            CreateNewConnection();
            CreateNewChannel();
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

    private bool CreateNewChannel()
    {
        if (Connection == null) throw new ArgumentException(nameof(Connection));
        if (!Connection.IsOpen) return false;
        lock (_channelLock)
        {
            _currentChannel = Connection.CreateModel();
            if (_configureChannel != null) _configureChannel(_currentChannel);
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
        if (_currentChannel?.IsClosed != true)
        {
            _currentChannel?.Close();
            _currentChannel?.Dispose();
        }
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