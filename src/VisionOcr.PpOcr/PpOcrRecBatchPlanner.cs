using System;
using System.Buffers;
using System.Collections.Generic;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace VisionOcr.PpOcr;

/// <summary>
/// Plans and executes deterministic multi-ROI / multi-quad SVTR-LCNet batch recognition.
/// Groups crops strictly by identical 32-aligned <c>TargetWidth</c> (<c>MaxBucketWidthSpreadPx = 0</c>) so
/// <c>RecognizeBatch</c> produces bit-equivalent tensor shapes and identical results regardless of batch neighbors or ROI permutation order.
/// </summary>
internal static class PpOcrRecBatchPlanner
{
    private const int ChannelCount = 3;
    private const int CpuL2CacheOptimalBatchSize = 2;
    private const int MaxAllowedBatchSize = 128;

    internal readonly record struct BatchCropDescriptor(
        int OriginalIndex,
        int TargetWidth,
        int ResizedContentWidth);

    public static PpOcrCtcDecoder.DecodedCropLine[] RunSlotAcrossCrops(
        PpOcrSessionHost.RecognizerSlot slot,
        PpOcrExecutionDevice activeDevice,
        IReadOnlyList<PpOcrRoiPatch> crops,
        IReadOnlyList<bool> invertFlags,
        PpOcrRunOptions options)
    {
        int count = crops.Count;
        var results = new PpOcrCtcDecoder.DecodedCropLine[count];
        if (count == 0)
        {
            return results;
        }

        // [Perf Why]: On CPU, SVTR-LCNet intermediate activations at batch=2 (~1.9 MB) stay L2/L3-cache-resident,
        // whereas batch=32 (~31.5 MB) spills to DRAM. Exact TargetWidth bucketing guarantees zero cross-ROI padding drift.
        int deviceBatchCap = activeDevice == PpOcrExecutionDevice.Cpu
            ? Math.Min(options.MaxBatchSize, CpuL2CacheOptimalBatchSize)
            : options.MaxBatchSize;
        int maxBatch = slot.SupportsDynamicBatch
            ? Math.Clamp(deviceBatchCap, 1, MaxAllowedBatchSize)
            : 1;

        var descriptors = new BatchCropDescriptor[count];
        for (int i = 0; i < count; i++)
        {
            PpOcrRoiPatch patch = crops[i];
            PpOcrPreprocessor.ComputeRecWidths(patch.Width, patch.Height, out int targetWidth, out int resizedContentWidth);
            descriptors[i] = new BatchCropDescriptor(i, targetWidth, resizedContentWidth);
        }

        Array.Sort(descriptors, static (a, b) => a.TargetWidth != b.TargetWidth
            ? a.TargetWidth.CompareTo(b.TargetWidth)
            : a.OriginalIndex.CompareTo(b.OriginalIndex));

        int start = 0;
        while (start < count)
        {
            int exactBucketWidth = descriptors[start].TargetWidth;
            int maxEnd = Math.Min(start + maxBatch, count);
            int end = start + 1;
            while (end < maxEnd && descriptors[end].TargetWidth == exactBucketWidth)
            {
                end++;
            }

            int batchSize = end - start;
            int singleItemFloats = ChannelCount * PpOcrRunOptions.OfficialRecImageHeight * exactBucketWidth;
            int batchTotalFloats = checked(batchSize * singleItemFloats);

            float[] rentedBatch = ArrayPool<float>.Shared.Rent(batchTotalFloats);
            try
            {
                for (int b = 0; b < batchSize; b++)
                {
                    BatchCropDescriptor desc = descriptors[start + b];
                    PpOcrRoiPatch patch = crops[desc.OriginalIndex];
                    PpOcrPreprocessor.FillRecBatchSliceInto(
                        patch.AsView(),
                        invertFlags[desc.OriginalIndex],
                        exactBucketWidth,
                        desc.ResizedContentWidth,
                        rentedBatch.AsSpan(b * singleItemFloats, singleItemFloats));
                }

                var dense = new DenseTensor<float>(
                    rentedBatch.AsMemory(0, batchTotalFloats),
                    [batchSize, ChannelCount, PpOcrRunOptions.OfficialRecImageHeight, exactBucketWidth]);
                var inputs = new List<NamedOnnxValue>(1)
                {
                    NamedOnnxValue.CreateFromTensor(slot.InputName, dense),
                };

                using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> onnxOutputs = slot.Session.Run(inputs);
                Tensor<float> output = onnxOutputs[0].AsTensor<float>();
                int timeSteps = output.Dimensions[1];
                int numClasses = output.Dimensions[2];
                ReadOnlySpan<bool> allowedMask = slot.GetAllowedMask(options.AllowedCharacterSet, numClasses);

                for (int b = 0; b < batchSize; b++)
                {
                    BatchCropDescriptor desc = descriptors[start + b];
                    PpOcrRoiPatch patch = crops[desc.OriginalIndex];
                    results[desc.OriginalIndex] = PpOcrCtcDecoder.DecodeBatchItem(
                        slot,
                        output,
                        b,
                        timeSteps,
                        numClasses,
                        exactBucketWidth,
                        desc.ResizedContentWidth,
                        patch.Width,
                        patch.Height,
                        allowedMask);
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(rentedBatch);
            }

            start = end;
        }

        return results;
    }

    public static PpOcrCtcDecoder.DecodedCropLine RunSingleCrop(
        PpOcrSessionHost.RecognizerSlot slot,
        Memory<float> inputTensor,
        int targetWidth,
        int resizedContentWidth,
        int orientedWidth,
        int orientedHeight,
        VisionAlgorithm.OpenSrc.IndustrialOcrOcv.OcrAllowedCharacterSet allowedSet)
    {
        var dense = new DenseTensor<float>(
            inputTensor,
            [1, ChannelCount, PpOcrRunOptions.OfficialRecImageHeight, targetWidth]);
        var inputs = new List<NamedOnnxValue>(1)
        {
            NamedOnnxValue.CreateFromTensor(slot.InputName, dense),
        };

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = slot.Session.Run(inputs);
        Tensor<float> output = results[0].AsTensor<float>();
        int timeSteps = output.Dimensions[1];
        int numClasses = output.Dimensions[2];
        ReadOnlySpan<bool> allowedMask = slot.GetAllowedMask(allowedSet, numClasses);

        return PpOcrCtcDecoder.DecodeBatchItem(
            slot,
            output,
            batchIndex: 0,
            timeSteps,
            numClasses,
            targetWidth,
            resizedContentWidth,
            orientedWidth,
            orientedHeight,
            allowedMask);
    }
}
