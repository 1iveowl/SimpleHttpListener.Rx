namespace SimpleHttpListener.Rx.Tests.TestHelpers;

/// <summary>
/// A read-only stream that serves a fixed payload in configurable chunk sizes, so tests can
/// exercise every split boundary a real network read could produce. When
/// <see cref="HoldOpenAfterPayload"/> is set, the read after the payload parks on a
/// never-completing task (like an idle keep-alive socket) instead of returning EOF.
/// </summary>
internal sealed class DribbleStream(byte[] payload, params int[] chunkSizes) : Stream
{
    private int _position;
    private int _chunkIndex;

    public bool HoldOpenAfterPayload { get; init; }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => payload.Length;
    public override long Position { get => _position; set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position >= payload.Length)
        {
            if (HoldOpenAfterPayload)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return 0;
        }

        var chunkSize = chunkSizes.Length == 0
            ? int.MaxValue
            : chunkSizes[Math.Min(_chunkIndex++, chunkSizes.Length - 1)];

        var count = Math.Min(Math.Min(chunkSize, buffer.Length), payload.Length - _position);
        payload.AsMemory(_position, count).CopyTo(buffer);
        _position += count;
        return count;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
