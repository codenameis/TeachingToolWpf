using TeachingToolWpf.Core.Geometry;

namespace TeachingToolWpf.Core.Editing;

public sealed class PreparedEditorPaste
{
    internal PreparedEditorChange Change
    {
        get;
    }

    internal RoiClipboard Clipboard
    {
        get;
    }

    internal RoiClipboardContent Content
    {
        get;
    }

    internal long Order
    {
        get;
    }

    public DocumentState State => Change.State;

    internal PreparedEditorPaste(PreparedEditorChange change, RoiClipboard clipboard, RoiClipboardContent content, long order)
    {
        Change = change;
        Clipboard = clipboard;
        Content = content;
        Order = order;
    }
}

public sealed partial class EditorSession
{
    public async Task<PreparedEditorPaste> PreparePasteAsync(RoiClipboard clipboard, CancellationToken token = default)
    {
        var image = State.Image ?? throw new InvalidOperationException("이미지를 먼저 열어 주세요.");
        var content = clipboard.Capture();
        var order = _nextOrder;
        var step = new PointD(20 / State.Viewport.Scale, 20 / State.Viewport.Scale);
        var prepared = await PrepareChangeAsync("ROI 붙여넣기", before =>
            RoiPaste.Prepare(before, content.Place(image, step, token), order, token), false, token);
        return new(prepared, clipboard, content, order);
    }

    public void CommitPreparedPaste(PreparedEditorPaste prepared)
    {
        var current = prepared.Clipboard.Capture();
        if (_nextOrder != prepared.Order || current.Entries != prepared.Content.Entries || current.PasteNumber != prepared.Content.PasteNumber)
        {
            throw new InvalidOperationException("붙여넣기 준비 중 클립보드가 변경되었습니다. 다시 시도하세요.");
        }

        CommitPrepared(prepared.Change);
        _nextOrder += prepared.Content.Entries.Length;
        prepared.Clipboard.Pasted();
    }
}
