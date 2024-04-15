using System.Collections.Immutable;

namespace EmqxLearning.MqttListener.Models;

public class IngestionMessage
{
    public string TopicName => "ingestion-exchange";
    public IDictionary<string, object> RawData { get; }
    public IngestionMessage(IDictionary<string, object> payload)
    {
        RawData = payload.ToImmutableDictionary();
    }
}