using System.Text;

/// <summary>Streams a two-dimensional float32 array to a NumPy .npy file, fixing up the row count on close.</summary>
internal sealed class NpyWriter : IDisposable
{
    private const int HeaderLength = 128;

    private readonly FileStream stream;
    private readonly BinaryWriter writer;
    private readonly int columns;
    private long rows;

    public NpyWriter(string path, int columns)
    {
        this.columns = columns;
        stream = File.Create(path);
        writer = new BinaryWriter(stream);
        WriteHeader();
    }

    public long Rows => rows;

    public void Write(ReadOnlySpan<double> first, ReadOnlySpan<double> second = default)
    {
        if (first.Length + second.Length != columns)
            throw new ArgumentException("Row has the wrong number of columns.");
        foreach (double value in first)
            writer.Write((float)value);
        foreach (double value in second)
            writer.Write((float)value);
        rows++;
    }

    private void WriteHeader()
    {
        string dictionary = $"{{'descr': '<f4', 'fortran_order': False, 'shape': ({rows}, {columns}), }}";
        // Magic (6) + version (2) + length (2) + dictionary, padded with spaces and ending in a newline.
        int padding = HeaderLength - 10 - dictionary.Length - 1;
        if (padding < 0)
            throw new InvalidOperationException("NPY header too long.");
        writer.Write(new byte[] { 0x93, (byte)'N', (byte)'U', (byte)'M', (byte)'P', (byte)'Y', 1, 0 });
        writer.Write((ushort)(HeaderLength - 10));
        writer.Write(Encoding.ASCII.GetBytes(dictionary + new string(' ', padding) + "\n"));
    }

    public void Dispose()
    {
        writer.Flush();
        stream.Position = 0;
        WriteHeader();
        writer.Flush();
        writer.Dispose();
    }
}
