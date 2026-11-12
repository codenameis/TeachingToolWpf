using System.Collections.Immutable;
using VisionCanvas.Core.Geometry;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Core.Editing;

internal static class EditorOperations
{
    internal sealed record AddRoiOperation(
        Guid Id,
        RoiGeometry Geometry,
        Guid? ParentId,
        bool IsGroup,
        long CurrentOrder) : IEditorOperation
    {
        public string Name => IsGroup
            ? "부모 ROI 추가"
            : ParentId is null ? "ROI 추가" : "자식 ROI 추가";

        public (long Before, long After)? OrderChange => (CurrentOrder, CurrentOrder + 1);

        public DocumentState ComputeNextState(DocumentState before, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (ParentId is { } parentId)
            {
                if (!before.Rois.ContainsKey(parentId))
                {
                    throw new ArgumentException("자식 ROI를 추가할 부모가 없습니다.", nameof(ParentId));
                }

                if (RoiHierarchy.Depth(before.Rois, parentId) >= RoiHierarchy.MaximumDepth)
                {
                    throw new InvalidOperationException("ROI 계층은 RootGroup을 포함하여 최대 4단계입니다.");
                }
            }

            var clippedGeometry = RoiImageBounds.ClipNew(Geometry, before.Image);
            var order = CurrentOrder + 1;
            var roi = new Roi(Id, RoiName.CreateDefault(order), clippedGeometry, ParentId, IsGroup, order);
            var next = before.WithRois(before.Rois.Add(roi.Id, roi), [roi.Id]).WithSelection([roi.Id]);
            if (ParentId is { } parent)
            {
                RoiGroupBounds.Validate(next, [parent], before);
            }

            return next;
        }
    }

    internal sealed class AddRangeOperation(
        IEnumerable<RoiCreation> definitions,
        bool selectAdded,
        bool isNameVisible,
        long currentOrder) : IEditorOperation
    {
        private readonly long _currentOrder = currentOrder;
        private List<Guid>? _addedIds;
        private long _finalOrder = currentOrder;

        public string Name => "ROI 일괄 추가";

        public (long Before, long After)? OrderChange => (_currentOrder, _finalOrder);

        public IReadOnlyList<Guid> AddedIds => _addedIds ?? [];

        public DocumentState ComputeNextState(DocumentState before, CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(definitions);
            var rois = before.Rois.ToBuilder();
            var added = new List<Guid>();
            var order = _currentOrder;
            foreach (var definition in definitions)
            {
                token.ThrowIfCancellationRequested();
                Guid? parent = null;
                if (definition.ParentIndex is { } index)
                {
                    if (index < 0 || index >= added.Count)
                    {
                        throw new ArgumentException("부모는 같은 배치에서 자식보다 먼저 정의해야 합니다.", nameof(definitions));
                    }

                    parent = added[index];
                    if (RoiHierarchy.Depth(rois, parent.Value) >= RoiHierarchy.MaximumDepth)
                    {
                        throw new InvalidOperationException("ROI 계층은 RootGroup을 포함하여 최대 4단계입니다.");
                    }
                }

                var nextOrder = ++order;
                var roi = new Roi(
                    Guid.NewGuid(),
                    RoiName.CreateDefault(nextOrder),
                    RoiImageBounds.ClipNew(definition.Geometry, before.Image),
                    parent,
                    false,
                    nextOrder)
                {
                    IsNameVisible = isNameVisible
                };
                rois.Add(roi.Id, roi);
                added.Add(roi.Id);
            }

            var next = before.WithRois(rois.ToImmutable(), added);
            if (selectAdded)
            {
                next = next.WithSelection(RoiIdSet.CreateRange(added));
            }

            RoiGroupBounds.Validate(next, added);
            _addedIds = added;
            _finalOrder = order;
            return next;
        }
    }

    internal sealed class ReplaceWithNamedRoisOperation(
        string editName,
        IReadOnlyList<Roi> namedRois,
        bool selectAdded,
        long currentOrder) : IEditorOperation
    {
        private readonly long _currentOrder = currentOrder;
        private long _maxOrder = currentOrder;

        public string Name => editName;

        public (long Before, long After)? OrderChange => (_currentOrder, _maxOrder);

        public DocumentState ComputeNextState(DocumentState before, CancellationToken token)
        {
            var builder = ImmutableDictionary.CreateBuilder<Guid, Roi>();
            var changedIds = new List<Guid>(before.Rois.Count + namedRois.Count);
            foreach (var existingId in before.Rois.Keys)
            {
                changedIds.Add(existingId);
            }

            long maxOrder = _currentOrder;
            for (int i = 0; i < namedRois.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var roi = namedRois[i];
                var clipped = roi with
                {
                    Geometry = RoiImageBounds.ClipNew(roi.Geometry, before.Image)
                };
                builder[clipped.Id] = clipped;
                changedIds.Add(clipped.Id);
                if (clipped.Order > maxOrder)
                {
                    maxOrder = clipped.Order;
                }
            }

            var next = before.WithRois(builder.ToImmutable(), changedIds);
            next = selectAdded
                ? next.WithSelection(RoiIdSet.CreateRange(namedRois.Select(static r => r.Id)))
                : next.WithSelection([]);
            _maxOrder = maxOrder;
            return next;
        }
    }

    internal sealed record SelectAllOperation : IEditorOperation
    {
        public string? Name => null;

        public DocumentState ComputeNextState(DocumentState before, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return before.Selection.Count == before.Rois.Count
                ? before
                : before.WithSelection(RoiIdSet.CreateRange(before.Rois.Keys));
        }
    }

    internal sealed record SelectOperation(IEnumerable<Guid> Ids) : IEditorOperation
    {
        public string? Name => null;

        public DocumentState ComputeNextState(DocumentState before, CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(Ids);
            var selection = RoiIdSet.CreateRange(Ids.Where(id =>
            {
                token.ThrowIfCancellationRequested();
                return before.Rois.ContainsKey(id);
            }));
            return selection.SetEquals(before.Selection) ? before : before.WithSelection(selection);
        }
    }

    internal sealed record SelectRegionOperation(BoundsD Bounds, bool Additive) : IEditorOperation
    {
        public string? Name => null;

        public DocumentState ComputeNextState(DocumentState before, CancellationToken token)
        {
            var ids = EditorSession.FindContainedOrdinaryIds(before, Bounds, token);
            var nextSelection = Additive ? before.Selection.Union(ids) : RoiIdSet.CreateRange(ids);
            return before.WithSelection(nextSelection);
        }
    }

    internal sealed record PasteOperation(
        RoiClipboardContent Content,
        ImageReference Image,
        PointD Step,
        long CurrentOrder) : IEditorOperation
    {
        public string Name => "ROI 붙여넣기";

        public (long Before, long After)? OrderChange => (CurrentOrder, CurrentOrder + Content.OrderCount);

        public DocumentState ComputeNextState(DocumentState before, CancellationToken token)
            => RoiPaste.Prepare(before, Content, Image, Step, CurrentOrder, token);
    }

    internal sealed record SetViewportOperation(ViewportTransform View) : IEditorOperation
    {
        public string? Name => null;

        public DocumentState ComputeNextState(DocumentState before, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return before.WithViewport(View);
        }
    }

    internal sealed record SetImageOperation(ImageReference Image, ViewportTransform View) : IEditorOperation
    {
        public string Name => "이미지 교체";

        public bool ReplacesImage => true;

        public DocumentState ComputeNextState(DocumentState before, CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(Image);
            token.ThrowIfCancellationRequested();
            return new DocumentState(ImmutableDictionary<Guid, Roi>.Empty, [], Image, View);
        }
    }

    internal sealed record SetPropertiesOperation(
        Guid Id,
        string RoiDisplayName,
        RoiGeometry Geometry,
        RoiSearchRange? SearchRange,
        bool IsNameVisible) : IEditorOperation
    {
        public string Name => "ROI 속성 변경";

        public DocumentState ComputeNextState(DocumentState before, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var trimmedName = RoiName.ValidateAndTrim(RoiDisplayName);
            if (before.IsGroupRoi(Id) && SearchRange != before.Rois[Id].SearchRange)
            {
                throw new ArgumentException("그룹 부모에는 검색 범위를 설정할 수 없습니다.", nameof(SearchRange));
            }

            // 확장 크기의 오버플로도 형상/이름을 게시하기 전에 검증한다.
            _ = SearchRange?.Expand(Geometry);
            var next = RoiTransforms.Apply(before, [Id], _ => Geometry);
            var changedIds = ReferenceEquals(next, before) ? [Id] : next.Snapshot.ChangedIds.Append(Id);
            return next.WithRoisFrom(before, next.Rois.SetItem(Id, next.Rois[Id] with
            {
                Name = trimmedName,
                SearchRange = SearchRange,
                IsNameVisible = IsNameVisible
            }), changedIds);
        }
    }

    internal sealed record RenameOperation(Guid Id, string NewName) : IEditorOperation
    {
        public string Name => "이름 변경";

        public DocumentState ComputeNextState(DocumentState before, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var trimmedName = RoiName.ValidateAndTrim(NewName, "name");
            return before.WithRois(before.Rois.SetItem(Id, before.Rois[Id] with
            {
                Name = trimmedName
            }), [Id]);
        }
    }

    internal sealed record SetOcvSettingsOperation(IReadOnlyList<Guid> Ids, RoiOcvSettings Settings) : IEditorOperation
    {
        public string Name => "ROI OCV 설정 변경";

        public DocumentState ComputeNextState(DocumentState before, CancellationToken token)
        {
            var normalized = new RoiOcvSettings(
                Settings.ExpectedText ?? string.Empty,
                Settings.FormatMask ?? string.Empty,
                RoiOcvSettings.NormalizeFontId(Settings.FontId));

            var rois = before.Rois.ToBuilder();
            var changedIds = new List<Guid>(Ids.Count);
            foreach (var id in Ids)
            {
                token.ThrowIfCancellationRequested();
                if (!rois.TryGetValue(id, out var current) || before.IsGroupRoi(id) || current.Ocv == normalized)
                {
                    continue;
                }

                rois[id] = current with { Ocv = normalized };
                changedIds.Add(id);
            }

            return changedIds.Count == 0 ? before : before.WithRois(rois.ToImmutable(), changedIds);
        }
    }

    internal sealed record DeleteSelectionOperation : IEditorOperation
    {
        public string Name => "선택 ROI 삭제";

        public DocumentState ComputeNextState(DocumentState before, CancellationToken token)
        {
            // 명시적으로 연결된 하위 트리만 삭제하며, 겹치는 독립 ROI는 보존한다.
            var ids = before.Selection.Count == before.Rois.Count
                ? before.Selection
                : RoiHierarchy.Subtree(before, RoiHierarchy.EffectiveRoots(before), token).ToImmutableHashSet();
            var rois = before.Rois.ToBuilder();
            foreach (var id in ids)
            {
                token.ThrowIfCancellationRequested();
                rois.Remove(id);
            }

            return before.WithRois(rois.ToImmutable(), ids)
                .WithSelection(before.Selection.Except(ids));
        }
    }

    internal sealed record SetFiltersOperation(FilterSettings Settings) : IEditorOperation
    {
        public string Name => "ROI 필터 등록";

        public DocumentState ComputeNextState(DocumentState before, CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(Settings);
            Settings.Validate();
            var rois = before.Rois.ToBuilder();
            foreach (var id in before.Selection)
            {
                token.ThrowIfCancellationRequested();
                rois[id] = rois[id] with
                {
                    Filter = Settings,
                    FilterPipeline = []
                };
            }

            return before.WithRois(rois.ToImmutable(), before.Selection);
        }
    }

    internal sealed record SetFilterPipelineOperation(ImmutableArray<FilterSettings> Pipeline) : IEditorOperation
    {
        public string Name => "ROI 전처리 파이프라인 등록";

        public DocumentState ComputeNextState(DocumentState before, CancellationToken token)
        {
            var normalized = Pipeline.IsDefault
                ? ImmutableArray<FilterSettings>.Empty
                : Pipeline.Where(static s => s.Kind != RoiFilterKind.None).ToImmutableArray();
            foreach (var step in normalized)
            {
                step.Validate();
            }

            var primary = normalized.LastOrDefault(static s => s.IsEnabled)
                          ?? (normalized.IsEmpty ? new FilterSettings(RoiFilterKind.None) : normalized[^1]);

            var rois = before.Rois.ToBuilder();
            foreach (var id in before.Selection)
            {
                token.ThrowIfCancellationRequested();
                rois[id] = rois[id] with
                {
                    Filter = primary,
                    FilterPipeline = normalized
                };
            }

            return before.WithRois(rois.ToImmutable(), before.Selection);
        }
    }

    internal sealed record TranslateSelectionOperation(PointD Delta) : IEditorOperation
    {
        public string Name => "ROI 이동";

        public DocumentState ComputeNextState(DocumentState before, CancellationToken token)
        {
            var selection = before.TransformSelection;
            var transform = new RigidTransform2D(default, 0, selection.Constrain(Delta));
            return selection.Apply(transform, token).WithViewport(before.Viewport);
        }
    }

    internal sealed record RotateSelectionOperation(double DeltaDegrees, PointD Pivot) : IEditorOperation
    {
        public string Name => "ROI 회전";

        public DocumentState ComputeNextState(DocumentState before, CancellationToken token)
        {
            var selection = before.TransformSelection;
            var transform = new RigidTransform2D(Pivot, selection.ConstrainRotation(Pivot, DeltaDegrees));
            return selection.Apply(transform, token).WithViewport(before.Viewport);
        }
    }

    internal sealed record ResetAnglesOperation : IEditorOperation
    {
        public string Name => "0°로 초기화";

        public DocumentState ComputeNextState(DocumentState before, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return RoiTransforms.ResetAngles(before);
        }
    }

    internal sealed record SetGeometryOperation(Guid Id, RoiGeometry Geometry) : IEditorOperation
    {
        public string Name => "ROI 속성 변경";

        public DocumentState ComputeNextState(DocumentState before, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return RoiTransforms.Apply(before, [Id], _ => Geometry);
        }
    }

    internal sealed record GroupSelectionOperation(long CurrentOrder) : IEditorOperation
    {
        public string Name => "부모 ROI로 그룹화";

        public (long Before, long After)? OrderChange => (CurrentOrder, CurrentOrder + 1);

        public DocumentState ComputeNextState(DocumentState before, CancellationToken token)
            => RoiGroupEditor.Group(before, CurrentOrder + 1, token);
    }

    internal sealed record UngroupSelectionOperation : IEditorOperation
    {
        public string Name => "그룹 해제";

        public DocumentState ComputeNextState(DocumentState before, CancellationToken token)
            => RoiGroupEditor.Ungroup(before, token);
    }

    internal sealed record AttachOperation(IReadOnlyList<Guid> Roots, Guid ParentId) : IEditorOperation
    {
        public string Name => "부모 ROI에 연결";

        public DocumentState ComputeNextState(DocumentState before, CancellationToken token)
            => RoiGroupEditor.Attach(before, Roots, ParentId, token);
    }

    internal sealed record SetMasksOperation(Guid Id, ImmutableArray<MaskRegion> Masks, string EditName) : IEditorOperation
    {
        public string Name => EditName;

        public DocumentState ComputeNextState(DocumentState before, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (before.IsGroupRoi(Id))
            {
                throw new InvalidOperationException("편집 중이 아닌 검사 ROI 하나를 선택하세요.");
            }

            if (Masks.Length > EditorSession.MaximumMasksPerRoi)
            {
                throw new InvalidOperationException($"ROI 하나에 마스크는 최대 {EditorSession.MaximumMasksPerRoi}개입니다.");
            }

            var roi = before.Rois[Id];
            if (roi.Masks.SequenceEqual(Masks))
            {
                return before;
            }

            return before.WithRois(before.Rois.SetItem(Id, roi with
            {
                Masks = Masks
            }), [Id]);
        }
    }

    internal sealed record CommitPreviewOperation(
        string EditName,
        DocumentState Baseline,
        RoiTransformPreview? Preview,
        DocumentState Displayed) : IEditorOperation
    {
        public string Name => EditName;

        public DocumentState? PreviewBaseline => Baseline;

        public DocumentState ComputeNextState(DocumentState before, CancellationToken token)
            => Preview is { } move ? move.Selection.ApplyPreview(move.Transform, token) : Displayed;
    }

    internal sealed class LoadDocumentOperation(
        ImageReference image,
        ImmutableArray<Roi> entries,
        ViewportTransform? viewport,
        long currentOrder,
        bool isInitialDocument) : IEditorOperation
    {
        private readonly long _currentOrder = currentOrder;
        private long _maxOrder = currentOrder;

        public string? Name => isInitialDocument ? null : "모델 로딩";

        public bool ReplacesImage => !isInitialDocument;

        public bool RestoresViewport => true;

        public (long Before, long After)? OrderChange => (_currentOrder, _maxOrder);

        public DocumentState ComputeNextState(DocumentState before, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var dictionary = entries.ToImmutableDictionary(roi => roi.Id);
            var orders = new HashSet<long>();
            foreach (var roi in entries)
            {
                token.ThrowIfCancellationRequested();
                if (roi.Id == Guid.Empty || !RoiName.IsValid(roi.Name) ||
                    roi.Order <= 0 || roi.Order == long.MaxValue || !orders.Add(roi.Order) ||
                    !RoiImageBounds.Fits(roi.Geometry.Bounds, image))
                {
                    throw new ArgumentException("ROI의 ID·이름·순서·이미지 경계가 올바르지 않습니다.", "rois");
                }

                roi.Filter.Validate();
                _ = roi.SearchRange?.Expand(roi.Geometry);
                if (roi.Masks.IsDefault || roi.Masks.Length > EditorSession.MaximumMasksPerRoi || roi.Masks.Any(mask => mask is null))
                {
                    throw new ArgumentException("ROI 마스크 목록이 올바르지 않습니다.", "rois");
                }

                // 최대 깊이로 탐색을 제한하므로 잘못된 순환 연결도 무한 반복하지 않는다.
                var depth = 1;
                for (var parent = roi.ParentId; parent is { } id; parent = dictionary[id].ParentId)
                {
                    if (!dictionary.ContainsKey(id) || ++depth > RoiHierarchy.MaximumDepth)
                    {
                        throw new ArgumentException("부모가 없거나 ROI 계층이 순환/최대 4단계를 초과합니다.", "rois");
                    }
                }
            }

            var view = viewport ?? before.Viewport;
            if (!double.IsFinite(view.Scale) || view.Scale <= 0 || !view.Offset.IsFinite)
            {
                throw new ArgumentException("화면 배율과 위치가 올바르지 않습니다.", "viewport");
            }

            var next = new DocumentState(dictionary, [], image, view);
            if (entries.Any(roi => next.IsGroupRoi(roi.Id) && (roi.SearchRange is not null || !roi.Masks.IsEmpty)))
            {
                throw new ArgumentException("그룹에는 검색 범위나 마스크를 지정할 수 없습니다.", "rois");
            }

            RoiGroupBounds.Validate(next, dictionary.Keys);
            _maxOrder = Math.Max(_currentOrder, entries.IsEmpty ? 0 : entries.Max(roi => roi.Order));
            return next;
        }
    }
}
