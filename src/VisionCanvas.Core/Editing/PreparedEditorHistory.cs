namespace VisionCanvas.Core.Editing;

public sealed class PreparedEditorHistory
{
    internal EditorSession Owner { get; }

    internal long Revision { get; }

    internal PreparedHistoryStep Step { get; }

    public DocumentState State => Step.Target;

    internal PreparedEditorHistory(EditorSession owner, long revision, PreparedHistoryStep step)
    {
        Owner = owner;
        Revision = revision;
        Step = step;
    }
}
