// File: src/VisionAlgorithm/OpenSrc/ToolBlock/PreFilteredToolNodeDecorator.cs
using System;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.ToolBlock;

/// <summary>
/// Zero-allocation GoF Decorator around an <see cref="IToolBlockNode"/> that applies an ImageJ
/// <see cref="RoiPreFilterKind"/> spatial filter to the node's target ROI before executing the inner algorithm.
/// </summary>
public sealed class PreFilteredToolNodeDecorator : IToolBlockNode
{
    private readonly IToolBlockNode _inner;
    private readonly RoiPreFilterKind _filter;
    private readonly int _roiX;
    private readonly int _roiY;
    private readonly int _roiWidth;
    private readonly int _roiHeight;
    private readonly byte[] _preallocatedFrameBuffer;

    public PreFilteredToolNodeDecorator(
        IToolBlockNode inner,
        RoiPreFilterKind filter,
        int imageWidth,
        int imageHeight,
        int imageStride,
        int roiX,
        int roiY,
        int roiWidth,
        int roiHeight)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(imageWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(imageHeight);
        ArgumentOutOfRangeException.ThrowIfLessThan(imageStride, imageWidth);

        _inner = inner;
        _filter = filter;
        _roiX = roiX;
        _roiY = roiY;
        _roiWidth = roiWidth;
        _roiHeight = roiHeight;
        _preallocatedFrameBuffer = filter == RoiPreFilterKind.None
            ? []
            : new byte[imageHeight * imageStride];
    }

    public int NodeId => _inner.NodeId;
    public string NodeName => _inner.NodeName;
    public IToolBlockNode InnerNode => _inner;
    public RoiPreFilterKind Filter => _filter;

    public ReadOnlySpan<ToolBlockTerminalDescriptor> GetInputTerminals()
        => _inner.GetInputTerminals();

    public ReadOnlySpan<ToolBlockTerminalDescriptor> GetOutputTerminals()
        => _inner.GetOutputTerminals();

    public void Execute(ReadOnlyImageView2D<byte> image, VisionWorkspace workspace, ToolBlockContext context)
    {
        if (_filter == RoiPreFilterKind.None)
        {
            _inner.Execute(image, workspace, context);
            return;
        }

        int frameBytes = image.Height * image.Stride;
        if (_preallocatedFrameBuffer.Length >= frameBytes)
        {
            Span<byte> scratch = _preallocatedFrameBuffer.AsSpan(0, frameBytes);
            image.Pixels.Slice(0, frameBytes).CopyTo(scratch);
            RoiImageProcessor.ApplyFilterInPlaceOnRoi(
                image,
                scratch,
                image.Stride,
                _roiX,
                _roiY,
                _roiWidth,
                _roiHeight,
                _filter);

            var filteredView = new ReadOnlyImageView2D<byte>(_preallocatedFrameBuffer, image.Width, image.Height, image.Stride);
            _inner.Execute(filteredView, workspace, context);
            return;
        }

        ReadOnlyImageView2D<byte> fallbackView = RoiImageProcessor.ApplyRoiFilterIfNeeded(
            image,
            _roiX,
            _roiY,
            _roiWidth,
            _roiHeight,
            _filter);
        _inner.Execute(fallbackView, workspace, context);
    }
}
