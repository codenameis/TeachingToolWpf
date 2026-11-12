using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Windows.Input;
using TeachingToolWpf.Commands;
using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.Models;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Interaction;
using VisionCanvas.Presentation;
using VisionCanvas.Rendering;
using VisionImaging;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.ViewModels;

/// <summary>메인 창의 명령을 한곳에서 연결한다. 생성자는 객체 조립만 남긴다.</summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>버튼·단축키가 호출할 명령을 만든다. 실행 조건(CanExecute)은 각 명령이 스스로 갖는다.</summary>
    /// <remarks>
    /// 명령 속성은 생성자에서 직접 대입하지 않고 이 메서드가 채운다. 그래서 컴파일러는 생성자가 끝날 때
    /// 속성이 비어 있다고 경고한다. 아래 <see cref="MemberNotNullAttribute"/> 목록은 "이 메서드가 돌아오면
    /// 이 속성들은 모두 null이 아니다"라고 컴파일러에게 알리는 선언이다. 목록이 곧 연결하는 명령의 전체 목록이며,
    /// 하나라도 빠뜨리면 컴파일러가 경고하므로 배선 누락을 빌드에서 잡을 수 있다.
    /// </remarks>
    [MemberNotNull(
        nameof(PreviousRowPage), nameof(NextRowPage))]                                   // 목록 페이지
    [MemberNotNull(
        nameof(SelectTool), nameof(PanTool), nameof(DrawTool), nameof(AddRoi),           // 도구 선택과
        nameof(AddParentRoi), nameof(DrawParentRoi), nameof(AddRepeatedTest),            // ROI 생성
        nameof(CompletePolygon), nameof(RemovePolygonPoint), nameof(CancelPolygon))]
    [MemberNotNull(
        nameof(ZoomIn), nameof(ZoomOut), nameof(ApplyZoom), nameof(Fit), nameof(ActualSize), // 화면 이동·확대
        nameof(Left), nameof(Right), nameof(Up), nameof(Down))]
    [MemberNotNull(
        nameof(Delete), nameof(Copy), nameof(Paste), nameof(Group), nameof(Ungroup),     // 편집과 이력
        nameof(ChooseChildRois), nameof(AttachToParent), nameof(ResetAngle), nameof(RotateLeft),
        nameof(RotateRight), nameof(ApplyProperties), nameof(SelectAll), nameof(Undo), nameof(Redo))]
    [MemberNotNull(
        nameof(CancelImport), nameof(Initialize), nameof(OpenImage),                     // 이미지·라이브·샘플
        nameof(Sample8K), nameof(Sample24K), nameof(PatternSample), nameof(PrimitiveSample),
        nameof(FewShotSample), nameof(SuiteSample), nameof(StartLive), nameof(StopLive), nameof(RestartRenderer),
        nameof(SaveTeachingRecipe), nameof(LoadTeachingRecipe))]
    private void WireCommands(IImageDisplay display)
    {
        // 목록 페이지
        PreviousRowPage = Command(() => ChangeRowPage(_rowPage - 1), () => _rowPage > 0);
        NextRowPage = Command(() => ChangeRowPage(_rowPage + 1), () => (long)(_rowPage + 1) * RowPageSize < TotalLogicalRowCount);

        // 도구 선택과 ROI 생성
        SelectTool = Command(() => SetTool(EditorTool.Select));
        PanTool = Command(() => SetTool(EditorTool.Pan), navigation: true);
        DrawTool = Command(() => StartRoiDrawing(SelectedGroupParent), CanCreateRoi);
        AddRoi = Command(AddRoiInView, CanCreateRoi);
        AddParentRoi = Command(AddParentRoiInView, HasImage);
        DrawParentRoi = Command(StartParentDrawing, HasImage);
        AddRepeatedTest = Command(AddRepeatedTestInDocument, HasImage);
        CompletePolygon = Command(Input.CompletePolygon, () => Input.CanCompletePolygon);
        RemovePolygonPoint = Command(Input.RemovePolygonPoint, () => Input.PolygonPointCount > 0);
        CancelPolygon = Command(Input.Cancel, () => Input.PolygonPointCount > 0);

        // 화면 이동·확대
        ZoomIn = Command(() => Zoom(1.25), HasImage, true);
        ZoomOut = Command(() => Zoom(.8), HasImage, true);
        ApplyZoom = Command(SetZoom, HasImage, true);
        Fit = Command(FitImage, HasImage, true);
        ActualSize = Command(() => ZoomTo(1 / Layout.Dpi), HasImage, true);
        Left = Command(() => Pan(100, 0), navigation: true);
        Right = Command(() => Pan(-100, 0), navigation: true);
        Up = Command(() => Pan(0, 100), navigation: true);
        Down = Command(() => Pan(0, -100), navigation: true);

        // 편집과 이력
        Delete = Async(() => EditCompletion = PrepareEditAsync("선택 ROI 삭제", Session.PrepareDeleteAsync), HasSelection);
        Copy = Command(CopySelection, HasSelection);
        Paste = Async(() => PasteCompletion = PasteAsync(), () => HasImage() && _clipboard.HasContent);
        Group = Async(() => EditCompletion = PrepareEditAsync("부모 ROI로 그룹화", Session.PrepareGroupAsync), () => !HasRepeatedSelection && Session.State.SelectionFacts.CanGroup);
        ChooseChildRois = Command(ChooseRoisToAttach, () => HasSelection() && !HasRepeatedSelection &&
            (Session.State.Snapshot.Children.IsEmpty || Session.State.Selection.All(id => Session.State.Rois[id].ParentId is null)));
        AttachToParent = Async(AttachRoisToParentAsync, () => _roisToAttach.Length > 0 && CanAttachToSelectedRoi());
        Ungroup = Async(() => EditCompletion = PrepareEditAsync("그룹 해제", Session.PrepareUngroupAsync), () => !HasRepeatedSelection && Session.State.SelectionFacts.HasGroup);
        ResetAngle = Command(Session.ResetAngles, () => Session.State.SelectionCount == 1 && !HasRepeatedSelection);
        RotateLeft = Command(() => Rotate(-15), () => Session.State.SelectionCount == 1);
        RotateRight = Command(() => Rotate(15), () => Session.State.SelectionCount == 1);
        ApplyProperties = Command(Properties.Apply, () => Properties.HasSingleSelection);
        SelectAll = Async(() => EditCompletion = PrepareEditAsync("전체 선택", Session.PrepareSelectAllAsync), () => Session.State.RoiCount != 0);
        Undo = Async(() => EditCompletion = HistoryAsync(false), () => Session.CanUndo);
        Redo = Async(() => EditCompletion = HistoryAsync(true), () => Session.CanRedo);

        // 이미지·라이브·샘플
        CancelImport = new(Images.Cancel, () => Images.IsBusy);
        Initialize = Async(InitializeAsync);
        OpenImage = Async(OpenAsync, () => _selectImage is not null);
        Sample8K = Async(() => LoadSampleAsync(8192, 8192));
        Sample24K = Async(() => LoadSampleAsync(24576, 32768));
        PatternSample = Async(LoadPatternSampleAsync);
        PrimitiveSample = Async(LoadPrimitiveSampleAsync, () => Inspections.IsIdle);
        FewShotSample = Async(() => LoadFewShotSampleAsync(), () => Inspections.IsIdle);
        SuiteSample = Async(() => LoadInspectionSuiteSampleAsync(Inspections.Suite?.SelectedAlgorithm ?? InspectionSuiteAlgorithmKind.Metrology), () => Inspections.IsIdle);
        StartLive = Async(StartLiveAsync, () => !Live.IsRunning);
        StopLive = Async(Live.StopAsync, () => Live.IsRunning);
        RestartRenderer = Async(display.RestartAsync);
        SaveTeachingRecipe = Command(SaveTeachingRecipeToDefaultFile, () => true);
        LoadTeachingRecipe = Command(LoadTeachingRecipeFromDefaultFile, () => true);
    }

    private async Task LoadPrimitiveSampleAsync()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "primitive-shapes.png");
        await Images.ReplaceAsync(new(path, "일반 도형 샘플", 640, 480, ImageFileVersion.Read(path)));
        if (Session.State.Image?.Key == path)
        {
            Inspections.Selected = InspectionToolKind.PrimitiveShapes;
        }
    }

    public async Task LoadFewShotSampleAsync(bool placePresetRois = false)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "edge-fewshot-single-board.png");
        await Images.ReplaceAsync(new(path, "소량 분류 단일 보드 샘플", 1000, 560, ImageFileVersion.Read(path)));
        if (Session.State.Image?.Key != path)
        {
            return;
        }

        FitImage();

        if (!placePresetRois)
        {
            if (Inspections.FewShot is not null)
            {
                Inspections.Selected = InspectionToolKind.EdgeFewShot;
                Inspections.Update(Session.State, Images.Generation, false, true);
            }

            Status = "소량 분류 단일 보드 샘플을 불러왔습니다. 직접 ROI를 그려 정상·불량을 학습하고 검사하세요.";
            return;
        }

        float[] trainGoodAngles = [0f, 4.0f, -3.5f, 2.5f, -2.0f];
        var goodRoiIds = new List<Guid>(5);
        for (int i = 0; i < 5; i++)
        {
            var id = Session.Add(new(new(110 + i * 155, 96), 64, 64, trainGoodAngles[i]));
            var roi = Session.State.Rois[id];
            Session.SetProperties(id, $"학습-정상-{i + 1}", roi.Geometry, roi.SearchRange, true);
            goodRoiIds.Add(id);
        }

        float[] trainBadAngles = [0f, -4.0f, 3.0f, -2.5f, 4.5f];
        var defectRoiIds = new List<Guid>(5);
        for (int i = 0; i < 5; i++)
        {
            var id = Session.Add(new(new(110 + i * 155, 256), 64, 64, trainBadAngles[i]));
            var roi = Session.State.Rois[id];
            Session.SetProperties(id, $"학습-불량-{i + 1}", roi.Geometry, roi.SearchRange, true);
            defectRoiIds.Add(id);
        }

        (double X, double Y, double Angle, string Name)[] testSpecs =
        [
            (110, 426, 6.0, "테스트-정상(회전)"),
            (295, 426, -3.5, "테스트-불량(상단번짐)"),
            (480, 426, 3.0, "테스트-불량(하단번짐)"),
            (665, 426, -2.0, "테스트-미등록오염(OOD)")
        ];
        foreach (var spec in testSpecs)
        {
            var id = Session.Add(new(new(spec.X, spec.Y), 64, 64, spec.Angle));
            var roi = Session.State.Rois[id];
            Session.SetProperties(id, spec.Name, roi.Geometry, roi.SearchRange, true);
        }

        if (Inspections.FewShot is { } fewShot)
        {
            Inspections.Selected = InspectionToolKind.EdgeFewShot;
            Inspections.Update(Session.State, Images.Generation, false, true);
            fewShot.RegisterSingleBoardPresetSamples(goodRoiIds, defectRoiIds);
        }
    }

    private void RequestSuiteSampleLoad(InspectionSuiteAlgorithmKind kind)
    {
        if (!CanEdit || Input.IsDragging || !Inspections.IsIdle)
        {
            return;
        }

        _ = ObserveEditAsync(LoadInspectionSuiteSampleAsync(kind, placePresetRois: false));
    }

    public async Task LoadInspectionSuiteSampleAsync(InspectionSuiteAlgorithmKind kind, bool placePresetRois = false)
    {
        var registry = Inspections.Suite?.Registry ?? InspectionAlgorithmRegistry.CreateDefault();
        IRoiInspectionAlgorithm alg = registry.GetRequired(kind);
        kind = alg.Kind;
        string sceneSlug = alg.SampleSceneSlug;
        string sampleTitle = alg.SampleSceneTitle;

        string key = $"{InspectionSuiteSampleTileSource.KeyPrefix}{sceneSlug}";
        await Images.ReplaceAsync(new(key, sampleTitle, InspectionSuiteSampleTileSource.DefaultWidth, InspectionSuiteSampleTileSource.DefaultHeight));
        if (Session.State.Image?.Key != key)
        {
            return;
        }

        FitImage();

        if (!placePresetRois)
        {
            if (Inspections.Suite is { } manualSuite)
            {
                Inspections.Selected = InspectionToolKind.InspectionSuite;
                manualSuite.SelectedAlgorithm = kind;
                Inspections.Update(Session.State, Images.Generation, false, true);
            }

            Status = $"{sampleTitle}을(를) 불러왔습니다. 직접 ROI를 그려 티칭·검사를 테스트하세요.";
            return;
        }

        List<Guid> normalRefIds = [];
        List<Guid> defectRefIds = [];
        List<Guid> targetIds = [];
        var explicitModes = new Dictionary<Guid, RoiSuiteExecutionMode>();

        Guid AddNamedRoi(double cx, double cy, double w, double h, double angleDeg, string name, RoiSuiteExecutionMode? mode = null)
        {
            Guid id = Session.Add(new(new(cx, cy), w, h, angleDeg));
            var roi = Session.State.Rois[id];
            Session.SetProperties(id, name, roi.Geometry, roi.SearchRange, true);
            if (mode is { } m)
            {
                explicitModes[id] = m;
            }

            return id;
        }

        switch (kind)
        {
            case InspectionSuiteAlgorithmKind.Metrology:
                targetIds.Add(AddNamedRoi(320.35, 400.25, 130, 220, 10.0, "트레이스 폭·직선 계측", new(MetrologyDefaultMode: MetrologyMeasurementMode.EdgePairWidth)));
                targetIds.Add(AddNamedRoi(820.40, 400.60, 230, 230, 0.0, "원형 패드 외경·진원도", new(MetrologyDefaultMode: MetrologyMeasurementMode.SpokeCircleFit)));
                break;

            case InspectionSuiteAlgorithmKind.GoldenVariation:
                double[] goldX = [200.2, 460.4, 720.1, 980.3];
                for (int i = 0; i < goldX.Length; i++)
                {
                    normalRefIds.Add(AddNamedRoi(goldX[i], 230.25, 136, 136, 1.15, $"기준-정상다이-{i + 1}"));
                }
                targetIds.Add(AddNamedRoi(250.25, 570.30, 136, 136, 1.15, "검사-정상다이"));
                targetIds.Add(AddNamedRoi(600.30, 570.25, 136, 136, 1.15, "검사-스크래치불량"));
                targetIds.Add(AddNamedRoi(950.20, 570.35, 136, 136, 1.15, "검사-솔더브릿지불량"));
                break;

            case InspectionSuiteAlgorithmKind.BlobRle:
                targetIds.Add(AddNamedRoi(260.5, 400.5, 210, 210, 0.0, "원형 패드 블롭(+노이즈)"));
                targetIds.Add(AddNamedRoi(600.4, 400.6, 230, 210, 0.0, "타원 캡슐 블롭(+노이즈)"));
                targetIds.Add(AddNamedRoi(940.3, 400.4, 230, 210, 0.0, "사각 패키지 블롭(+노이즈)"));
                break;

            case InspectionSuiteAlgorithmKind.Curvilinear:
                targetIds.Add(AddNamedRoi(588.0, 250.0, 780, 110, 0.0, "실링 비드 1 (정상 연속)"));
                targetIds.Add(AddNamedRoi(588.0, 550.0, 780, 110, 0.0, "실링 비드 2 (단선·협소 불량)"));
                break;

            case InspectionSuiteAlgorithmKind.FewShot:
                double[] fsX = [200.0, 460.0, 720.0, 980.0];
                for (int i = 0; i < fsX.Length; i++)
                {
                    normalRefIds.Add(AddNamedRoi(fsX[i], 180.0, 110, 90, (i - 1.5) * 1.4, $"학습-정상-{i + 1}"));
                }
                for (int i = 0; i < fsX.Length; i++)
                {
                    defectRefIds.Add(AddNamedRoi(fsX[i], 400.0, 110, 90, (1.5 - i) * 1.4, $"학습-불량-{i + 1}"));
                }
                targetIds.Add(AddNamedRoi(250.0, 630.0, 110, 90, 1.7, "테스트-정상(Good)"));
                targetIds.Add(AddNamedRoi(600.0, 630.0, 110, 90, -1.1, "테스트-등록불량(Defect)"));
                targetIds.Add(AddNamedRoi(950.0, 630.0, 110, 90, 0.6, "테스트-미등록이상치(OOD)"));
                break;

            case InspectionSuiteAlgorithmKind.PatchAnomaly:
                double[] padimX = [200.2, 460.3, 720.1, 980.4];
                for (int i = 0; i < padimX.Length; i++)
                {
                    normalRefIds.Add(AddNamedRoi(padimX[i], 230.2, 132, 132, 0.0, $"기준-정상BGA-{i + 1}"));
                }
                targetIds.Add(AddNamedRoi(250.2, 570.2, 132, 132, 0.0, "검사-정상BGA"));
                targetIds.Add(AddNamedRoi(600.2, 570.2, 132, 132, 0.0, "검사-볼누락불량"));
                targetIds.Add(AddNamedRoi(950.2, 570.2, 132, 132, 0.0, "검사-솔더이물불량"));
                break;

            case InspectionSuiteAlgorithmKind.Calibration:
                targetIds.Add(AddNamedRoi(600.0, 400.0, 760, 560, 0.0, "9-Point 핸드아이 교정 플레이트"));
                break;

            case InspectionSuiteAlgorithmKind.CornerAlignment:
                targetIds.Add(AddNamedRoi(328.0, 228.0, 220, 220, 0.0, "코너 1 · 좌상단(TL) 모따기 가상코너"));
                targetIds.Add(AddNamedRoi(888.0, 252.0, 220, 220, 90.0, "코너 2 · 우상단(TR) 모따기 가상코너"));
                targetIds.Add(AddNamedRoi(872.0, 572.0, 220, 220, 180.0, "코너 3 · 우하단(BR) 모따기 가상코너"));
                targetIds.Add(AddNamedRoi(312.0, 548.0, 220, 220, -90.0, "코너 4 · 좌하단(BL) 모따기 가상코너"));
                break;

            case InspectionSuiteAlgorithmKind.PpOcrOnnx:
                targetIds.Add(AddNamedRoi(600.0, 260.0, 420, 64, 0.0, "레이저 연속 각인 (LOT-2026-A09)"));
                targetIds.Add(AddNamedRoi(600.0, 520.0, 460, 64, 0.0, "도트 매트릭스 잉크젯 (EXP.2027.11.24)"));
                break;

            case InspectionSuiteAlgorithmKind.AutoTeaching:
            {
                Guid p1 = AddNamedRoi(280.4, 319.8, 200, 200, 23.7, "Part#1 브래킷 (+하위 ROI 2개 자동생성)");
                Guid p2 = AddNamedRoi(660.4, 340.2, 200, 200, -41.3, "Part#2 커넥터 (+하위 ROI 2개 자동생성)");
                Guid p3 = AddNamedRoi(979.6, 540.3, 200, 200, 112.4, "Part#3 실드캔 (+하위 ROI 1개 자동생성)");
                normalRefIds.Add(p1);
                normalRefIds.Add(p2);
                normalRefIds.Add(p3);
                targetIds.Add(p1);
                targetIds.Add(p2);
                targetIds.Add(p3);
                break;
            }

            case InspectionSuiteAlgorithmKind.PreprocessingShadingAndRecon:
                targetIds.Add(AddNamedRoi(250.0, 400.0, 240, 320, 0.0, "전처리 1 · 2D 다항식 조명 평탄화", new(ShadingVariant: ShadingFilterVariant.PolynomialShadingAndReconstruction)));
                targetIds.Add(AddNamedRoi(600.0, 400.0, 220, 220, 0.0, "전처리 2 · 비등방성 확산(Diffusion)", new(ShadingVariant: ShadingFilterVariant.AnisotropicDiffusion)));
                targetIds.Add(AddNamedRoi(950.0, 400.0, 240, 240, 0.0, "전처리 3 · 형태학적 재구성(Recon)", new(ShadingVariant: ShadingFilterVariant.MorphologicalReconstruction)));
                break;

            case InspectionSuiteAlgorithmKind.AdvancedMetrologyEllipse:
                targetIds.Add(AddNamedRoi(300.35, 400.25, 250, 220, 0.0, "복합 캘리퍼 (외곽 간섭에지 억제)", new(AdvancedMetrology: AdvancedMetrologyVariant.CompositeCaliper)));
                targetIds.Add(AddNamedRoi(640.40, 400.30, 250, 190, 22.5, "Fitzgibbon 타원 1 (22.5° 회전)", new(AdvancedMetrology: AdvancedMetrologyVariant.FitzgibbonEllipse)));
                targetIds.Add(AddNamedRoi(980.25, 400.60, 220, 170, -31.0, "Fitzgibbon 타원 2 (-31.0° 회전)", new(AdvancedMetrology: AdvancedMetrologyVariant.FitzgibbonEllipse)));
                break;

            case InspectionSuiteAlgorithmKind.AdvancedBlobWatershed:
                targetIds.Add(AddNamedRoi(260.0, 400.0, 220, 190, 0.0, "고급형상 (Euler 홀2개·MinAreaBox)", new(AdvancedBlob: AdvancedBlobVariant.TopologyAndMinAreaBox)));
                targetIds.Add(AddNamedRoi(610.0, 400.0, 220, 190, 0.0, "워터셰드 분리 1 (2접촉 솔더볼)", new(AdvancedBlob: AdvancedBlobVariant.WatershedSplit)));
                targetIds.Add(AddNamedRoi(950.0, 400.0, 210, 190, 0.0, "워터셰드 분리 2 (3접촉 입자군집)", new(AdvancedBlob: AdvancedBlobVariant.WatershedSplit)));
                break;

            case InspectionSuiteAlgorithmKind.DataMatrix2DReader:
                targetIds.Add(AddNamedRoi(324.0, 384.0, 210, 210, 0.0, "DataMatrix 1 (LOT26A · 0°)"));
                targetIds.Add(AddNamedRoi(844.0, 384.0, 210, 210, 0.0, "DataMatrix 2 (OK9912 · 0°)"));
                targetIds.Add(AddNamedRoi(584.0, 656.0, 196, 196, 0.0, "DataMatrix 3 (ROT23 · +22.5° 회전)"));
                break;

            case InspectionSuiteAlgorithmKind.PreprocessingFftAndGabor:
                targetIds.Add(AddNamedRoi(210.0, 405.0, 240, 280, 0.0, "전처리 1 · 2D FFT 모아레 노치 감쇠", new(FrequencyFilter: FrequencyFilterVariant.FftPeriodicNotch)));
                targetIds.Add(AddNamedRoi(610.0, 400.0, 240, 240, 0.0, "전처리 2 · 호모모픽(Homomorphic) 조명 분리", new(FrequencyFilter: FrequencyFilterVariant.HomomorphicIllumination)));
                targetIds.Add(AddNamedRoi(980.0, 400.0, 240, 240, 30.0, "전처리 3 · 2D Gabor 방향성 텍스처", new(FrequencyFilter: FrequencyFilterVariant.GaborTexture)));
                break;

            case InspectionSuiteAlgorithmKind.Barcode1DReader:
                targetIds.Add(AddNamedRoi(310.0, 385.0, 460, 220, 0.0, "1D 바코드 1 (LOT26A · 0°)"));
                targetIds.Add(AddNamedRoi(855.0, 385.0, 390, 220, 0.0, "1D 바코드 2 (OK99 · 0°)"));
                targetIds.Add(AddNamedRoi(600.0, 648.0, 360, 210, 0.0, "1D 바코드 3 (ANG18 · -18.0° 회전)"));
                break;

            case InspectionSuiteAlgorithmKind.PhotometricStereo:
                targetIds.Add(AddNamedRoi(330.0, 400.0, 320, 320, 0.0, "포토메트릭 1 · 2D얼룩만 있는 정상표면(OK)"));
                targetIds.Add(AddNamedRoi(870.0, 400.0, 320, 320, 0.0, "포토메트릭 2 · 3D 미세찍힘 요철결함(NG)"));
                break;

            case InspectionSuiteAlgorithmKind.GeometricTolerancing:
                targetIds.Add(AddNamedRoi(320.0, 380.0, 350, 350, 0.0, "GD&T 부품 1 · 직교90° & 동심보어 정상(OK)"));
                targetIds.Add(AddNamedRoi(840.0, 380.0, 350, 350, 0.0, "GD&T 부품 2 · 경사데이텀 & 편심보어 불량(NG)"));
                break;

            case InspectionSuiteAlgorithmKind.QrCode2DReader:
                targetIds.Add(AddNamedRoi(306.0, 326.0, 224, 224, 0.0, "QR 코드 1 (QR26A · 0.0°)"));
                targetIds.Add(AddNamedRoi(860.0, 326.0, 224, 224, 0.0, "QR 코드 2 (ROT-325 · -32.5° 회전)"));
                targetIds.Add(AddNamedRoi(584.0, 620.0, 216, 216, 0.0, "QR 코드 3 (INV135 · +135.0° 회전)"));
                break;
        }

        if (targetIds.Count > 0)
        {
            Session.Select(targetIds);
        }

        FitImage();

        if (Inspections.Suite is { } suite)
        {
            Inspections.Selected = InspectionToolKind.InspectionSuite;
            suite.SelectedAlgorithm = kind;
            Inspections.Update(Session.State, Images.Generation, false, true);
            foreach (var kv in explicitModes)
            {
                suite.SetRoiExecutionMode(kv.Key, kv.Value);
            }

            Inspections.RecordPresetSuiteAssignments(Session.State, targetIds, kind);
            suite.RegisterPresetReferenceRois(normalRefIds, defectRefIds);

            if (suite.ExecuteSelected.CanExecute(null))
            {
                await suite.ExecuteSelected.ExecuteAsync();
            }
        }

        Status = $"{sampleTitle} 및 전용 ROI {Session.State.RoiCount}개를 불러왔습니다.";
    }

    /// <summary>동기 명령을 만들어 <see cref="RefreshCommands"/> 목록에 넣는다.</summary>
    /// <param name="navigation">true면 편집 잠금과 무관하게 화면 이동만 허용한다.</param>
    private RelayCommand Command(Action action, Func<bool>? available = null, bool navigation = false)
    {
        var command = new RelayCommand(() => RunGuarded(action), () => (navigation ? CanNavigate : CanEdit) && !Input.IsDragging && (available?.Invoke() ?? true));
        _syncCommands.Add(command);
        return command;
    }

    /// <summary>비동기 명령을 만들어 <see cref="RefreshCommands"/> 목록에 넣는다. 실패는 상태 표시줄로 보낸다.</summary>
    private AsyncCommand Async(Func<Task> action, Func<bool>? available = null)
    {
        var command = new AsyncCommand(action, ReportError, () => CanEdit && !Input.IsDragging && (available?.Invoke() ?? true));
        _asyncCommands.Add(command);
        return command;
    }
}
