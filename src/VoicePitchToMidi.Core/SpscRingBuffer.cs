using System.Threading;

namespace VoicePitchToMidi.Core;

/// <summary>
/// Lock-free single-producer, single-consumer ring buffer.
/// Uses power-of-two capacity with bitwise masking for fast modular indexing.
/// Thread-safe between one writer thread and one reader thread via Volatile reads/writes.
/// </summary>
internal sealed class SpscRingBuffer
{
    private readonly float[] _buffer;
    private readonly int _mask;

    private int _head; // written by producer, read by consumer
    private int _tail; // written by consumer, read by producer

    /// <summary>
    /// Number of samples available for reading.
    /// </summary>
    public int Available
    {
        get
        {
            int head = Volatile.Read(ref _head);
            int tail = Volatile.Read(ref _tail);
            return head - tail;
        }
    }

    public int Capacity => _buffer.Length;

    public SpscRingBuffer(int minCapacity)
    {
        // Round up to next power of two
        int capacity = 1;
        while (capacity < minCapacity)
            capacity <<= 1;

        _buffer = new float[capacity];
        _mask = capacity - 1;
    }

    /// <summary>
    /// Write samples into the buffer. If the buffer is full, oldest samples are overwritten.
    /// Called from the producer (audio callback) thread only.
    /// </summary>
    public void Write(ReadOnlySpan<float> data)
    {
        int head = _head;
        int capacity = _buffer.Length;

        foreach (float sample in data)
        {
            _buffer[head & _mask] = sample;
            head++;
        }

        // If we wrote more than capacity, advance tail to discard oldest
        int tail = Volatile.Read(ref _tail);
        if (head - tail > capacity)
        {
            Volatile.Write(ref _tail, head - capacity);
        }

        Volatile.Write(ref _head, head);
    }

    /// <summary>
    /// Copy samples without consuming them (non-destructive read).
    /// Called from the consumer (processing) thread only.
    /// </summary>
    public void Peek(Span<float> destination)
    {
        int tail = _tail;
        int available = Volatile.Read(ref _head) - tail;
        int toPeek = Math.Min(destination.Length, available);

        for (int i = 0; i < toPeek; i++)
        {
            destination[i] = _buffer[(tail + i) & _mask];
        }
    }

    /// <summary>
    /// Advance the read position, discarding samples.
    /// Called from the consumer (processing) thread only.
    /// </summary>
    public void Advance(int count)
    {
        int tail = _tail;
        int available = Volatile.Read(ref _head) - tail;
        int toAdvance = Math.Min(count, available);
        Volatile.Write(ref _tail, tail + toAdvance);
    }

    /// <summary>
    /// Clear the buffer. Not thread-safe — call only when both threads are idle.
    /// </summary>
    public void Clear()
    {
        _head = 0;
        _tail = 0;
    }
}
