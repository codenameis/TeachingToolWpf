using System.Text;
using TorchSharp;
using static TorchSharp.torch;

namespace PoseMatch.Tdcm.TorchSharp;

internal static class TdcmWeights
{
    internal sealed record Entry(string Name, long[] Shape, float[] Values);
    internal static List<Entry> Read(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (Encoding.ASCII.GetString(reader.ReadBytes(8)) != "TDCM0001")
        {
            throw new InvalidDataException("TDCM 가중치 형식이 아닙니다.");
        }

        var count = reader.ReadInt32();
        if (count is < 1 or > 256)
        {
            throw new InvalidDataException("가중치 개수가 잘못되었습니다.");
        }

        var entries = new List<Entry>();
        long total = 0;
        for (var i = 0; i < count; i++)
        {
            var length = reader.ReadInt32();
            if (length is < 1 or > 256)
            {
                throw new InvalidDataException("가중치 이름 길이가 잘못되었습니다.");
            }

            var name = Encoding.UTF8.GetString(reader.ReadBytes(length));
            var rank = reader.ReadInt32();
            if (rank is < 1 or > 4)
            {
                throw new InvalidDataException("가중치 차원이 잘못되었습니다.");
            }

            var shape = new long[rank];
            long size = 1;
            for (var d = 0; d < rank; d++)
            {
                shape[d] = reader.ReadInt64();
                if (shape[d] is < 1 or > 4096)
                {
                    throw new InvalidDataException("가중치 크기가 잘못되었습니다.");
                }

                size = checked(size * shape[d]);
            }
            total += size;
            if (size != reader.ReadInt32() || total > 2_000_000)
            {
                throw new InvalidDataException("가중치 메모리 예산 초과 또는 크기 불일치.");
            }

            var values = new float[(int)size];
            for (var v = 0; v < values.Length; v++)
            {
                values[v] = reader.ReadSingle();
                if (!float.IsFinite(values[v]))
                {
                    throw new InvalidDataException("유한하지 않은 가중치.");
                }
            }
            if (entries.Any(e => e.Name == name))
            {
                throw new InvalidDataException("중복 가중치.");
            }

            entries.Add(new(name, shape, values));
        }
        if (stream.ReadByte() != -1)
        {
            throw new InvalidDataException("가중치 뒤에 알 수 없는 데이터가 있습니다.");
        }

        return entries;
    }

    internal static void Write(Stream stream, IReadOnlyDictionary<string, Tensor> weights)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("TDCM0001"));
        writer.Write(weights.Count);
        using var scope = NewDisposeScope();
        foreach (var (name, value) in weights)
        {
            var bytes = Encoding.UTF8.GetBytes(name);
            writer.Write(bytes.Length);
            writer.Write(bytes);
            writer.Write(value.shape.Length);
            foreach (var dimension in value.shape)
            {
                writer.Write(dimension);
            }

            var data = value.detach().cpu().contiguous().data<float>().ToArray();
            writer.Write(data.Length);
            foreach (var item in data)
            {
                writer.Write(item);
            }
        }
    }
}
