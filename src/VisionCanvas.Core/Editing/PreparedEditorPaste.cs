namespace VisionCanvas.Core.Editing;

public sealed class PreparedEditorPaste
{
    internal PreparedEditorChange Change { get; }

    internal RoiClipboard Clipboard { get; }

    internal RoiClipboardContent Content { get; }

    internal long Order { get; }

    public DocumentState State => Change.State;

    internal PreparedEditorPaste(PreparedEditorChange change, RoiClipboard clipboard, RoiClipboardContent content, long order)
    {
        Change = change;
        Clipboard = clipboard;
        Content = content;
        Order = order;
    }
}
