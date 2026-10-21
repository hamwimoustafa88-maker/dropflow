using System.Text;

namespace DropFlow.AI.Tests;

/// <summary>
/// Writes tiny but genuine ONNX files by hand (protobuf wire format), so the ONNX Runtime plumbing can be tested end to end
/// without downloading any model. The graph is <c>output = Sigmoid(ReduceMean(input, axis 1, keepdims))</c>: bright pixels give a
/// high mask value, dark pixels a low one.
/// </summary>
public static class OnnxTestModel
{
    public static byte[] BrightnessMask(int height, int width, string inputName = "input", string outputName = "mask")
    {
        // NodeProto: ReduceMean(axes=[1], keepdims=1) -> Sigmoid
        var reduce = Message(
            Str(1, inputName), Str(2, "mean"), Str(3, "reduce"), Str(4, "ReduceMean"),
            Bytes(5, Message(Str(1, "axes"), Varint(20, 7), Varint(8, 1))),      // attribute axes: INTS [1]
            Bytes(5, Message(Str(1, "keepdims"), Varint(20, 2), Varint(3, 1)))); // attribute keepdims: INT 1
        var sigmoid = Message(Str(1, "mean"), Str(2, outputName), Str(3, "sigmoid"), Str(4, "Sigmoid"));

        var graph = Message(
            Bytes(1, reduce), Bytes(1, sigmoid), Str(2, "brightness-mask"),
            Bytes(11, ValueInfo(inputName, 1, 3, height, width)),
            Bytes(12, ValueInfo(outputName, 1, 1, height, width)));

        return Message(
            Varint(1, 8),                                        // ir_version 8
            Str(2, "DropFlow tests"),
            Bytes(7, graph),
            Bytes(8, Message(Str(1, ""), Varint(2, 13))));       // opset 13
    }

    /// <summary>
    /// A model that ignores its image input and always answers with the given constant tensors (each through an Identity node).
    /// Used to feed a face detector's decoder exactly the heads a real network would produce.
    /// </summary>
    public static byte[] ConstantOutputs(string inputName, int[] inputDims, params (string Name, int[] Dims, float[] Data)[] outputs)
    {
        var nodes = new List<byte[]>();
        var initializers = new List<byte[]>();
        var graphOutputs = new List<byte[]>();
        foreach (var (name, dims, data) in outputs)
        {
            var constName = name + "_const";
            var raw = new byte[data.Length * 4];
            Buffer.BlockCopy(data, 0, raw, 0, raw.Length);
            initializers.Add(Bytes(5, Message(
                [.. dims.Select(d => Varint(1, d)), Varint(2, 1), Str(8, constName), Bytes(9, raw)])));
            nodes.Add(Bytes(1, Message(Str(1, constName), Str(2, name), Str(3, "id_" + name), Str(4, "Identity"))));
            graphOutputs.Add(Bytes(12, ValueInfo(name, dims)));
        }

        var graph = Message([.. nodes, Str(2, "constants"), .. initializers, Bytes(11, ValueInfo(inputName, inputDims)), .. graphOutputs]);
        return Message(Varint(1, 8), Str(2, "DropFlow tests"), Bytes(7, graph), Bytes(8, Message(Str(1, ""), Varint(2, 13))));
    }

    private static byte[] ValueInfo(string name, params int[] dims)
    {
        var shape = Message(dims.Select(d => Bytes(1, Message(Varint(1, d)))).ToArray());
        var tensorType = Message(Varint(1, 1), Bytes(2, shape));  // elem_type FLOAT, shape
        return Message(Str(1, name), Bytes(2, Message(Bytes(1, tensorType))));
    }

    private static byte[] Message(params byte[][] fields) => fields.SelectMany(f => f).ToArray();

    private static byte[] Varint(int field, long value)
    {
        var bytes = new List<byte>();
        WriteVarint(bytes, (uint)(field << 3));
        WriteVarint(bytes, (ulong)value);
        return bytes.ToArray();
    }

    private static byte[] Bytes(int field, byte[] payload)
    {
        var bytes = new List<byte>();
        WriteVarint(bytes, (uint)((field << 3) | 2));
        WriteVarint(bytes, (ulong)payload.Length);
        bytes.AddRange(payload);
        return bytes.ToArray();
    }

    private static byte[] Str(int field, string value) => Bytes(field, Encoding.UTF8.GetBytes(value));

    private static void WriteVarint(List<byte> bytes, ulong value)
    {
        while (value >= 0x80)
        {
            bytes.Add((byte)(value | 0x80));
            value >>= 7;
        }
        bytes.Add((byte)value);
    }
}
