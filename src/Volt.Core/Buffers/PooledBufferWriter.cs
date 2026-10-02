using System.Buffers;

namespace Volt;

/// <summary>
/// Pooled, growable <see cref="IBufferWriter{T}"/> over rented arrays.
/// The render hot path writes UTF-8 straight into pooled buffers — no strings, no streams.
/// </summary>
public sealed class PooledBufferWriter : IBufferWriter<byte>, IDisposable
{
    private const int InitialCapacity = 8 * 1024;
    private const int MaxRetainedCapacity = 1 << 20; // 1 MB

    private byte[] _buffer;
    private int _written;

    public PooledBufferWriter()
    {
        _buffer = ArrayPool<byte>.Shared.Rent(InitialCapacity);
    }

    public int WrittenCount => _written;

    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);

    public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);

    /// <summary>Keeps the rented buffer (if reasonably small) and resets the write position.</summary>
    public void Reset()
    {
        if (_buffer.Length > MaxRetainedCapacity)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = ArrayPool<byte>.Shared.Rent(InitialCapacity);
        }
        _written = 0;
    }

    public void Dispose()
    {
        if (_buffer is { Length: > 0 })
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = Array.Empty<byte>();
            _written = 0;
        }
    }

    private void EnsureCapacity(int sizeHint)
    {
        if (sizeHint <= 0) sizeHint = 1;
        long need = (long)_written + sizeHint;
        if (need <= _buffer.Length) return;
        long newSize = Math.Max((long)_buffer.Length * 2, need);
        if (newSize > int.MaxValue) throw new InvalidOperationException("Volt: response exceeds 2GB.");
        var newBuffer = ArrayPool<byte>.Shared.Rent((int)newSize);
        _buffer.AsSpan(0, _written).CopyTo(newBuffer);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = newBuffer;
    }

    Memory<byte> IBufferWriter<byte>.GetMemory(int sizeHint)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsMemory(_written);
    }

    Span<byte> IBufferWriter<byte>.GetSpan(int sizeHint)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_written);
    }

    void IBufferWriter<byte>.Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        _written += count;
    }
}
