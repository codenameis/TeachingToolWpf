namespace PyramidComparison;

/// <summary>이전 코덱별 압축 corpus 생성 명령은 폐기한다. 기존 원본과 측정 결과는 보존한다.</summary>
internal static class CodecFixtures
{
    public static void Create(string directory) => Retired();
    public static void CreateLarge(string directory) => Retired();

    private static void Retired() => throw new NotSupportedException(
        "기존 NetVips corpus 생성기는 제거되었습니다. 보존된 artifacts 입력과 native 코덱 테스트 fixture를 사용하세요. " +
        "다른 인코더로 같은 파일명을 재생성하면 과거 측정과 동일한 입력이 아닙니다.");
}
