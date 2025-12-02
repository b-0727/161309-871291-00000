using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Pulsar.Common.DNS
{
    public class HostsConverter
    {

        public List<Host> RawHostsToList(string rawHosts, bool server = false)
        {
            List<Host> hostsList = new List<Host>();

            if (string.IsNullOrEmpty(rawHosts)) return hostsList;

            if ((rawHosts.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                 rawHosts.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) &&
                !rawHosts.Contains(";"))
            {
                hostsList.Add(CreateFromUri(rawHosts));
                return hostsList;
            }

            var hosts = rawHosts.Split(';');

            foreach (var host in hosts)
            {
                if (string.IsNullOrEmpty(host)) continue;

                if (Uri.TryCreate(host, UriKind.Absolute, out Uri uri) &&
                    (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                {
                    hostsList.Add(CreateFromUri(host));
                }
                else if (host.Contains(':'))
                {
                    if (ushort.TryParse(host.Split(':').Last(), out ushort port))
                    {
                        hostsList.Add(new Host
                        {
                            Hostname = host.Substring(0, host.LastIndexOf(':')),
                            Port = port
                        });
                    }
                }
                else
                {
                    hostsList.Add(new Host { Hostname = host });
                }
            }

            return hostsList;
        }

        private static Host CreateFromUri(string host)
        {
            if (Uri.TryCreate(host, UriKind.Absolute, out Uri uri))
            {
                return new Host
                {
                    Hostname = host,
                    Port = uri.IsDefaultPort ? (ushort)443 : (ushort)uri.Port,
                    Transport = TransportKind.HttpsLongPoll
                };
            }

            return new Host { Hostname = host };
        }

        public string ListToRawHosts(IList<Host> hosts)
        {
            StringBuilder rawHosts = new StringBuilder();

            foreach (var host in hosts)
                rawHosts.Append(host + ";");

            return rawHosts.ToString();
        }
    }
}
