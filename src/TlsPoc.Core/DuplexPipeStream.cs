using System.Buffers;
using System.IO.Pipelines;

namespace TlsPoc.Core;

/// <summary>
/// Trimmed copy of Kestrel's <c>DuplexPipeStream</c> (src/Shared/ServerInfrastructure).
/// This is the Stream shim that today sits between the socket transport pipe and
/// <see cref="System.Net.Security.SslStream"/>.
/// </summary>
public sealed class DuplexPipeStream(PipeReader input, PipeWriter output) : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        var vt = ReadAsyncInternal(new Memory<byte>(buffer, offset, count), default);
        return vt.IsCompleted ? vt.Result : vt.AsTask().GetAwaiter().GetResult();
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default)
        => ReadAsyncInternal(new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();

    public override ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
        => ReadAsyncInternal(destination, cancellationToken);

    public override void Write(byte[] buffer, int offset, int count)
        => WriteAsync(buffer, offset, count).GetAwaiter().GetResult();

    public override Task WriteAsync(byte[]? buffer, int offset, int count, CancellationToken cancellationToken)
        => output.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default)
    {
        var task = output.WriteAsync(source, cancellationToken);
        return task.IsCompletedSuccessfully ? ValueTask.CompletedTask : Awaited(task);

        static async ValueTask Awaited(ValueTask<FlushResult> t) => await t;
    }

    public override void Flush() => FlushAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override Task FlushAsync(CancellationToken cancellationToken)
        => output.FlushAsync(cancellationToken).AsTask();

    private async ValueTask<int> ReadAsyncInternal(Memory<byte> destination, CancellationToken cancellationToken)
    {
        while (true)
        {
            var result = await input.ReadAsync(cancellationToken);
            var readableBuffer = result.Buffer;
            try
            {
                if (!readableBuffer.IsEmpty)
                {
                    var count = (int)Math.Min(readableBuffer.Length, destination.Length);
                    readableBuffer = readableBuffer.Slice(0, count);
                    readableBuffer.CopyTo(destination.Span);
                    return count;
                }

                if (result.IsCompleted)
                {
                    return 0;
                }
            }
            finally
            {
                input.AdvanceTo(readableBuffer.End, readableBuffer.End);
            }
        }
    }
}
