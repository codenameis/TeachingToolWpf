using VisionCanvas.Core.Editing;
using VisionGeometry;

namespace VisionCanvas.Core.Interaction;

/// <summary>개별 포인터 제스처에 필요한 문서 상태 읽기·오버레이 표시·명령 전달 문맥이다. EditorSession을 직접 노출하지 않는다.</summary>
internal interface IGestureContext
{
    IEditorCommandSink Commands { get; }

    DocumentState State { get; }

    EditorTool Tool { get; set; }

    RoiShapeKind DrawShape { get; }

    bool DrawAsGroup { get; }

    MaskShape MaskShape { get; }

    void Show(InteractionOverlay overlay);

    void AddDrawnRoi(RoiGeometry geometry);

    void ReconcileToolWithDocument();
}
