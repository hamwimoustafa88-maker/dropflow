// SPDX-License-Identifier: GPL-3.0-or-later
namespace DropFlow.Engines.Media.Preview;

/// <summary>
/// A write-only stream that receives FFmpeg's raw mono <c>f32le</c> samples and keeps only the loudest absolute sample of every
/// block, so an hour of audio costs a few hundred kilobytes instead of the whole decode. <see cref="Reduce"/> then makes the bars.
/// </summary>
internal sealed class WaveformSink(int blockSamples = 40) : Stream
{
    private readonly List<float> _blocks = [];
    private readonly byte[] _carry = new byte[4];
    private int _carried;
    private float _current;
    private int _inBlock;

    public int BlockCount => _blocks.Count + (_inBlock > 0 ? 1 : 0);

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> data)
    {
        var i = 0;
        if (_carried > 0)
        {
            while (_carried < 4 && i < data.Length) _carry[_carried++] = data[i++];
            if (_carried < 4) return;
            Add(BitConverter.ToSingle(_carry, 0));
            _carried = 0;
        }
        for (; i + 4 <= data.Length; i += 4) Add(BitConverter.ToSingle(data.Slice(i, 4)));
        while (i < data.Length) _carry[_carried++] = data[i++];
    }

    private void Add(float sample)
    {
        var v = float.IsFinite(sample) ? Math.Abs(sample) : 0f;
        if (v > _current) _current = v;
        if (++_inBlock < blockSamples) return;
        _blocks.Add(_current);
        _current = 0;
        _inBlock = 0;
    }

    /// <summary>Exactly <paramref name="bars"/> values in 0..1: the peak of each equal share of the file.</summary>
    public float[] Reduce(int bars)
    {
        var blocks = new List<float>(_blocks);
        if (_inBlock > 0) blocks.Add(_current);
        var result = new float[Math.Max(0, bars)];
        if (blocks.Count == 0 || bars <= 0) return result;
        for (var b = 0; b < bars; b++)
        {
            var from = (int)((long)b * blocks.Count / bars);
            var to = Math.Max(from + 1, (int)((long)(b + 1) * blocks.Count / bars));
            var peak = 0f;
            for (var k = from; k < to && k < blocks.Count; k++) peak = Math.Max(peak, blocks[k]);
            result[b] = Math.Min(1f, peak);
        }
        return result;
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
