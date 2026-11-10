// File: samples/ToolBlock.Simulator/Models/LoadedImageFrameItem.cs
using System;
using System.Collections.Generic;
using System.IO;
using VisionImaging.Abstractions;

namespace ToolBlock.Simulator.Models;

/// <summary>
/// Represents an 8-bit grayscale slice in the ImageJ + ToolBlock Simulator Stack.
/// Supports both zero-full-allocation disk tile streaming (<see cref="IsDiskTiled"/> = <c>true</c>,
/// e.g., <c>ken32.png</c> at <c>17,280 × 30,720</c> = 530 MP) and in-memory mutable buffers
/// (<see cref="OriginalPixels"/> / <see cref="WorkingPixels"/>) with per-slice Undo/Redo history.
/// </summary>
public sealed class LoadedImageFrameItem
{
    private const int MaxUndoSteps = 16;

    private readonly byte[]? _originalPixels;
    private readonly byte[]? _workingPixels;
    private readonly Stack<byte[]> _undoStack = new(MaxUndoSteps);
    private readonly Stack<byte[]> _redoStack = new(MaxUndoSteps);

    public LoadedImageFrameItem(
        int frameIndex,
        string fileName,
        string fullPath,
        int width,
        int height,
        int stride,
        ReadOnlySpan<byte> initialPixels)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentOutOfRangeException.ThrowIfNegative(frameIndex);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width);

        int byteCount = checked(height * stride);
        if (initialPixels.Length < byteCount)
        {
            throw new ArgumentException("Initial pixel buffer is smaller than Height * Stride.", nameof(initialPixels));
        }

        FrameIndex = frameIndex;
        FileName = fileName;
        FullPath = fullPath ?? string.Empty;
        Width = width;
        Height = height;
        Stride = stride;
        IsDiskTiled = false;
        DiskVersion = string.Empty;

        _originalPixels = new byte[byteCount];
        _workingPixels = new byte[byteCount];
        initialPixels.Slice(0, byteCount).CopyTo(_originalPixels);
        initialPixels.Slice(0, byteCount).CopyTo(_workingPixels);
    }

    private LoadedImageFrameItem(
        int frameIndex,
        string fileName,
        string fullPath,
        string diskVersion,
        int width,
        int height)
    {
        FrameIndex = frameIndex;
        FileName = fileName;
        FullPath = fullPath;
        DiskVersion = diskVersion;
        Width = Math.Max(0, width);
        Height = Math.Max(0, height);
        Stride = Width;
        IsDiskTiled = true;
    }

    public static LoadedImageFrameItem CreateDiskTiled(int frameIndex, string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentOutOfRangeException.ThrowIfNegative(frameIndex);

        return new LoadedImageFrameItem(
            frameIndex,
            Path.GetFileName(filePath),
            filePath,
            ImageFileVersion.Read(filePath),
            width: 0,
            height: 0);
    }

    public int FrameIndex { get; }
    public string FileName { get; }
    public string FullPath { get; }
    public string DiskVersion { get; }
    public bool IsDiskTiled { get; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Stride { get; private set; }

    public byte[] OriginalPixels => _originalPixels ?? [];
    public byte[] WorkingPixels => _workingPixels ?? [];

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;
    public int UndoDepth => _undoStack.Count;
    public int RedoDepth => _redoStack.Count;

    public string DisplayTitle => Width > 0 && Height > 0
        ? $"{FrameIndex + 1}. {FileName} ({Width:N0}×{Height:N0} px)"
        : $"{FrameIndex + 1}. {FileName}";

    public void UpdateResolvedDimensions(int width, int height)
    {
        if (width > 0 && height > 0)
        {
            Width = width;
            Height = height;
            if (IsDiskTiled)
            {
                Stride = width;
            }
        }
    }

    public void CommitPixelModification(ReadOnlySpan<byte> nextPixels)
    {
        if (_workingPixels is null)
        {
            return;
        }

        int byteCount = _workingPixels.Length;
        var snapshot = new byte[byteCount];
        _workingPixels.AsSpan().CopyTo(snapshot);
        _undoStack.Push(snapshot);
        _redoStack.Clear();

        nextPixels.Slice(0, byteCount).CopyTo(_workingPixels);
    }

    public bool TryUndo()
    {
        if (_workingPixels is null || _undoStack.Count == 0)
        {
            return false;
        }

        var redoSnapshot = new byte[_workingPixels.Length];
        _workingPixels.AsSpan().CopyTo(redoSnapshot);
        _redoStack.Push(redoSnapshot);

        byte[] prev = _undoStack.Pop();
        prev.AsSpan().CopyTo(_workingPixels);
        return true;
    }

    public bool TryRedo()
    {
        if (_workingPixels is null || _redoStack.Count == 0)
        {
            return false;
        }

        var undoSnapshot = new byte[_workingPixels.Length];
        _workingPixels.AsSpan().CopyTo(undoSnapshot);
        _undoStack.Push(undoSnapshot);

        byte[] next = _redoStack.Pop();
        next.AsSpan().CopyTo(_workingPixels);
        return true;
    }

    public void RevertToOriginal()
    {
        if (_originalPixels is not null)
        {
            CommitPixelModification(_originalPixels);
        }
    }
}
