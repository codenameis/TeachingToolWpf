using System.Text.Json;
using PoseMatch.Tdcm;

internal static class ExampleDataset
{
    internal static void Create(string directory)
    {
        if (Directory.Exists(directory))
        {
            throw new IOException("예제는 새 디렉터리에 생성하세요.");
        }

        Directory.CreateDirectory(directory);
        var template = new byte[36 * 36];
        Array.Fill(template, (byte)30);
        for (var y = 5; y < 31; y++)
        {
            for (var x = 5; x < 31; x++)
            {
                if (x < 10 || y > 25 || (x > 22 && y < 12))
                {
                    template[(y * 36) + x] = 230;
                }
            }
        }

        File.WriteAllBytes(Path.Combine(directory, "template.gray"), template);
        var teaching = new Pixels("template.gray", 36, 36, 36);
        Sample[] Make(string split, int count, int seed)
        {
            var random = new Random(seed);
            return Enumerable.Range(0, count).Select(index =>
            {
                const int size = 128;
                var pixels = new byte[size * size];
                for (var i = 0; i < pixels.Length; i++)
                {
                    pixels[i] = (byte)random.Next(25, 36);
                }

                var x = random.Next(35, 93) + .25;
                var y = random.Next(35, 93) + .25;
                var angle = (index % 4 * 90) + (split == "validation" ? 15 : 0);
                var targets = index % 5 == 4 ? Array.Empty<PoseMatchResult>() : [new PoseMatchResult(x, y, 36, 36, angle, 1)];
                if (targets.Length > 0)
                {
                    var cosine = Math.Cos(angle * Math.PI / 180);
                    var sine = Math.Sin(angle * Math.PI / 180);
                    for (var yy = 0; yy < size; yy++)
                    {
                        for (var xx = 0; xx < size; xx++)
                        {
                            var tx = ((xx - x) * cosine) + ((yy - y) * sine) + 17.5;
                            var ty = (-(xx - x) * sine) + ((yy - y) * cosine) + 17.5;
                            if (tx < 0 || ty < 0 || tx >= 35 || ty >= 35)
                            {
                                continue;
                            }

                            var ix = (int)tx;
                            var iy = (int)ty;
                            var fx = tx - ix;
                            var fy = ty - iy;
                            pixels[(yy * size) + xx] = (byte)Math.Round(
                                (((template[(iy * 36) + ix] * (1 - fx)) + (template[(iy * 36) + ix + 1] * fx)) * (1 - fy)) +
                                (((template[((iy + 1) * 36) + ix] * (1 - fx)) + (template[((iy + 1) * 36) + ix + 1] * fx)) * fy));
                        }
                    }
                }
                var file = $"{split}-{index}.gray";
                File.WriteAllBytes(Path.Combine(directory, file), pixels);
                return new Sample($"synthetic-{split}-{index}", new(file, size, size, size), teaching, targets);
            }).ToArray();
        }
        var dataset = new Dataset("C# 학습 동작 검증용 비대칭 도형. 서로 다른 장면/위치/각도. Kensa/8K 현장 정확도의 증거가 아님.", true,
            Make("training", 20, 421), Make("validation", 10, 911));
        File.WriteAllText(Path.Combine(directory, "dataset.json"), JsonSerializer.Serialize(dataset, new JsonSerializerOptions { WriteIndented = true }));
    }
}
