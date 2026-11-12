using VisionGeometry;

namespace VisionCanvas.Core.Editing;

public sealed partial class EditorSession
{
    public async Task<PreparedEditorPaste> PreparePasteAsync(RoiClipboard clipboard, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        var image = State.Image ?? throw new InvalidOperationException("이미지를 먼저 열어 주세요.");
        var content = clipboard.Capture();
        var order = _nextOrder;
        var step = CreateDefaultPasteStep();
        var prepared = await PrepareAsync(new EditorOperations.PasteOperation(content, image, step, order), token);
        return new(prepared, clipboard, content, order);
    }

    public void CommitPreparedPaste(PreparedEditorPaste prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        var current = prepared.Clipboard.Capture();
        if (_nextOrder != prepared.Order
            || current.Entries != prepared.Content.Entries
            || current.PasteNumber != prepared.Content.PasteNumber)
        {
            throw new InvalidOperationException("붙여넣기 준비 중 클립보드가 변경되었습니다. 다시 시도하세요.");
        }

        CommitPrepared(prepared.Change);
        prepared.Clipboard.Pasted();
    }

    private PointD CreateDefaultPasteStep()
        => new(
            RoiClipboard.DefaultPasteOffsetDip / State.Viewport.Scale,
            RoiClipboard.DefaultPasteOffsetDip / State.Viewport.Scale);
}
