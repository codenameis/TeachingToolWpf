// File: src/TeachingToolWpf.Presentation/MassiveModels/MassiveRoiModelSerializer.cs
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.Models;
using TeachingToolWpf.ViewModels;
using VisionGeometry;

namespace TeachingToolWpf.MassiveModels;

/// <summary>5종의 1억 개 랜덤 ROI 모델(.vmodel)을 디스크에 생성하고, 사전 캐싱 없이 파일 탐색기 경로에서 초고속으로 로드한다.</summary>
public sealed class MassiveRoiModelSerializer
{
    public const int PresetModelCount = 5; // [Domain Why]: User requested 5 distinct 100-million ROI models on disk.
    public const int DefaultImageDimension = 8192; // [Domain Why]: Matches the 8K (8192x8192) studio canvas coordinate space.
    public const int DefaultSpatialSectorCount = 4096; // [Perf Why]: 4,096 spatial sectors (96 KB binary table) fit in L2 cache and read from SSD in < 1 ms while driving 100M distinct coordinates.
    public const int DefaultCanvasAnchorCount = 2048; // [Perf Why]: 2,048 materialized anchor ROIs stored in the file render the full 8K spatial distribution immediately at 60 FPS with < 8 ms load time.
    public const string FileExtension = ".vmodel"; // [Domain Why]: Dedicated file extension for Vision Massive ROI Model files in Windows File Explorer.
    public const string DefaultFolderName = "Models100M"; // [Domain Why]: Dedicated directory storing the 5 100M random ROI model files.

    private const int HeaderFixedByteSize = 128; // [Perf Why]: Fixed 128-byte binary header aligned to cache line boundary.
    private const int AnchorRecordByteSize = 48; // [Perf Why]: Fixed 48-byte materialized anchor ROI record on disk.
    private const int ModelNameMaxBytes = 64; // [Domain Why]: UTF-8 fixed buffer capacity for model display name inside the 128-byte header.
    private const int FileStreamBufferSize = 65536; // [Perf Why]: 64 KB sequential OS read/write buffer avoids small syscall overhead.
    private const ulong BaseSeedModel1 = 0xA17C_9042_551E_8D01uL; // [Domain Why]: Distinct cryptographic seed for Model 1 (Uniform Full Scatter).
    private const ulong BaseSeedModel2 = 0xB28D_A153_662F_9E02uL; // [Domain Why]: Distinct cryptographic seed for Model 2 (Multi-Cluster Wafer).
    private const ulong BaseSeedModel3 = 0xC39E_B264_7730_AF03uL; // [Domain Why]: Distinct cryptographic seed for Model 3 (Concentric Ring Array).
    private const ulong BaseSeedModel4 = 0xD4AF_C375_8841_B004uL; // [Domain Why]: Distinct cryptographic seed for Model 4 (Diagonal Spiral Wave).
    private const ulong BaseSeedModel5 = 0xE5B0_D486_9952_C105uL; // [Domain Why]: Distinct cryptographic seed for Model 5 (Stratified Checker Jitter).
    private const ulong SectorSeedStepA = 0x9E37_79B9_7F4A_7C15uL; // [Math Why]: Golden ratio Weyl step for sector seed generation.
    private const ulong SectorSeedStepB = 0xBF58_476D_1CE4_E5B9uL; // [Math Why]: Secondary mixing constant for orthogonal sector coordinate seed.
    private const double TwoPi = Math.PI * 2.0; // [Math Why]: Full radian period for polar/ring/spiral topologies.
    private const double HalfFactor = 0.5; // [Math Why]: Midpoint multiplier for center coordinate computation.
    private const double DoubleMarginFactor = 2.0; // [Math Why]: Subtracts both left/right or top/bottom margins from image span.
    private const double SectorBorderMargin = 180.0; // [Domain Why]: Keeps sector centers away from outer image edges so spread radius stays in bounds.
    private const float SpreadRadiusModel1 = 240.0f; // [Domain Why]: Wide uniform scatter radius across 8K field.
    private const float SpreadRadiusModel2 = 95.0f; // [Domain Why]: Tighter die-cluster radius for 16-cluster wafer layout.
    private const float SpreadRadiusModel3 = 82.0f; // [Domain Why]: Radial band spread for concentric ring layout.
    private const float SpreadRadiusModel4 = 105.0f; // [Domain Why]: Spiral wave arm thickness spread.
    private const float SpreadRadiusModel5 = 64.0f; // [Domain Why]: Local cell jitter spread for 64x64 stratified checkerboard.
    private const int ClusterCountModel2 = 16; // [Domain Why]: 16 die/module clusters for Model 2.
    private const int ClusterGridSideModel2 = 4; // [Domain Why]: 4x4 die cluster matrix for Model 2.
    private const double ClusterRadiusRatioModel2 = 0.34; // [Domain Why]: Keeps each die cluster separated from adjacent clusters.
    private const int RingCountModel3 = 8; // [Domain Why]: 8 concentric wafer rings for Model 3.
    private const double RingMaxRadiusRatioModel3 = 0.48; // [Domain Why]: Fits outermost concentric ring within usable 8K area.
    private const double RingRadialJitterDivisor = 2.4; // [Domain Why]: Prevents radial overlap between adjacent concentric rings.
    private const double RingPhaseStepRadians = 0.37; // [Math Why]: Angular phase twist per ring to break axial alignment.
    private const int SpiralArmCountModel4 = 5; // [Domain Why]: 5 diagonal wave/spiral arms for Model 4.
    private const double SpiralMaxRadiusRatioModel4 = 0.47; // [Domain Why]: Outer spiral radius ratio within usable 8K area.
    private const double SpiralInnerOffsetRatio = 0.08; // [Domain Why]: Leaves central hub clear of excessive spiral density.
    private const double SpiralOuterSpanRatio = 0.92; // [Math Why]: 1.0 - 0.08 normalized radial progression span.
    private const double SpiralTurnsCount = 2.25; // [Domain Why]: Number of spiral revolutions from center to edge.
    private const double SpiralAngularJitterSpan = 0.22; // [Domain Why]: Angular jitter across each spiral arm.
    private const int CheckerGridSideModel5 = 64; // [Domain Why]: 64x64 stratified cells for Model 5.
    private const double CheckerCellPaddingRatio = 0.2; // [Domain Why]: Inner cell padding ratio in stratified checkerboard.
    private const double CheckerCellJitterSpan = 0.6; // [Domain Why]: Usable interior fraction (1.0 - 2*0.2) per checkerboard cell.
    private const int AlgorithmByteShift = 24; // [Perf Why]: Bit shift to extract independent byte for sector base algorithm slot.
    private const int ShapeByteShift = 16; // [Perf Why]: Bit shift to extract independent byte for sector shape variant.
    private const int CoordinateRoundingDigits = 2; // [Domain Why]: 0.01 px coordinate rounding precision.
    private const int DimensionRoundingDigits = 1; // [Domain Why]: 0.1 px dimension rounding precision.

    public static string GetDefaultModelsDirectory()
    {
        return Path.Combine(AppContext.BaseDirectory, DefaultFolderName);
    }

    public static string GetPresetModelFilePath(string directoryPath, int modelNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        if ((modelNumber < 1) || (modelNumber > PresetModelCount))
        {
            throw new ArgumentOutOfRangeException(nameof(modelNumber), modelNumber, "모델 번호는 1~5 사이여야 합니다.");
        }

        string slug = modelNumber switch
        {
            1 => "UniformScatter",
            2 => "MultiClusterWafer",
            3 => "ConcentricRing",
            4 => "DiagonalSpiral",
            _ => "StratifiedChecker"
        };

        return Path.Combine(directoryPath, $"Model_100M_Random_{modelNumber:00}_{slug}{FileExtension}");
    }

    public static IReadOnlyList<string> EnsureFivePresetModelsOnDisk(
        string? directoryPath = null,
        int canvasAnchorCount = DefaultCanvasAnchorCount)
    {
        string targetDir = string.IsNullOrWhiteSpace(directoryPath)
            ? GetDefaultModelsDirectory()
            : directoryPath;

        Directory.CreateDirectory(targetDir);
        string[] paths = new string[PresetModelCount];

        for (int modelNumber = 1; modelNumber <= PresetModelCount; modelNumber++)
        {
            string filePath = GetPresetModelFilePath(targetDir, modelNumber);
            paths[modelNumber - 1] = filePath;
            if (!IsValidExistingModelFile(filePath, canvasAnchorCount))
            {
                WritePresetModelFile(filePath, modelNumber, canvasAnchorCount);
            }
        }

        return paths;
    }

    public static void WritePresetModelFile(
        string filePath,
        int modelNumber,
        int canvasAnchorCount = DefaultCanvasAnchorCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if ((modelNumber < 1) || (modelNumber > PresetModelCount))
        {
            throw new ArgumentOutOfRangeException(nameof(modelNumber), modelNumber, "모델 번호는 1~5 사이여야 합니다.");
        }

        if (canvasAnchorCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(canvasAnchorCount), canvasAnchorCount, "캔버스 앵커 개수는 1 이상이어야 합니다.");
        }

        (string modelName, MassiveRoiModelTopology topology, ulong masterSeed) = GetPresetDescriptor(modelNumber);
        MassiveRoiModelHeader header = MassiveRoiModelHeader.Create(
            modelNumber,
            modelName,
            topology,
            MassiveRoiModelHeader.DefaultHundredMillionRoiCount,
            DefaultImageDimension,
            DefaultImageDimension,
            masterSeed,
            DefaultSpatialSectorCount,
            canvasAnchorCount);

        MassiveRoiSectorRecord[] sectors = GenerateTopologySectors(in header);
        int totalBytes = HeaderFixedByteSize
            + (sectors.Length * MassiveRoiSectorRecord.BinaryByteSize)
            + (canvasAnchorCount * AnchorRecordByteSize);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(totalBytes);
        try
        {
            Span<byte> span = buffer.AsSpan(0, totalBytes);
            span.Clear();
            WriteHeader(span[..HeaderFixedByteSize], in header);

            int offset = HeaderFixedByteSize;
            for (int i = 0; i < sectors.Length; i++)
            {
                WriteSectorRecord(span.Slice(offset, MassiveRoiSectorRecord.BinaryByteSize), in sectors[i]);
                offset += MassiveRoiSectorRecord.BinaryByteSize;
            }

            for (int i = 0; i < canvasAnchorCount; i++)
            {
                MassiveRoiEntry entry = MassiveRoiModelFile.ComputeEntryFromSectors(in header, sectors, i);
                WriteAnchorRecord(span.Slice(offset, AnchorRecordByteSize), in entry);
                offset += AnchorRecordByteSize;
            }

            string? parentDir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(parentDir))
            {
                Directory.CreateDirectory(parentDir);
            }

            using FileStream stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read, FileStreamBufferSize, FileOptions.SequentialScan);
            stream.Write(span);
            stream.Flush();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static async Task<MassiveRoiModelFile> LoadFromDiskAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("선택한 1억 ROI 모델 파일(.vmodel)을 찾을 수 없습니다.", filePath);
        }

        long startTimestamp = Stopwatch.GetTimestamp();

        using FileStream stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: FileStreamBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        if (stream.Length < HeaderFixedByteSize)
        {
            throw new InvalidDataException("유효하지 않은 .vmodel 파일입니다 (헤더 크기 미달).");
        }

        int fileLength = checked((int)stream.Length);
        byte[] rawBuffer = ArrayPool<byte>.Shared.Rent(fileLength);
        try
        {
            int bytesReadTotal = 0;
            while (bytesReadTotal < fileLength)
            {
                int read = await stream.ReadAsync(rawBuffer.AsMemory(bytesReadTotal, fileLength - bytesReadTotal), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                bytesReadTotal += read;
            }

            return ParseBuffer(filePath, rawBuffer.AsSpan(0, bytesReadTotal), startTimestamp);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rawBuffer);
        }
    }

    public static MassiveRoiModelFile LoadFromDisk(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("선택한 1억 ROI 모델 파일(.vmodel)을 찾을 수 없습니다.", filePath);
        }

        long startTimestamp = Stopwatch.GetTimestamp();

        using FileStream stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: FileStreamBufferSize,
            FileOptions.SequentialScan);

        if (stream.Length < HeaderFixedByteSize)
        {
            throw new InvalidDataException("유효하지 않은 .vmodel 파일입니다 (헤더 크기 미달).");
        }

        int fileLength = checked((int)stream.Length);
        byte[] rawBuffer = ArrayPool<byte>.Shared.Rent(fileLength);
        try
        {
            stream.ReadExactly(rawBuffer.AsSpan(0, fileLength));
            return ParseBuffer(filePath, rawBuffer.AsSpan(0, fileLength), startTimestamp);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rawBuffer);
        }
    }

    private static MassiveRoiModelFile ParseBuffer(string filePath, ReadOnlySpan<byte> span, long startTimestamp)
    {
        MassiveRoiModelHeader header = ReadHeader(span[..HeaderFixedByteSize]);
        int expectedBytes = HeaderFixedByteSize
            + (header.SpatialSectorCount * MassiveRoiSectorRecord.BinaryByteSize)
            + (header.CanvasAnchorCount * AnchorRecordByteSize);

        if (span.Length < expectedBytes)
        {
            throw new InvalidDataException($"손상된 .vmodel 파일입니다 (필요 바이트: {expectedBytes}, 실제: {span.Length}).");
        }

        MassiveRoiSectorRecord[] sectors = new MassiveRoiSectorRecord[header.SpatialSectorCount];
        int offset = HeaderFixedByteSize;
        for (int i = 0; i < sectors.Length; i++)
        {
            sectors[i] = ReadSectorRecord(span.Slice(offset, MassiveRoiSectorRecord.BinaryByteSize));
            offset += MassiveRoiSectorRecord.BinaryByteSize;
        }

        MassiveRoiEntry[] anchors = new MassiveRoiEntry[header.CanvasAnchorCount];
        for (int i = 0; i < anchors.Length; i++)
        {
            anchors[i] = ReadAnchorRecord(span.Slice(offset, AnchorRecordByteSize), in header, i);
            offset += AnchorRecordByteSize;
        }

        double elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        return new MassiveRoiModelFile(filePath, header, sectors, anchors, elapsedMs);
    }

    private static bool IsValidExistingModelFile(string filePath, int expectedAnchorCount)
    {
        if (!File.Exists(filePath))
        {
            return false;
        }

        int expectedLength = HeaderFixedByteSize
            + (DefaultSpatialSectorCount * MassiveRoiSectorRecord.BinaryByteSize)
            + (expectedAnchorCount * AnchorRecordByteSize);

        FileInfo info = new FileInfo(filePath);
        return info.Length == expectedLength;
    }

    private static (string ModelName, MassiveRoiModelTopology Topology, ulong MasterSeed) GetPresetDescriptor(int modelNumber)
    {
        return modelNumber switch
        {
            1 => ("모델 01 · 1억 ROI 전영역 균일 산포 (Uniform Scatter)", MassiveRoiModelTopology.UniformFullScatter, BaseSeedModel1),
            2 => ("모델 02 · 1억 ROI 16-클러스터 웨이퍼 군집 (Multi-Cluster)", MassiveRoiModelTopology.MultiClusterWafer, BaseSeedModel2),
            3 => ("모델 03 · 1억 ROI 8-동심원 링 배열 (Concentric Rings)", MassiveRoiModelTopology.ConcentricRingArray, BaseSeedModel3),
            4 => ("모델 04 · 1억 ROI 대각 나선 파동 분포 (Spiral Wave)", MassiveRoiModelTopology.DiagonalSpiralWave, BaseSeedModel4),
            _ => ("모델 05 · 1억 ROI 체커보드 층화 지터 (Stratified Jitter)", MassiveRoiModelTopology.StratifiedCheckerJitter, BaseSeedModel5)
        };
    }

    private static MassiveRoiSectorRecord[] GenerateTopologySectors(in MassiveRoiModelHeader header)
    {
        MassiveRoiSectorRecord[] sectors = new MassiveRoiSectorRecord[header.SpatialSectorCount];
        double width = header.ImageWidth;
        double height = header.ImageHeight;
        double centerX = width * HalfFactor;
        double centerY = height * HalfFactor;
        double margin = SectorBorderMargin;
        double usableW = width - (margin * DoubleMarginFactor);
        double usableH = height - (margin * DoubleMarginFactor);

        for (int i = 0; i < sectors.Length; i++)
        {
            ulong s0 = MassiveRoiModelFile.Mix64(header.MasterSeed ^ ((ulong)(i + 1) * SectorSeedStepA));
            ulong s1 = MassiveRoiModelFile.Mix64(s0 + SectorSeedStepB);
            double u = MassiveRoiModelFile.ToUnitDouble(s0);
            double v = MassiveRoiModelFile.ToUnitDouble(s1);

            double anchorX;
            double anchorY;
            float spreadRadius;

            switch (header.Topology)
            {
                case MassiveRoiModelTopology.UniformFullScatter:
                    anchorX = margin + (u * usableW);
                    anchorY = margin + (v * usableH);
                    spreadRadius = SpreadRadiusModel1;
                    break;

                case MassiveRoiModelTopology.MultiClusterWafer:
                {
                    int clusterIdx = i % ClusterCountModel2;
                    int clusterCol = clusterIdx % ClusterGridSideModel2;
                    int clusterRow = clusterIdx / ClusterGridSideModel2;
                    double cellW = usableW / ClusterGridSideModel2;
                    double cellH = usableH / ClusterGridSideModel2;
                    double clusterCenterX = margin + ((clusterCol + HalfFactor) * cellW);
                    double clusterCenterY = margin + ((clusterRow + HalfFactor) * cellH);
                    double localR = Math.Sqrt(u) * (Math.Min(cellW, cellH) * ClusterRadiusRatioModel2);
                    double localTheta = v * TwoPi;
                    anchorX = clusterCenterX + (Math.Cos(localTheta) * localR);
                    anchorY = clusterCenterY + (Math.Sin(localTheta) * localR);
                    spreadRadius = SpreadRadiusModel2;
                    break;
                }

                case MassiveRoiModelTopology.ConcentricRingArray:
                {
                    int ringIdx = (i % RingCountModel3) + 1;
                    double maxRadius = Math.Min(usableW, usableH) * RingMaxRadiusRatioModel3;
                    double ringRadius = (ringIdx / (double)RingCountModel3) * maxRadius;
                    double radialJitter = (u - HalfFactor) * (maxRadius / (RingCountModel3 * RingRadialJitterDivisor));
                    double theta = (v * TwoPi) + (ringIdx * RingPhaseStepRadians);
                    anchorX = centerX + (Math.Cos(theta) * (ringRadius + radialJitter));
                    anchorY = centerY + (Math.Sin(theta) * (ringRadius + radialJitter));
                    spreadRadius = SpreadRadiusModel3;
                    break;
                }

                case MassiveRoiModelTopology.DiagonalSpiralWave:
                {
                    int armIdx = i % SpiralArmCountModel4;
                    double t = i / (double)sectors.Length;
                    double maxR = Math.Min(usableW, usableH) * SpiralMaxRadiusRatioModel4;
                    double r = (SpiralInnerOffsetRatio + (SpiralOuterSpanRatio * Math.Sqrt(t))) * maxR;
                    double angle = (t * TwoPi * SpiralTurnsCount) + (armIdx * (TwoPi / SpiralArmCountModel4)) + ((u - HalfFactor) * SpiralAngularJitterSpan);
                    anchorX = centerX + (Math.Cos(angle) * r);
                    anchorY = centerY + (Math.Sin(angle) * r);
                    spreadRadius = SpreadRadiusModel4;
                    break;
                }

                default:
                {
                    int cellIdx = i % (CheckerGridSideModel5 * CheckerGridSideModel5);
                    int gx = cellIdx % CheckerGridSideModel5;
                    int gy = cellIdx / CheckerGridSideModel5;
                    if (((gx + gy) & 1) == 1)
                    {
                        gx = (gx + 1) % CheckerGridSideModel5;
                    }

                    double stepX = usableW / CheckerGridSideModel5;
                    double stepY = usableH / CheckerGridSideModel5;
                    anchorX = margin + ((gx + CheckerCellPaddingRatio + (u * CheckerCellJitterSpan)) * stepX);
                    anchorY = margin + ((gy + CheckerCellPaddingRatio + (v * CheckerCellJitterSpan)) * stepY);
                    spreadRadius = SpreadRadiusModel5;
                    break;
                }
            }

            byte baseAlg = (byte)((s0 >> AlgorithmByteShift) % MassiveRoiModelFile.TotalAlgorithmCatalogCount);
            byte shapeVariant = (byte)((s1 >> ShapeByteShift) % MassiveRoiModelFile.ShapeKindModulus);
            sectors[i] = MassiveRoiSectorRecord.Create(
                (float)Math.Clamp(anchorX, margin, width - margin),
                (float)Math.Clamp(anchorY, margin, height - margin),
                spreadRadius,
                s0 ^ s1,
                baseAlg,
                shapeVariant);
        }

        return sectors;
    }

    private static void WriteHeader(Span<byte> target, in MassiveRoiModelHeader header)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(target[0..4], MassiveRoiModelHeader.BinaryMagic);
        BinaryPrimitives.WriteInt32LittleEndian(target[4..8], MassiveRoiModelHeader.CurrentFormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(target[8..12], header.ModelNumber);
        target[12] = (byte)header.Topology;
        BinaryPrimitives.WriteInt64LittleEndian(target[16..24], header.TotalRoiCount);
        BinaryPrimitives.WriteInt32LittleEndian(target[24..28], header.ImageWidth);
        BinaryPrimitives.WriteInt32LittleEndian(target[28..32], header.ImageHeight);
        BinaryPrimitives.WriteUInt64LittleEndian(target[32..40], header.MasterSeed);
        BinaryPrimitives.WriteInt32LittleEndian(target[40..44], header.SpatialSectorCount);
        BinaryPrimitives.WriteInt32LittleEndian(target[44..48], header.CanvasAnchorCount);

        byte[] nameBytes = Encoding.UTF8.GetBytes(header.ModelName);
        int copyLen = Math.Min(nameBytes.Length, ModelNameMaxBytes);
        BinaryPrimitives.WriteInt32LittleEndian(target[48..52], copyLen);
        nameBytes.AsSpan(0, copyLen).CopyTo(target.Slice(52, copyLen));
    }

    private static MassiveRoiModelHeader ReadHeader(ReadOnlySpan<byte> source)
    {
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(source[0..4]);
        if (magic != MassiveRoiModelHeader.BinaryMagic)
        {
            throw new InvalidDataException("유효한 1억 ROI 모델 파일(.vmodel) 시그니처가 아닙니다.");
        }

        int version = BinaryPrimitives.ReadInt32LittleEndian(source[4..8]);
        if (version != MassiveRoiModelHeader.CurrentFormatVersion)
        {
            throw new InvalidDataException($"지원하지 않는 .vmodel 버전입니다: {version}");
        }

        int modelNumber = BinaryPrimitives.ReadInt32LittleEndian(source[8..12]);
        MassiveRoiModelTopology topology = (MassiveRoiModelTopology)source[12];
        long totalRoiCount = BinaryPrimitives.ReadInt64LittleEndian(source[16..24]);
        int imageWidth = BinaryPrimitives.ReadInt32LittleEndian(source[24..28]);
        int imageHeight = BinaryPrimitives.ReadInt32LittleEndian(source[28..32]);
        ulong masterSeed = BinaryPrimitives.ReadUInt64LittleEndian(source[32..40]);
        int sectorCount = BinaryPrimitives.ReadInt32LittleEndian(source[40..44]);
        int anchorCount = BinaryPrimitives.ReadInt32LittleEndian(source[44..48]);
        int nameByteLen = Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(source[48..52]), 0, ModelNameMaxBytes);
        string modelName = nameByteLen > 0
            ? Encoding.UTF8.GetString(source.Slice(52, nameByteLen))
            : $"모델 {modelNumber:00}";

        return MassiveRoiModelHeader.Create(
            modelNumber,
            modelName,
            topology,
            totalRoiCount,
            imageWidth,
            imageHeight,
            masterSeed,
            sectorCount,
            anchorCount);
    }

    private static void WriteSectorRecord(Span<byte> target, in MassiveRoiSectorRecord sector)
    {
        BinaryPrimitives.WriteSingleLittleEndian(target[0..4], sector.AnchorX);
        BinaryPrimitives.WriteSingleLittleEndian(target[4..8], sector.AnchorY);
        BinaryPrimitives.WriteSingleLittleEndian(target[8..12], sector.SpreadRadius);
        target[12] = sector.BaseAlgorithmSlot;
        target[13] = sector.ShapeVariant;
        BinaryPrimitives.WriteUInt64LittleEndian(target[16..24], sector.SectorSeed);
    }

    private static MassiveRoiSectorRecord ReadSectorRecord(ReadOnlySpan<byte> source)
    {
        float anchorX = BinaryPrimitives.ReadSingleLittleEndian(source[0..4]);
        float anchorY = BinaryPrimitives.ReadSingleLittleEndian(source[4..8]);
        float spread = BinaryPrimitives.ReadSingleLittleEndian(source[8..12]);
        byte baseAlg = source[12];
        byte shapeVariant = source[13];
        ulong seed = BinaryPrimitives.ReadUInt64LittleEndian(source[16..24]);
        return MassiveRoiSectorRecord.Create(anchorX, anchorY, spread, seed, baseAlg, shapeVariant);
    }

    private static void WriteAnchorRecord(Span<byte> target, in MassiveRoiEntry entry)
    {
        entry.Id.TryWriteBytes(target[0..16]);
        BinaryPrimitives.WriteSingleLittleEndian(target[16..20], (float)entry.Geometry.Center.X);
        BinaryPrimitives.WriteSingleLittleEndian(target[20..24], (float)entry.Geometry.Center.Y);
        BinaryPrimitives.WriteSingleLittleEndian(target[24..28], (float)entry.Geometry.Width);
        BinaryPrimitives.WriteSingleLittleEndian(target[28..32], (float)entry.Geometry.Height);
        BinaryPrimitives.WriteSingleLittleEndian(target[32..36], (float)entry.Geometry.Angle);
        target[36] = (byte)entry.Geometry.Shape.Kind;
        target[37] = (byte)entry.ToolKind;
        target[38] = entry.PatternMethod.HasValue ? (byte)(entry.PatternMethod.Value + 1) : (byte)0;
        target[39] = entry.SuiteAlgorithm.HasValue ? (byte)(entry.SuiteAlgorithm.Value + 1) : (byte)0;
        BinaryPrimitives.WriteInt64LittleEndian(target[40..48], entry.Index);
    }

    private static MassiveRoiEntry ReadAnchorRecord(
        ReadOnlySpan<byte> source,
        in MassiveRoiModelHeader header,
        int fallbackIndex)
    {
        Guid id = new Guid(source[0..16]);
        double cx = Math.Round(BinaryPrimitives.ReadSingleLittleEndian(source[16..20]), CoordinateRoundingDigits);
        double cy = Math.Round(BinaryPrimitives.ReadSingleLittleEndian(source[20..24]), CoordinateRoundingDigits);
        double width = Math.Round(BinaryPrimitives.ReadSingleLittleEndian(source[24..28]), DimensionRoundingDigits);
        double height = Math.Round(BinaryPrimitives.ReadSingleLittleEndian(source[28..32]), DimensionRoundingDigits);
        double angle = Math.Round(BinaryPrimitives.ReadSingleLittleEndian(source[32..36]), DimensionRoundingDigits);
        RoiShapeKind shapeKind = (RoiShapeKind)source[36];
        if (shapeKind == RoiShapeKind.Circle)
        {
            height = width;
            angle = 0.0;
        }

        InspectionToolKind toolKind = (InspectionToolKind)source[37];
        PatternMatchingMethod? patternMethod = source[38] > 0 ? (PatternMatchingMethod)(source[38] - 1) : null;
        InspectionSuiteAlgorithmKind? suiteAlgorithm = source[39] > 0 ? (InspectionSuiteAlgorithmKind)(source[39] - 1) : null;
        long index = BinaryPrimitives.ReadInt64LittleEndian(source[40..48]);
        if (index < 0)
        {
            index = fallbackIndex;
        }

        RoiGeometry geometry = new RoiGeometry(new PointD(cx, cy), width, height, angle, MassiveRoiModelFile.ResolveRoiShape(shapeKind));
        string badge = RoiAlgorithmAssignment.FormatAlgorithmBadge(toolKind, patternMethod, suiteAlgorithm);
        string accent = RoiAlgorithmAssignment.GetAlgorithmAccent(toolKind, patternMethod, suiteAlgorithm);
        string summary = $"{badge} · 좌표({cx:F1}, {cy:F1}) 랜덤 모델 로드됨";
        string name = $"M{header.ModelNumber}-ROI #{index + 1:000,000,000}";

        return MassiveRoiEntry.Create(
            index,
            id,
            name,
            geometry,
            toolKind,
            patternMethod,
            suiteAlgorithm,
            badge,
            accent,
            summary);
    }
}
