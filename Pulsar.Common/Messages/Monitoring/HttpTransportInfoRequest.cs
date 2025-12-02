using MessagePack;
using Pulsar.Common.Messages.Other;

namespace Pulsar.Common.Messages.Monitoring
{
    /// <summary>
    /// Requests transport details from the connected client so the server can
    /// understand whether HTTP long-polling or TCP is in use.
    /// </summary>
    [MessagePackObject]
    public class HttpTransportInfoRequest : IMessage
    {
    }
}
