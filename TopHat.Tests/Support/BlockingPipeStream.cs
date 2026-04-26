using System.IO.Pipelines;

namespace TopHat.Tests.Support;

/// <summary>
/// A read-only <see cref="Stream"/> backed by a <see cref="Pipe"/>. ReadAsync genuinely blocks until
/// <see cref="SignalAsync"/> or <see cref="CompleteAsync"/> is called — unlike MemoryStream, which would
/// make HttpCompletionOption.ResponseHeadersRead and ResponseContentRead behave identically and render the
/// streaming test a no-op.
/// </summary>
internal sealed class BlockingPipeStream : Stream
{
    private readonly Pipe _pipe = new();

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var result = await this._pipe.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (result.Buffer.IsEmpty && result.IsCompleted)
        {
            return 0;
        }

        var toCopy = (int)Math.Min(result.Buffer.Length, buffer.Length);
        var sliced = result.Buffer.Slice(0, toCopy);
        var destination = buffer.Span;
        foreach (var segment in sliced)
        {
            segment.Span.CopyTo(destination);
            destination = destination[segment.Length..];
        }

        this._pipe.Reader.AdvanceTo(result.Buffer.GetPosition(toCopy));
        return toCopy;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return await this.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
    }

    public async Task SignalAsync(byte[] data)
    {
        await this._pipe.Writer.WriteAsync(data).ConfigureAwait(false);
    }

    public async Task CompleteAsync()
    {
        await this._pipe.Writer.CompleteAsync().ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            this._pipe.Writer.Complete();
            this._pipe.Reader.Complete();
        }

        base.Dispose(disposing);
    }
}
