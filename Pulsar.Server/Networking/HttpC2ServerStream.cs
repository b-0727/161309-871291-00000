using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Pulsar.Server.Networking
{
    internal sealed class HttpC2ServerStream : Stream
    {
        private readonly ConcurrentQueue<byte> _incoming = new ConcurrentQueue<byte>();
        private readonly ConcurrentQueue<byte> _outgoing = new ConcurrentQueue<byte>();
        private readonly AutoResetEvent _incomingAvailable = new AutoResetEvent(false);
        private readonly AutoResetEvent _outgoingAvailable = new AutoResetEvent(false);
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly object _disposeLock = new object();

        private bool _disposed;

        public IPEndPoint RemoteEndPoint { get; }

        public HttpC2ServerStream(IPEndPoint remoteEndPoint)
        {
            RemoteEndPoint = remoteEndPoint ?? new IPEndPoint(IPAddress.Loopback, 0);
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
                while (_incoming.TryDequeue(out byte value) && bytesRead < count)
                {
                    buffer[offset + bytesRead] = value;
                    bytesRead++;
                }

                if (bytesRead > 0 || _disposed)
                {
                    break;
                }

                _incomingAvailable.WaitOne(100);
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

            for (int i = 0; i < count; i++)
            {
                _outgoing.Enqueue(buffer[offset + i]);
            }

            _outgoingAvailable.Set();
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

        public byte[] DequeueOutgoing(int timeoutMs, int maxBytes)
        {
            var buffer = new MemoryStream();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            while (buffer.Length == 0 && !_disposed)
            {
                while (_outgoing.TryDequeue(out byte value) && buffer.Length < maxBytes)
                {
                    buffer.WriteByte(value);
                }

                if (buffer.Length > 0 || stopwatch.ElapsedMilliseconds >= timeoutMs)
                {
                    break;
                }

                _outgoingAvailable.WaitOne(50);
            }

            return buffer.ToArray();
        }

        public void EnqueueIncoming(byte[] buffer, int offset, int count)
        {
            ValidateBuffer(buffer, offset, count);
            EnsureNotDisposed();

            for (int i = 0; i < count; i++)
            {
                _incoming.Enqueue(buffer[offset + i]);
            }

            _incomingAvailable.Set();
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
                _incomingAvailable.Set();
                _outgoingAvailable.Set();
                _incomingAvailable.Dispose();
                _outgoingAvailable.Dispose();
                _cts.Dispose();
            }

            base.Dispose(disposing);
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
                throw new ObjectDisposedException(nameof(HttpC2ServerStream));
            }
        }
    }
}
