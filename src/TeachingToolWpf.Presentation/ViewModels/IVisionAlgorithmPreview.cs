using System.ComponentModel;
using VisionCanvas.Imaging.Filters;
using VisionCanvas.Rendering;
using VisionGeometry;

namespace TeachingToolWpf.ViewModels;

/// <summary>
/// 모든 비전 알고리즘 도구(패턴매칭, 도형 검출, 향후 추가될 캘리퍼/블롭/새 매칭 등)가 공유하는
/// 캔버스 미리보기·시각적 진단·문서 상태 동기화 계약.
/// </summary>
public interface IVisionAlgorithmPreview : INotifyPropertyChanged, IDisposable
{
    /// <summary>미리보기 또는 결과 오버레이가 갱신되었을 때 발생한다.</summary>
    event Action? Changed;

    /// <summary>캔버스 뷰포트를 검출/학습 영역으로 이동시킬 때 발생한다.</summary>
    event Action<BoundsD>? LocateRequested;

    /// <summary>현재 도구가 유휴(실행 가능) 상태인지 여부.</summary>
    bool IsIdle { get; }

    /// <summary>현재 도구가 비동기 학습/검사를 수행 중인지 여부.</summary>
    bool IsBusy { get; }

    /// <summary>캔버스에 결과/미리보기 오버레이를 표시할지 여부.</summary>
    bool ShowOverlay { get; set; }

    /// <summary>캔버스에 그릴 현재 알고리즘의 학습 미리보기 또는 검사 결과 오버레이.</summary>
    ResultOverlay? Overlay { get; }

    /// <summary>현재 알고리즘 파라미터와 연동되어 캔버스 ROI 내부에 실시간 표시되는 검사 전처리 필터 영상.</summary>
    FilterPreviewResult? FilterPreview => null;

    /// <summary>학습·검사를 실행하기 전 현재 파라미터 설정이 추출 특징·탐색 공간·정밀도에 미치는 영향을 요약한 문구.</summary>
    string ParameterPreviewSummary { get; }

    /// <summary>문서 상태·이미지 세대·라이브 여부·도구 가용성을 동기화한다.</summary>
    void Update(InspectionUpdate update);
}
