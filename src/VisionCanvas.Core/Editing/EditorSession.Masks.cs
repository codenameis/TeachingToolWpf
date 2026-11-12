using System.Collections.Immutable;
using VisionGeometry;

namespace VisionCanvas.Core.Editing;

public sealed partial class EditorSession
{
    public const int MaximumMasksPerRoi = 512;

    public void AddMask(Guid id, MaskRegion mask)
    {
        ArgumentNullException.ThrowIfNull(mask);
        SetMasks(id, State.Rois[id].Masks.Add(mask), "마스크 추가");
    }

    public void ReplaceMask(Guid id, int index, MaskRegion mask)
    {
        ArgumentNullException.ThrowIfNull(mask);
        var masks = State.Rois[id].Masks;
        if (masks[index] == mask)
        {
            return;
        }

        SetMasks(id, masks.SetItem(index, mask), "마스크 변경");
    }

    public void RemoveMask(Guid id, int index)
        => SetMasks(id, State.Rois[id].Masks.RemoveAt(index), "마스크 삭제");

    public void ClearMasks(Guid id)
        => SetMasks(id, [], "마스크 모두 삭제");

    public void ReplaceAllMasks(Guid id, ImmutableArray<MaskRegion> masks, string name = "패턴 마스크 일괄 편집")
    {
        if (masks.IsDefault)
        {
            throw new ArgumentNullException(nameof(masks));
        }

        for (var i = 0; i < masks.Length; i++)
        {
            ArgumentNullException.ThrowIfNull(masks[i]);
        }

        SetMasks(id, masks, name);
    }

    private void SetMasks(Guid id, ImmutableArray<MaskRegion> masks, string name)
    {
        ThrowIfPreviewing("편집 중이 아닌 검사 ROI 하나를 선택하세요.");
        Execute(new EditorOperations.SetMasksOperation(id, masks, name));
    }
}
