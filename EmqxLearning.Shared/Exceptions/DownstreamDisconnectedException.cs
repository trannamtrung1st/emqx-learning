using System.Runtime.Serialization;

namespace EmqxLearning.Shared.Exceptions;

public class DownstreamDisconnectedException : Exception
{
    public DownstreamDisconnectedException()
    {
    }

    public DownstreamDisconnectedException(string message) : base(message)
    {
    }

    public DownstreamDisconnectedException(string message, Exception innerException) : base(message, innerException)
    {
    }

    protected DownstreamDisconnectedException(SerializationInfo info, StreamingContext context) : base(info, context)
    {
    }
}