namespace VisionAnomaly;

public sealed record VideoSourceSpec(VideoSourceKind Kind, string Address)
{
    public string DisplayName => Kind switch
    {
        VideoSourceKind.File => Path.GetFileName(Address),
        VideoSourceKind.Camera => $"USB 카메라 {Address}",
        _ => Uri.TryCreate(Address, UriKind.Absolute, out var uri) ? $"{uri.Scheme}://{uri.Host}" : "네트워크 영상"
    };

    public void Validate()
    {
        if (!Enum.IsDefined(Kind) || string.IsNullOrWhiteSpace(Address))
        {
            throw new ArgumentException("영상 입력을 지정하세요.");
        }

        if (Kind == VideoSourceKind.File && !System.IO.File.Exists(Address))
        {
            throw new FileNotFoundException("영상 파일을 찾을 수 없습니다.");
        }

        if (Kind == VideoSourceKind.Camera && (!int.TryParse(Address, out var camera) || camera < 0))
        {
            throw new ArgumentException("USB 카메라 번호는0 이상의 정수입니다.");
        }

        if (Kind == VideoSourceKind.Stream && (!Uri.TryCreate(Address, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("rtsp" or "http" or "https")))
        {
            throw new ArgumentException("RTSP 또는 HTTP(S) 영상 주소를 입력하세요.");
        }
    }
}
