namespace VisionCanvas.Core.Editing;

/// <summary>Prepared immutable values; only the originating session may publish them once.</summary>
public sealed class PreparedEditorChange
{
    internal bool RestoresViewport { get; }

    internal EditorSession Owner { get; }

    internal long Revision { get; }

    internal long PreviewEpoch { get; }

    internal DocumentEdit? Edit { get; }

    internal (long Before, long After)? OrderChange { get; }

    internal bool Consumed { get; set; }

    public DocumentState State { get; }

    internal DocumentState Before { get; }

    internal PreparedEditorChange(
        EditorSession owner,
        long revision,
        long previewEpoch,
        DocumentState before,
        DocumentState state,
        DocumentEdit? edit,
        (long Before, long After)? orderChange = null,
        bool restoresViewport = false)
    {
        Owner = owner;
        Revision = revision;
        PreviewEpoch = previewEpoch;
        Before = before;
        State = state;
        Edit = edit;
        OrderChange = orderChange;
        RestoresViewport = restoresViewport;
    }
}
