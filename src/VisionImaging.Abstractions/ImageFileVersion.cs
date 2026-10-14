namespace VisionImaging.Abstractions;

/// <summary>기존 이력 식별 정책인 파일 길이·UTC 수정 시각. 내용 해시나 동시 쓰기 잠금을 대신하지 않는다.</summary>
public static class ImageFileVersion
{
    public static string Read(string path)
    {
        var file = new FileInfo(path);
        return $"{file.Length}:{file.LastWriteTimeUtc.Ticks}";
    }

    public static void Validate(ImageReference reference)
    {
        if (!File.Exists(reference.Key))
        {
            throw new FileNotFoundException("원본 이미지를 찾을 수 없습니다. 현재 이미지와 편집 이력을 유지합니다.", reference.Key);
        }
        if (reference.Version.Length != 0 && Read(reference.Key) != reference.Version)
        {
            throw new IOException("원본 파일이 변경되어 이력의 이미지를 복원할 수 없습니다.");
        }
    }
}
