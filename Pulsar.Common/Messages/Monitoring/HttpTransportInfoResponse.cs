using MessagePack;
using Pulsar.Common.DNS;
using Pulsar.Common.Messages.Other;

namespace Pulsar.Common.Messages.Monitoring
{
    /// <summary>
    /// Reports the active transport mode used by the client.
    /// </summary>
    [MessagePackObject]
    public class HttpTransportInfoResponse : IMessage
    {
        [Key(1)]
        public TransportKind Transport { get; set; }

        [Key(2)]
        public string Endpoint { get; set; }

        [Key(3)]
        public bool Connected { get; set; }
    }
}
