using System.Collections.Immutable;
using VisionCanvas.Core.Alignment;
using VisionCanvas.Core.Editing;
using VisionGeometry;

namespace TeachingToolWpf.Core.Tests;

public class RoiAlignmentMapTests
{
    [Fact]
    public void HierarchicalAlignmentComposesGlobalModuleAndGroupInOrderAndPreservesRoiMetadata()
    {
        var (state, moduleA, _, group, leaf, sibling) = CreateDocument();
        var map = new RoiAlignmentMap(state);
        Assert.True(map.TryAlign(null,
            [new(0, 0), new(1000, 0)],
            [new(10, 20), new(1010, 20)],
            out map, out var error), error);
        Assert.True(map.TryAlign(moduleA.Id,
            [new(10, 20), new(110, 20)],
            [new(15, 22), new(15, 122)],
            out map, out error), error);
        Assert.True(map.TryAlign(group.Id,
            [new(30, 30)],
            [new(33, 26)],
            out map, out error), error);

        var expected = new RigidTransform2D(new(30, 30), 90, new(3, -4))
            .Apply(leaf.Geometry.Center);
        var actual = map.GetAlignment(leaf.Id)!;
        AssertClose(expected, actual.Transform.Apply(leaf.Geometry.Center));
        var aligned = map.Map(leaf.Id);
        Assert.Equal(leaf.Id, aligned.Id);
        Assert.Equal(leaf.ParentId, aligned.ParentId);
        Assert.Equal(leaf.Name, aligned.Name);
        Assert.Equal(leaf.SearchRange, aligned.SearchRange);
        Assert.Equal(leaf.Masks, aligned.Masks);
        AssertClose(expected, aligned.Geometry.Center);
        Assert.Equal(90, aligned.Geometry.Angle, 6);
        Assert.Equal(leaf.Geometry.Width, aligned.Geometry.Width, 8);
        Assert.Equal(leaf.Geometry.Height, aligned.Geometry.Height, 8);

        var alignedSibling = map.Map(sibling.Id);
        AssertClose(sibling.Geometry.Center + new PointD(10, 20), alignedSibling.Geometry.Center);
        Assert.Equal(0, alignedSibling.Geometry.Angle, 8);
        Assert.Equal(leaf, state.Rois[leaf.Id]);
    }

    [Fact]
    public void ChangingAnAncestorAfterDescendantAlignmentIsRejectedWithoutMutatingPreviousMap()
    {
        var (state, moduleA, _, group, leaf, _) = CreateDocument();
        var map = new RoiAlignmentMap(state);
        Assert.True(map.TryAlign(group.Id, [new(0, 0)], [new(3, 4)], out map, out _));
        var before = map.Map(leaf.Id);
        Assert.False(map.TryAlign(moduleA.Id, [new(0, 0)], [new(10, 0)], out var rejected, out var error));
        Assert.Same(map, rejected);
        Assert.Contains("하위", error);
        Assert.Equal(before, map.Map(leaf.Id));
    }

    [Fact]
    public void LeafAndUnknownScopesAndFailedFitsDoNotChangeTheMap()
    {
        var (state, module, _, _, leaf, _) = CreateDocument();
        var map = new RoiAlignmentMap(state);
        foreach (var id in new[] { leaf.Id, Guid.NewGuid() })
        {
            Assert.False(map.TryAlign(id, [new(0, 0)], [new(1, 1)], out var failed, out var error));
            Assert.Same(map, failed);
            Assert.NotEmpty(error);
        }
        Assert.False(map.TryAlign(module.Id, [], [], out var rejected, out _));
        Assert.Same(map, rejected);
    }

    [Fact]
    public void BrokenOrCyclicParentsAreReportedInsteadOfGuessingByPosition()
    {
        var (state, module, _, group, leaf, _) = CreateDocument();
        var orphaned = state.WithRois(state.Rois.SetItem(group.Id, group with
        {
            ParentId = Guid.NewGuid()
        }), [group.Id]);
        Assert.Throws<InvalidOperationException>(() => new RoiAlignmentMap(orphaned).Map(leaf.Id));
        var cyclic = state.WithRois(state.Rois.SetItem(module.Id, module with
        {
            ParentId = group.Id
        }), [module.Id]);
        Assert.Throws<InvalidOperationException>(() => new RoiAlignmentMap(cyclic).Map(leaf.Id));
    }

    [Theory]
    [InlineData(5001, 12.5, -7.25, 3.5, -4.0, 6.5, -2.0, 2.25, -1.75, 1.25)]
    [InlineData(15812, -18.0, 24.0, -5.0, 8.5, -3.5, 4.0, -3.0, 5.0, -1.5)]
    [InlineData(50000, 35.75, 14.25, 7.25, -11.5, -9.25, -3.75, 6.5, -4.5, 2.5)]
    public void HierarchicalSessionAlignsHundredMillionToTenBillionRoisAndRecomposesWhenAncestorsChange(
        int gridSidePerComponent,
        double globalDx,
        double globalDy,
        double globalTheta,
        double moduleDx,
        double moduleDy,
        double moduleTheta,
        double componentDx,
        double componentDy,
        double componentTheta)
    {
        var (image, rois, scopes) = AlignmentTestPreset.Create(gridSidePerComponent, visualGridSidePerComponent: 5);
        var state = new DocumentState(rois.ToImmutableDictionary(r => r.Id), RoiIdSet.Empty, image, ViewportTransform.Identity);
        var partitionsBefore = state.Snapshot.AlignmentPartitions;

        var session = new HierarchicalAlignmentSession(state, scopes);
        var expectedGridCells = 4L * gridSidePerComponent * gridSidePerComponent;
        Assert.Equal(state.RoiCount + expectedGridCells, session.TotalLogicalRoiCount);

        var moduleAScope = scopes.First(s => s.Name.Contains("모듈 A"));
        var moduleBScope = scopes.First(s => s.Name.Contains("모듈 B"));
        var compA1Scope = scopes.First(s => s.Name.Contains("컴포넌트 A-1"));
        var compA2Scope = scopes.First(s => s.Name.Contains("컴포넌트 A-2"));
        var compB1Scope = scopes.First(s => s.Name.Contains("컴포넌트 B-1"));

        // 하위(컴포넌트 A-1)를 먼저 설정한 뒤 상위(모듈 A, 전체)를 나중에 수정해도 자동 재합성되어야 한다.
        Assert.True(session.TrySetOffset(compA1Scope.ScopeId, new(componentDx, componentDy, componentTheta), out var error), error);
        Assert.True(session.TrySetOffset(moduleAScope.ScopeId, new(moduleDx, moduleDy, moduleTheta), out error), error);
        Assert.True(session.TrySetOffset(null, new(globalDx, globalDy, globalTheta), out error), error);

        var map = session.CurrentMap;
        Assert.Same(partitionsBefore[0], state.Snapshot.AlignmentPartitions[0]);

        var globalTransform = map.OverallTransform;
        var moduleATransform = map.GetScopeTransform(moduleAScope.ScopeId);
        var compA1Transform = map.GetScopeTransform(compA1Scope.ScopeId);
        var compA2Transform = map.GetScopeTransform(compA2Scope.ScopeId);
        var compB1Transform = map.GetScopeTransform(compB1Scope.ScopeId);

        var expectedTotalAngle = Math.IEEERemainder(globalTheta + moduleTheta + componentTheta, 360);
        Assert.Equal(expectedTotalAngle, compA1Transform.AngleDegrees, 6);

        // 컴포넌트 A-2는 자기 보정이 없으므로 모듈 A 합성 변환을 그대로 상속한다.
        Assert.Equal(moduleATransform.AngleDegrees, compA2Transform.AngleDegrees, 8);
        AssertClose(moduleATransform.Apply(new(2350, 5550)), compA2Transform.Apply(new(2350, 5550)));

        // 모듈 B와 컴포넌트 B-1은 로컬 보정이 없으므로 전체(Global) 보정만 상속한다.
        Assert.Equal(globalTransform.AngleDegrees, map.GetScopeTransform(moduleBScope.ScopeId).AngleDegrees, 8);
        AssertClose(globalTransform.Apply(new(5850, 2650)), compB1Transform.Apply(new(5850, 2650)));

        // 1억~100억 개 파라메트릭 격자의 첫 셀, 중앙 셀, 마지막 셀 보정 좌표 검증
        var gridA1 = compA1Scope.Grid!;
        foreach (var (r, c) in new[] { (0, 0), (gridSidePerComponent / 2, gridSidePerComponent / 2), (gridSidePerComponent - 1, gridSidePerComponent - 1) })
        {
            var teachingCell = gridA1.GetTeachingCell(r, c);
            var alignedCell = map.MapGridCell(compA1Scope.ScopeId!.Value, r, c);
            AssertClose(compA1Transform.Apply(teachingCell.Center), alignedCell.Center);
            Assert.Equal(RoiGeometry.NormalizeAngle(teachingCell.Angle + compA1Transform.AngleDegrees), alignedCell.Angle, 6);
        }

        // 모듈 A의 보정마크를 비활성화(IsEnabled=false)하면 모듈 A 오프셋은 건너뛰고 전체 + 컴포넌트 A-1만 합성된다.
        Assert.True(session.TrySetOffset(moduleAScope.ScopeId, session.GetOffset(moduleAScope.ScopeId).WithEnabled(false), out error), error);
        var mapWithoutModuleA = session.CurrentMap;
        var angleWithoutModuleA = RoiGeometry.NormalizeAngle(globalTheta + componentTheta);
        Assert.Equal(angleWithoutModuleA, RoiGeometry.NormalizeAngle(mapWithoutModuleA.GetScopeTransform(compA1Scope.ScopeId).AngleDegrees), 6);
        Assert.Equal(RoiGeometry.NormalizeAngle(globalTheta), RoiGeometry.NormalizeAngle(mapWithoutModuleA.GetScopeTransform(compA2Scope.ScopeId).AngleDegrees), 6);
    }

    private static (DocumentState State, Roi ModuleA, Roi ModuleB, Roi Group, Roi Leaf, Roi Sibling) CreateDocument()
    {
        var root = new Roi(Guid.NewGuid(), "전체", new(new(500, 500), 1000, 1000, 0), null, true, 1);
        var moduleA = new Roi(Guid.NewGuid(), "모듈 A", new(new(50, 50), 100, 100, 0), root.Id, true, 2);
        var moduleB = new Roi(Guid.NewGuid(), "모듈 B", new(new(250, 50), 100, 100, 0), root.Id, true, 3);
        var group = new Roi(Guid.NewGuid(), "그룹 A1", new(new(25, 35), 20, 20, 0), moduleA.Id, true, 4);
        var leaf = new Roi(Guid.NewGuid(), "검사 ROI", new(new(25, 35), 4, 4, 0), group.Id, false, 5)
        {
            SearchRange = new(20, 20),
            Masks = [new(MaskShape.Rectangle, new(0, 0, 0.2, 0.2))]
        };
        var sibling = new Roi(Guid.NewGuid(), "다른 모듈 ROI", new(new(205, 35), 4, 4, 0), moduleB.Id, false, 6);
        var rois = new[] { root, moduleA, moduleB, group, leaf, sibling }.ToImmutableDictionary(r => r.Id);
        return (new(rois, RoiIdSet.Empty, null, ViewportTransform.Identity), moduleA, moduleB, group, leaf, sibling);
    }

    private static void AssertClose(PointD expected, PointD actual)
    {
        Assert.InRange((expected - actual).Length, 0, 1e-8);
    }
}
