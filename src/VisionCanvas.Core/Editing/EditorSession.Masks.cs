using System.Collections.Immutable;
using VisionGeometry;

namespace VisionCanvas.Core.Editing;

public sealed partial class EditorSession
{
    public const int MaximumMasksPerRoi = 64;

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

    public void ClearMasks(Guid id) => SetMasks(id, [], "마스크 모두 삭제");

    private void SetMasks(Guid id, ImmutableArray<MaskRegion> masks, string name)
    {
        if (IsPreviewing || State.IsGroupRoi(id))
        {
            throw new InvalidOperationException("편집 중이 아닌 검사 ROI 하나를 선택하세요.");
        }
        if (masks.Length > MaximumMasksPerRoi)
        {
            throw new InvalidOperationException($"ROI 하나에 마스크는 최대 {MaximumMasksPerRoi}개입니다.");
        }
        var roi = State.Rois[id];
        if (roi.Masks.SequenceEqual(masks))
        {
            return;
        }
        Commit(name, State.WithRois(State.Rois.SetItem(id, roi with
        {
            Masks = masks
        }), [id]));
    }
}
