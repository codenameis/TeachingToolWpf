using System.Collections.Immutable;
using System.IO;
using System.Windows.Media;
using TeachingToolWpf.Commands;
using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.Infrastructure;
using TeachingToolWpf.Services;
using TeachingToolWpf.ViewModels;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionGeometry;

namespace TeachingToolWpf.Models;

/// <summary>패턴 매칭 리스트에 등록된 개별 패턴 템플릿 항목(모델 스냅샷, 썸네일 이미지, 외부 파일 보관 상태 및 정상/불량 분류)을 관리한다.</summary>
public sealed class RegisteredPatternItem : ObservableObject, IDisposable
{
    private const double BytesPerKilobyte = 1024.0;

    private int _index;
    private string _name;
    private PatternRegistrationRole _role;
    private bool _isEnabled = true;
    private bool _isMaskDirty;
    private RectifiedPatternPatch _previewPatch;
    private ImageSource? _thumbnailImage;
    private string? _imageFilePath;
    private long _diskSizeBytes;
    private bool _disposed;

    internal RegisteredPatternItem(
        Guid id,
        int index,
        string name,
        PatternMatchingMethod method,
        PatternRegistrationRole role,
        Guid? teachingRoiId,
        string? teachingRoiName,
        RoiGeometry teachingRegion,
        ImmutableArray<MaskRegion> teachingMasks,
        string summary,
        RectifiedPatternPatch previewPatch = default,
        Action<RegisteredPatternItem>? onToggleRole = null,
        Action<RegisteredPatternItem>? onRemove = null,
        ShapeModel? shapeModel = null,
        ShapeTrainingSettings? shapeTrainedSettings = null,
        PatMaxTrainedTemplate? patMaxTemplate = null,
        PatMaxUiSettings? patMaxSettings = null,
        CpuTemplate? cpuTemplate = null,
        CorrelationTemplate? templateModel = null)
    {
        Id = id;
        _index = index;
        _name = name;
        Method = method;
        _role = role;
        TeachingRoiId = teachingRoiId;
        TeachingRoiName = teachingRoiName;
        TeachingRegion = teachingRegion;
        TeachingMasks = teachingMasks.IsDefault ? [] : teachingMasks;
        Summary = summary;
        ShapeModel = shapeModel;
        ShapeTrainedSettings = shapeTrainedSettings;
        PatMaxTemplate = patMaxTemplate;
        PatMaxSettings = patMaxSettings;
        CpuTemplate = cpuTemplate;
        TemplateModel = templateModel;

        _previewPatch = previewPatch.IsEmpty
            ? PatternThumbnailRenderer.ResolveOrSynthesizePatch(
                rawGray8: null,
                rawWidth: 0,
                rawHeight: 0,
                teachingRegion: teachingRegion,
                shapeModel: shapeModel,
                patMaxTemplate: patMaxTemplate,
                cpuTemplate: cpuTemplate,
                templateModel: templateModel)
            : previewPatch;

        if (!_previewPatch.IsEmpty)
        {
            _thumbnailImage = PatternThumbnailRenderer.CreateFrozenThumbnail(_previewPatch);
        }

        ToggleRoleCommand = new RelayCommand(() =>
        {
            if (onToggleRole is not null)
            {
                onToggleRole(this);
            }
            else
            {
                Role = IsDefect ? PatternRegistrationRole.Good : PatternRegistrationRole.Defect;
            }
        });
        RemoveCommand = new RelayCommand(() => onRemove?.Invoke(this));
    }

    public event Action? StateChanged;

    public RelayCommand ToggleRoleCommand { get; }
    public RelayCommand RemoveCommand { get; }

    public Guid Id { get; }

    public int Index
    {
        get => _index;
        internal set
        {
            if (Set(ref _index, value))
            {
                Notify(nameof(DisplayTitle));
            }
        }
    }

    public string Name
    {
        get => _name;
        set
        {
            if (Set(ref _name, string.IsNullOrWhiteSpace(value) ? $"패턴-{_index}" : value.Trim()))
            {
                Notify(nameof(DisplayTitle));
                StateChanged?.Invoke();
            }
        }
    }

    public PatternMatchingMethod Method { get; }

    public PatternRegistrationRole Role
    {
        get => _role;
        set
        {
            if (Set(ref _role, value))
            {
                Notify(nameof(IsGood));
                Notify(nameof(IsDefect));
                Notify(nameof(RoleBadge));
                Notify(nameof(RoleShortBadge));
                Notify(nameof(RoleAccent));
                Notify(nameof(RoleBackground));
                Notify(nameof(DisplayTitle));
                StateChanged?.Invoke();
            }
        }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (Set(ref _isEnabled, value))
            {
                StateChanged?.Invoke();
            }
        }
    }

    public bool IsMaskDirty
    {
        get => _isMaskDirty;
        internal set
        {
            if (Set(ref _isMaskDirty, value))
            {
                Notify(nameof(SummaryText));
            }
        }
    }

    public Guid? TeachingRoiId { get; }
    public string? TeachingRoiName { get; }
    public RoiGeometry TeachingRegion { get; }
    public ImmutableArray<MaskRegion> TeachingMasks { get; }
    public string Summary { get; }

    public ShapeModel? ShapeModel { get; }
    internal ShapeTrainingSettings? ShapeTrainedSettings { get; }
    public PatMaxTrainedTemplate? PatMaxTemplate { get; }
    internal PatMaxUiSettings? PatMaxSettings { get; }
    public CpuTemplate? CpuTemplate { get; }
    public CorrelationTemplate? TemplateModel { get; }

    public RectifiedPatternPatch PreviewPatch => _previewPatch;
    public int PreviewWidth => _previewPatch.Width;
    public int PreviewHeight => _previewPatch.Height;
    public ImageSource? ThumbnailImage => _thumbnailImage;
    public bool HasThumbnail => _thumbnailImage is not null;
    public string? ImageFilePath => _imageFilePath;
    public long DiskSizeBytes => _diskSizeBytes;
    public bool IsBackedByFile => !string.IsNullOrWhiteSpace(_imageFilePath) && File.Exists(_imageFilePath);

    public bool IsGood => _role == PatternRegistrationRole.Good;
    public bool IsDefect => _role == PatternRegistrationRole.Defect;
    public string RoleBadge => IsDefect ? "불량 (NG)" : "정상 (OK)";
    public string RoleShortBadge => IsDefect ? "NG·불량" : "OK·정상";
    public string RoleAccent => IsDefect ? "#EF4444" : "#10B981";
    public string RoleBackground => IsDefect ? "#FEF2F2" : "#ECFDF5";
    public string MethodBadge => RoiAlgorithmAssignment.FormatAlgorithmBadge(InspectionToolKind.Pattern, Method);
    public string DisplayTitle => $"#{_index} [{(IsDefect ? "불량" : "정상")}] {_name}";
    public string SummaryText => _isMaskDirty ? $"{Summary} · (마스크 변경됨: 재학습 권장)" : Summary;
    public string ImageDimensionsText => $"{PreviewWidth}×{PreviewHeight} px";

    public string StorageStatusText => !string.IsNullOrWhiteSpace(_imageFilePath)
        ? $"병렬 파일 캐시 · {Path.GetFileName(_imageFilePath)} ({_diskSizeBytes / BytesPerKilobyte:F1} KB)"
        : $"메모리 패치 · {PreviewWidth}×{PreviewHeight} px";

    internal void MarkBackedByFile(string filePath, long diskSizeBytes)
    {
        _imageFilePath = filePath;
        _diskSizeBytes = diskSizeBytes;
        Notify(nameof(ImageFilePath));
        Notify(nameof(DiskSizeBytes));
        Notify(nameof(IsBackedByFile));
        Notify(nameof(StorageStatusText));
    }

    internal void ApplyLoadedPatch(
        RectifiedPatternPatch patch,
        ImageSource thumbnail,
        string filePath,
        long diskSizeBytes)
    {
        _previewPatch = patch;
        _thumbnailImage = thumbnail;
        _imageFilePath = filePath;
        _diskSizeBytes = diskSizeBytes;
        Notify(nameof(PreviewPatch));
        Notify(nameof(PreviewWidth));
        Notify(nameof(PreviewHeight));
        Notify(nameof(ThumbnailImage));
        Notify(nameof(HasThumbnail));
        Notify(nameof(ImageFilePath));
        Notify(nameof(DiskSizeBytes));
        Notify(nameof(IsBackedByFile));
        Notify(nameof(ImageDimensionsText));
        Notify(nameof(StorageStatusText));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        PatMaxTemplate?.Dispose();
    }
}
