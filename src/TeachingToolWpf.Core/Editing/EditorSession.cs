using System.Collections.Immutable;
using TeachingToolWpf.Core.Geometry;

namespace TeachingToolWpf.Core.Editing;

/// <summary>Application controller: validates and commits edits; geometry/history own their rules.</summary>
public sealed class EditorSession
{
    private readonly EditHistory history = new();
    private DocumentState? previewStart;
    private long nextOrder;
    public DocumentState State { get; private set; } = DocumentState.Empty;
    public event EventHandler? Changed;
    public bool CanUndo => history.CanUndo && previewStart is null;
    public bool CanRedo => history.CanRedo && previewStart is null;
    public string? UndoName => history.UndoName;
    public string? RedoName => history.RedoName;
    public long HistoryBytes => history.Bytes;
    public bool IsPreviewing => previewStart is not null;

    public Guid Add(RotatedRect geometry)
    {
        geometry = RoiImageBounds.ClipNew(geometry, State.Image);
        var roi = new Roi(Guid.NewGuid(), $"ROI {++nextOrder:000}", geometry, null, false, nextOrder);
        Commit("ROI 추가", State with { Rois = State.Rois.Add(roi.Id, roi), Selection = [roi.Id] });
        return roi.Id;
    }
    public void AddRange(IEnumerable<RotatedRect> geometries)
    {
        var rois = State.Rois.ToBuilder();
        foreach (var geometry in geometries)
        {
            var roi = new Roi(Guid.NewGuid(), $"ROI {++nextOrder:000}", RoiImageBounds.ClipNew(geometry, State.Image), null, false, nextOrder);
            rois.Add(roi.Id, roi);
        }
        Commit("ROI 일괄 추가", State with { Rois = rois.ToImmutable() });
    }
    public void Select(IEnumerable<Guid> ids)
    {
        var selected = ids.Where(State.Rois.ContainsKey).ToImmutableHashSet();
        if (!State.Selection.SetEquals(selected)) Publish(State with { Selection = selected });
    }
    public void Paste(RoiClipboard clipboard)
    {
        if (!clipboard.HasContent || State.Image is null) return;
        var templates = clipboard.Place(State.Image, new(20 / State.Viewport.Scale, 20 / State.Viewport.Scale));
        var ids = templates.ToDictionary(r => r.Id, _ => Guid.NewGuid());
        var rois = State.Rois.ToBuilder();
        var roots = ImmutableHashSet.CreateBuilder<Guid>();
        foreach (var template in templates)
        {
            var id = ids[template.Id];
            if (template.ParentId is not { } parent || !ids.ContainsKey(parent)) roots.Add(id);
            rois.Add(id, template with { Id = id, ParentId = null, Order = ++nextOrder,
                Name = template.Name[..Math.Min(template.Name.Length, 253)] + " 복사" });
        }
        Commit("ROI 붙여넣기", State with { Rois = rois.ToImmutable(), Selection = roots.ToImmutable() });
        clipboard.Pasted();
    }
    public void SetViewport(ViewportTransform view) => Publish(State with { Viewport = view });
    public void SetImage(ImageReference image, ViewportTransform view) => Commit("이미지 교체", State with
    { Image = image, Viewport = view, Rois = ImmutableDictionary<Guid, Roi>.Empty, Selection = [] }, rebuild: false, replacesImage: true);
    public void InitializeImage(ImageReference image, ViewportTransform view)
    {
        if (State.Image is not null || State.Rois.Count != 0) throw new InvalidOperationException("이미지 초기화는 빈 문서에서만 가능합니다.");
        Publish(State with { Image = image, Viewport = view });
    }
    public void SetProperties(Guid id, string name, RotatedRect geometry)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 256) throw new ArgumentException("이름은 1–256자여야 합니다.");
        var next = RoiTransforms.Apply(State, [id], _ => geometry);
        Commit("ROI 속성 변경", next with { Rois = next.Rois.SetItem(id, next.Rois[id] with { Name = name.Trim() }) });
    }
    public void Rename(Guid id, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 256) throw new ArgumentException("이름은 1–256자여야 합니다.", nameof(name));
        Commit("이름 변경", State with { Rois = State.Rois.SetItem(id, State.Rois[id] with { Name = name.Trim() }) }, rebuild: false);
    }
    public void DeleteSelection()
    {
        var ids = RoiHierarchy.Subtree(State, RoiHierarchy.EffectiveRoots(State));
        Commit("선택 ROI 삭제", State with { Rois = State.Rois.RemoveRange(ids), Selection = State.Selection.Except(ids) });
    }
    public void SetFilters(FilterSettings settings)
    {
        settings.Validate();
        var rois = State.Rois.ToBuilder();
        foreach (var id in State.Selection) rois[id] = rois[id] with { Filter = settings };
        Commit("ROI 필터 등록", State with { Rois = rois.ToImmutable() }, rebuild: false);
    }
    public void TranslateSelection(PointD delta) => Transform("ROI 이동", r => r.Geometry.Translate(delta));
    public void RotateSelection(double delta, PointD pivot) => Transform("ROI 회전", r => r.Geometry.RotateAround(pivot, delta));
    public void ResetAngles() => Commit("0°로 초기화", RoiTransforms.ResetAngles(State));
    public void SetGeometry(Guid id, RotatedRect geometry) => Commit("ROI 속성 변경", RoiTransforms.Apply(State, [id], _ => geometry));
    private void Transform(string name, Func<Roi, RotatedRect> transform) => Commit(name, RoiTransforms.Apply(State, RoiHierarchy.EffectiveRoots(State), transform));

    public Guid GroupSelection()
    {
        var roots = RoiHierarchy.EffectiveRoots(State).ToArray();
        if (roots.Length < 2) throw new InvalidOperationException("독립적인 ROI를 2개 이상 선택하세요.");
        var bounds = BoundsD.FromPoints(roots.SelectMany(id => State.Rois[id].Geometry.Corners));
        var padding = Math.Max(1, Math.Max(Math.Abs(bounds.Center.X), Math.Abs(bounds.Center.Y)) * 1e-10);
        var group = new Roi(Guid.NewGuid(), $"그룹 {++nextOrder:000}",
            RoiImageBounds.ClipNew(new(bounds.Center, bounds.Width + padding * 2, bounds.Height + padding * 2, 0), State.Image), null, true, nextOrder);
        var next = State with { Rois = RoiHierarchy.Rebuild(State.Rois.Add(group.Id, group)), Selection = [group.Id] };
        if (roots.Any(id => !RoiHierarchy.IsDescendant(next, id, group.Id)))
            throw new InvalidOperationException("기존 부모와 충돌합니다. 해당 부모 ROI까지 선택한 뒤 그룹화하세요.");
        Commit("부모 ROI로 그룹화", next, rebuild: false);
        return group.Id;
    }
    public void UngroupSelection()
    {
        var groups = State.Selection.Where(id => State.Rois[id].IsGroup).ToHashSet();
        var children = State.Rois.Values.Where(r => r.ParentId is { } id && groups.Contains(id)).Select(r => r.Id).ToImmutableHashSet();
        Commit("그룹 해제", State with { Rois = State.Rois.RemoveRange(groups), Selection = children.Except(groups) });
    }
    public DocumentState PreviewUndo() => history.PreviewUndo(State);
    public DocumentState PreviewRedo() => history.PreviewRedo(State);
    public void Undo() { if (CanUndo) Publish(history.Undo(State)); }
    public void Redo() { if (CanRedo) Publish(history.Redo(State)); }

    public void BeginPreview() { CancelPreview(); previewStart = State; }
    public void PreviewTransform(Func<Roi, RotatedRect> transform)
    {
        if (previewStart is null) throw new InvalidOperationException("편집 preview가 시작되지 않았습니다.");
        Publish(RoiTransforms.Apply(previewStart, RoiHierarchy.EffectiveRoots(previewStart), transform));
    }
    public void PreviewGeometry(Guid id, RotatedRect geometry)
    {
        if (previewStart is null) throw new InvalidOperationException("편집 preview가 시작되지 않았습니다.");
        Publish(RoiTransforms.Apply(previewStart, [id], _ => geometry));
    }
    public void PreviewResize(Guid id, RotatedRect geometry)
    {
        if (previewStart is null) throw new InvalidOperationException("편집 preview가 시작되지 않았습니다.");
        // Children keep their size and parent-relative position, including the parent's center shift.
        Publish(RoiTransforms.Apply(previewStart, [id], _ => geometry));
    }
    public void CommitPreview(string name)
    {
        if (previewStart is null) return;
        var before = previewStart;
        var next = State;
        previewStart = null;
        State = before;
        try { Commit(name, next); }
        catch { Publish(before); throw; }
    }
    public void CancelPreview()
    {
        if (previewStart is not { } before) return;
        previewStart = null;
        Publish(before);
    }
    private void Commit(string name, DocumentState next, bool rebuild = true, bool replacesImage = false)
    {
        if (previewStart is not null) throw new InvalidOperationException("진행 중인 편집을 먼저 완료하세요.");
        if (!replacesImage && ReferenceEquals(State.Rois, next.Rois) && State.Image == next.Image) return;
        if (rebuild) next = next with { Rois = RoiHierarchy.Rebuild(next.Rois) };
        var edit = new DocumentEdit(name, State, next, replacesImage);
        if (edit.IsEmpty) return;
        history.Push(edit);
        Publish(next);
    }
    private void Publish(DocumentState next) { if (next == State) return; State = next; Changed?.Invoke(this, EventArgs.Empty); }
}
