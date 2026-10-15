using VisionImaging.Abstractions;

namespace VisionCanvas.MvvmSample.Services;

// VM은 '어떤 Windows 창을 띄우는지' 대신 '선택한 이미지 정보를 받을 수 있다'는 약속만 안다.
// 테스트에서는 이 약속을 구현한 가짜 객체를 전달하므로 실제 파일 창을 열 필요가 없다.
public interface IImageFilePicker
{
    // ?는 null을 반환할 수도 있다는 뜻이다. 여기서 null은 사용자가 선택을 취소한 경우다.
    ImageReference? Pick();
}
