using TeachingToolWpf.Models;

namespace TeachingToolWpf.ViewModels;

public sealed partial class ShapeMatchingViewModel
{
    private string? _inspectionNotice;

    public string TeachingSelectionText => _selection is { } roi
        ? $"선택: {roi.ShapeName} · {roi.Width:F0} × {roi.Height:F0} px · {roi.Angle:F1}°"
        : "이미지에서 학습할 ROI 한 개를 선택하세요.";

    /// <summary>검사에 쓰일 학습 모델이 어떤 ROI에서 왔는지, 지금 선택한 ROI와 같은지 보여 준다.</summary>
    public string TrainedModelText
    {
        get
        {
            if (IsPatMaxMethod && _patMaxState.Template is { } patMaxTpl)
            {
                var pmRoi = patMaxTpl.TeachingRegion;
                var pmSummary = $"검사에 쓰는 PatMax 모델: {pmRoi.ShapeName} {pmRoi.Width:F0} × {pmRoi.Height:F0} px (다이폴 {patMaxTpl.PreviewPoints.Length}개)";
                return _selection is { } cur && (cur != pmRoi || _selectedRoiId != _patMaxState.TeachingRoiId)
                    ? pmSummary + " · 현재 선택 ROI와 다릅니다. ‘선택 ROI 학습’을 눌러야 새 ROI로 검사합니다."
                    : pmSummary;
            }
            if (!IsShapeMethod || ActiveShapeState.TeachingRegion is not { } taught || Model is null)
            {
                return "";
            }
            var summary = $"검사에 쓰는 학습 모델: {taught.ShapeName} {taught.Width:F0} × {taught.Height:F0} px";
            return _selection is { } current && (current != taught || _selectedRoiId != ActiveShapeState.TeachingRoiId)
                ? summary + " · 현재 선택 ROI와 다릅니다. ‘선택 ROI 학습’을 눌러야 새 ROI로 검사합니다."
                : summary;
        }
    }

    public string SearchScopeText => UseSelection
        ? _searchSelection is { } roi
            ? $"검색: {roi.Width:F0} × {roi.Height:F0} px · 선택 영역"
            : _targetRois.Length > 0
                ? $"검색: 대상 리프 ROI {_targetRois.Length}개 각자 개별 검사"
                : "검색할 ROI가 선택되지 않았습니다."
        : _image is { } image ? $"검색: {image.Width:N0} × {image.Height:N0} px · 전체 이미지" : "검색할 이미지를 열어 주세요.";

    public string PanelTitle => OptionsError.Length > 0 ? "설정 확인 필요" : IsBusy ? _training ? "학습 중" : "검사 중" :
        IsTrainingDirty ? "다시 학습하세요" : Verdict switch
        {
            InspectionVerdict.Warning => "검사 확인 필요 · 불량 판정 아님",
            InspectionVerdict.Review => Markers.IsEmpty ? "검출 없음 · 조건 확인" : $"{Markers.Length}개 검출 · 위치 확인",
            InspectionVerdict.Pass => $"{Markers.Length}개 검출 · 정상",
            InspectionVerdict.Fail => VerdictText,
            _ => HasModel ? "검사 준비" : "학습 대기"
        };

    public string PanelHint
    {
        get
        {
            if (HasInputErrors)
            {
                return "숫자 입력을 확인하세요. ‘파라미터 설정’의 잘못 입력한 값을 수정하면 실행할 수 있습니다.";
            }
            if (OptionsError.Length > 0)
            {
                return OptionsError;
            }
            if (IsBusy)
            {
                return "원본 이미지에서 처리하고 있습니다. 아래 ‘취소’로 중단할 수 있습니다.";
            }
            if (_image is null)
            {
                return "이미지를 열고, 찾을 패턴에 ROI를 그려 주세요.";
            }
            if (IsTrainingDirty)
            {
                return "학습 조건이나 마스크가 바뀌었습니다. ‘선택 ROI 학습’을 다시 누르세요.";
            }
            if (_inspectionNotice is not null)
            {
                return _inspectionNotice;
            }
            if (!HasModel)
            {
                return "ROI 한 개를 선택한 뒤 ‘+ 정상 패턴 추가’, ‘+ 불량 패턴 추가’ 또는 ‘선택 패턴 학습’을 누르세요.";
            }
            if (!HasEnabledRegisteredPatterns)
            {
                return "등록된 패턴 리스트에서 검사에 사용할 패턴을 최소 1개 이상 체크하세요.";
            }
            if (UseSelection && _selection is null && _targetRois.IsEmpty)
            {
                return "검사할 ROI를 선택하거나 상단 검사 범위에서 ‘전체 이미지 검사’를 켜세요.";
            }
            if (!_canExecute)
            {
                return "현재 이미지 작업을 마친 뒤 다시 시도하세요.";
            }
            return "검색 범위와 점수를 확인한 뒤 아래 ‘검사 실행’을 누르세요.";
        }
    }

    private static string DetectionNotice(bool candidatesTruncated, bool matchLimitExceeded, int count)
    {
        var messages = new List<string>();
        if (candidatesTruncated)
        {
            messages.Add("일부 후보를 확인하지 못했습니다. ‘파라미터 설정 → 공통 검색 예산’의 최대 후보 수를 늘리거나 검색 ROI를 줄이세요.");
        }
        if (matchLimitExceeded)
        {
            messages.Add("검출 결과 일부가 생략됐습니다. ‘파라미터 설정’의 최대 결과 수를 늘려 다시 검사하세요.");
        }
        return messages.Count > 0 ? string.Join("\n", messages) : count == 0
            ? "설정 조건에 맞는 패턴이 없습니다. 검색 영역과 학습 패턴·각도 범위를 확인하세요."
            : "‘결과’에서 위치·각도·점수를 확인하세요. 이 방식은 제품 합격/불량을 자동 판정하지 않습니다.";
    }

    public string ParameterPreviewSummary
    {
        get
        {
            if (OptionsError.Length > 0)
            {
                return "설정 범위 확인 필요: " + OptionsError;
            }
            if (IsPatMaxMethod)
            {
                var s = _patMaxState.Settings;
                var pol = s.IgnorePolarity ? "극성 무시(|d_m·d_r|)" : "극성 유지";
                var scaleTxt = s.EnableScale ? $"배율 {s.ScaleMin:G}~{s.ScaleMax:G}x" : "배율 고정 1.0x";
                var active = (!IsTrainingDirty ? _patMaxState.Template : null) ?? _patMaxState.PreviewTemplate ?? _patMaxState.Template;
                var dipoleInfo = active is not null
                    ? $"Fine {active.PreviewPoints.Length}개(초록) · Coarse {active.CoarsePreviewPoints.Length}개(하늘) · κ={active.Model.Stability.ConditionNumber:F1}"
                    : $"Coarse {s.CoarseGranularity} / Fine {s.FineGranularity}";
                return $"[실시간 설정 미리보기] PatMax {dipoleInfo} · 에지≥{s.EdgeThreshold:F1} · 인력장 ±{s.ElasticityPixels:F1}px(주황)\n" +
                       $"탐색 각도 {s.MinAngleDegrees:F1}°~{s.MaxAngleDegrees:F1}° · {scaleTxt} · 후보 {s.MaxCandidates}개 · {pol}";
            }
            if (IsCpuMethod)
            {
                var cpuOpts = ActiveCpuState.Options;
                var angleCount = cpuOpts.AngleStep > 0
                    ? (int)Math.Ceiling(Math.Max(0, cpuOpts.MaximumAngle - cpuOpts.MinimumAngle) / cpuOpts.AngleStep) + 1
                    : 1;
                var scaleCount = cpuOpts.ScaleStep > 0
                    ? (int)Math.Ceiling(Math.Max(0, cpuOpts.MaximumScale - cpuOpts.MinimumScale) / cpuOpts.ScaleStep) + 1
                    : 1;
                var featureSummary = IsCpuEdgeMethod
                    ? $"축소 에지 {CpuCoarseFeatures}개 · 원본 에지 {CpuFineFeatures}개"
                    : $"축소 표본 {CpuNccCoarseSamples}개 · 최소 명암차 {CpuNccMinimumContrast}";
                return $"[실시간 설정 미리보기] {featureSummary}\n" +
                       $"회전 {cpuOpts.MinimumAngle:F1}°~{cpuOpts.MaximumAngle:F1}°(간격 {cpuOpts.AngleStep:G}°) · 배율 {cpuOpts.MinimumScale:G}~{cpuOpts.MaximumScale:G}x → 자세 {angleCount * scaleCount:N0}개 · 피라미드 {cpuOpts.PyramidLevels}단계 · 억제 반경 {cpuOpts.SuppressionRadius:F1}px";
            }
            if (!IsShapeMethod)
            {
                return $"[실시간 설정 미리보기] OpenCV 명암 상관계수(CCOEFF_NORMED) · 회전/배율 고정(1.0x)\n" +
                       $"최소 점수 {MinimumScore:F2} · 최대 결과 {MaximumMatches}개 · 중복 허용(IoU) {MaximumOverlap:F2}";
            }

            var roi = _selection ?? ActiveShapeState.TeachingRegion;
            var activeModel = (!IsTrainingDirty ? Model : null) ?? _previewShapeModel;
            var minAngle = InspectRotation ? -180.0 : MinimumAngle;
            var maxAngle = InspectRotation ? 180.0 : MaximumAngle;
            var minScale = EnableScale ? MinimumScale : 1.0;
            var maxScale = EnableScale ? MaximumScale : 1.0;
            var expectedLevels = activeModel?.Levels.Length ?? (roi is not null
                ? Math.Clamp((int)Math.Floor(Math.Log2(Math.Max(roi.Width, roi.Height) / 64)) + 1, 1, 6)
                : 3);
            var expectedTemplates = activeModel?.TemplateCount ??
                ((int)((Math.Ceiling(Math.Max(0, maxAngle - minAngle) / Math.Max(0.1, AngleStep)) + 1) *
                       (EnableScale ? Math.Ceiling(Math.Max(0, maxScale - minScale) / Math.Max(0.01, ScaleStep)) + 1 : 1)));

            string featureLine;
            if (activeModel is not null)
            {
                var level0Count = activeModel.Levels[0].Features.Length;
                var asymCount = 0;
                if (!activeModel.FeatureAsymmetryFlags.IsDefault)
                {
                    foreach (var flag in activeModel.FeatureAsymmetryFlags)
                    {
                        if ((flag & 3) != 0)
                        {
                            asymCount++;
                        }
                    }
                }
                var contextState = StrictContext && MaxOcclusionRatio <= 0.2
                    ? $"비대칭 문맥 키 {asymCount}개(하늘색 엄격 검증 ON)"
                    : $"비대칭 키 {asymCount}개(완화됨 · 초록 표시)";
                featureLine = $"추출 특징 {level0Count}개 (상한 {MaximumFeatures}) · {contextState} · 경사≥{MinimumGradient:G} · 대비비≥{MinimumFeatureContrastRatio:P0}";
            }
            else
            {
                var contextMode = StrictContext && MaxOcclusionRatio <= 0.2 ? "비대칭 문맥 엄격 검증 ON" : "비대칭 문맥 완화 모드";
                featureLine = $"최대 특징 {MaximumFeatures}개 · 경사≥{MinimumGradient:G} · 대비비≥{MinimumFeatureContrastRatio:P0} · {contextMode}";
            }

            var scaleText = EnableScale ? $"배율 {minScale:G}~{maxScale:G}x(간격 {ScaleStep:G})" : "배율 고정 1.0x";
            var polarityText = IgnorePolarity ? "명암 반전 허용" : "명암 극성 유지";
            var refineText = Refine ? "서브픽셀 정밀화 ON" : "픽셀 정밀도";
            var occlusionText = MaxOcclusionRatio > 0 ? $"가림 구출 {MaxOcclusionRatio:P0}" : "가림 0%(엄격)";
            return $"[실시간 설정 미리보기] {featureLine}\n" +
                   $"회전 {minAngle:F0}°~{maxAngle:F0}°(간격 {AngleStep:G}°) · {scaleText} · {polarityText} → 템플릿 {expectedTemplates:N0}개 · 피라미드 {expectedLevels}단계\n" +
                   $"윤곽 허용 거리 ±{ClutterTolerancePixels:F1}px(주황 대역) · {occlusionText} · 지지율≥{MinimumSupport:P0} · Greediness {Greediness:F2} · {refineText}";
        }
    }

    private void NotifyPanelState()
    {
        Notify(nameof(PanelTitle));
        Notify(nameof(PanelHint));
        Notify(nameof(TeachingSelectionText));
        Notify(nameof(TrainedModelText));
        Notify(nameof(SearchScopeText));
        Notify(nameof(ParameterPreviewSummary));
        NotifyPatternListProperties();
    }
}
