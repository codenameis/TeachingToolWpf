using System.Numerics;
using VisionCanvas.Core.Editing;

namespace VisionCanvas.Rendering;

public static class RoiStyleRoles
{
    public static RoiRole Resolve(DocumentState state, Guid id)
    {
        // 명시적으로 연결된 자식 유무가 기준이며 공간 포함이나 그룹 생성 이력으로 추론하지 않는다.
        var level = state.Snapshot.GroupLevels.GetValueOrDefault(id);
        if (level > 0)
        {
            return level switch
            {
                1 => RoiRole.Parent,
                2 => RoiRole.Module,
                _ => RoiRole.Root
            };
        }

        // 기본 ROI는 부모 연결 전에도 자식 스타일을 사용한다. Default는 요약/공통 표시용이다.
        return RoiRole.Child;
    }
}
