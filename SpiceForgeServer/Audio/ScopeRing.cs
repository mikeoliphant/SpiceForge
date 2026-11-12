using System.Buffers.Binary;

namespace SpiceForgeServer.Audio;

/// <summary>
/// Scope samples on their way from the audio thread to the socket. The audio thread writes each block's signals into a
/// preallocated ring, without allocating or locking; a monitor thread drains it into binary frames (WebSocketApi.md,
/// Binary frames). Single producer, single consumer. When the consumer falls behind and the ring is full, blocks are
/// dropped, and the frames' sample indices show the gap.
/// </summary>
public sealed class ScopeRing
{
    /// <summary>The longest frame drained, in samples per signal, so a consumer that fell behind doesn't send one huge
    /// frame.</summary>
    public const int MaxFrameSamples = 16384;

    private struct Block
    {
        public long First;      // The sample index of the block's first sample.
        public int Frames;
        public int[] Streams;   // The stream number of each signal, in the order written. Blocks that share this array
                                // (by reference) and follow on from each other are drained into one frame.
        public long Offset;     // Where the block's samples start in the ring: signal by signal, Frames each.
    }

    private readonly float[] data;
    private readonly int dataMask;
    private readonly Block[] blocks;
    private readonly int blockMask;
    private long dataHead, blockHead;   // Written by the producer.
    private long dataTail, blockTail;   // Written by the consumer.

    // The block being written.
    private long pendingOffset;
    private int pendingFrames, pendingSignals;
    private bool pending;

    public ScopeRing(int sampleCapacityLog2 = 20, int blockCapacityLog2 = 10)
    {
        data = new float[1 << sampleCapacityLog2];
        dataMask = data.Length - 1;
        blocks = new Block[1 << blockCapacityLog2];
        blockMask = blocks.Length - 1;
    }

    // ---- The producer (the audio thread) ----

    /// <summary>Starts a block of <paramref name="signals"/> signals of <paramref name="frames"/> samples each. Returns
    /// false, and the block is dropped, if there's no room.</summary>
    public bool Begin(int signals, int frames)
    {
        long size = (long)signals * frames;
        pending = size <= data.Length
            && dataHead + size - Volatile.Read(ref dataTail) <= data.Length
            && blockHead - Volatile.Read(ref blockTail) < blocks.Length;
        pendingOffset = dataHead;
        pendingFrames = frames;
        pendingSignals = signals;
        return pending;
    }

    public void Put(int signal, int n, float value) => data[(pendingOffset + (long)signal * pendingFrames + n) & dataMask] = value;

    public void Commit(long first, int[] streams)
    {
        if (!pending)
            return;
        pending = false;
        blocks[blockHead & blockMask] = new Block { First = first, Frames = pendingFrames, Streams = streams, Offset = pendingOffset };
        dataHead += (long)pendingSignals * pendingFrames;
        // Publishes the samples and the block together.
        Volatile.Write(ref blockHead, blockHead + 1);
    }

    // ---- The consumer (the monitor thread) ----

    /// <summary>Drops everything written so far.</summary>
    public void Clear()
    {
        long head = Volatile.Read(ref blockHead);
        if (head == blockTail)
            return;
        Block last = blocks[(head - 1) & blockMask];
        Volatile.Write(ref dataTail, last.Offset + (long)last.Streams.Length * last.Frames);
        Volatile.Write(ref blockTail, head);
    }

    /// <summary>Drains everything written so far into frames, from <paramref name="source"/> (a simulation's number, or
    /// 0 for the audio test).</summary>
    public List<byte[]> Drain(uint source, float sampleRate)
    {
        var frames = new List<byte[]>();
        long head = Volatile.Read(ref blockHead);
        while (blockTail < head)
        {
            // A run of blocks with the same signals, each following on from the last.
            Block first = blocks[blockTail & blockMask];
            int count = 1, samples = first.Frames;
            while (blockTail + count < head)
            {
                Block next = blocks[(blockTail + count) & blockMask];
                Block previous = blocks[(blockTail + count - 1) & blockMask];
                if (!ReferenceEquals(next.Streams, first.Streams) || next.First != previous.First + previous.Frames
                    || samples + next.Frames > MaxFrameSamples)
                    break;
                samples += next.Frames;
                count++;
            }
            frames.Add(Frame(source, sampleRate, first, count, samples));
            Block last = blocks[(blockTail + count - 1) & blockMask];
            Volatile.Write(ref dataTail, last.Offset + (long)last.Streams.Length * last.Frames);
            Volatile.Write(ref blockTail, blockTail + count);
        }
        return frames;
    }

    private byte[] Frame(uint source, float sampleRate, Block first, int count, int samples)
    {
        int[] streams = first.Streams;
        var frame = new byte[24 + streams.Length * (8 + 4 * samples)];
        Span<byte> span = frame;
        span[0] = 1;
        span[1] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], source);
        BinaryPrimitives.WriteUInt64LittleEndian(span[8..], (ulong)first.First);
        BinaryPrimitives.WriteSingleLittleEndian(span[16..], sampleRate);
        BinaryPrimitives.WriteUInt16LittleEndian(span[20..], (ushort)streams.Length);
        int at = 24;
        for (int s = 0; s < streams.Length; s++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span[at..], (uint)streams[s]);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(at + 4)..], (uint)samples);
            at += 8;
            for (int k = 0; k < count; k++)
            {
                Block b = blocks[(blockTail + k) & blockMask];
                long start = b.Offset + (long)s * b.Frames;
                for (int n = 0; n < b.Frames; n++)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(span[at..], data[(start + n) & dataMask]);
                    at += 4;
                }
            }
        }
        return frame;
    }
}

/// <summary>Lock-free helpers for values the audio thread accumulates and a monitor thread takes.</summary>
public static class Accumulate
{
    /// <summary>Raises <paramref name="target"/> to <paramref name="value"/> if it's larger.</summary>
    public static void Max(ref double target, double value)
    {
        double current = Volatile.Read(ref target);
        while (value > current)
        {
            double seen = Interlocked.CompareExchange(ref target, value, current);
            if (seen == current)
                return;
            current = seen;
        }
    }
}
