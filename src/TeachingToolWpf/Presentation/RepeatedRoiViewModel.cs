using System.Globalization;
using VisionCanvas.Controls;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Interaction;
using VisionCanvas.Rendering;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Presentation;

public sealed class RepeatedRoiViewModel : ObservableObject
{
    private readonly Dx12Viewport _viewport;
    private ImageInfo? _image;
    private long _generation;
    private string _status = "준비 중…";
    private string _performance = "";
    public RepeatedRoiSession Session
    {
        get;
    }
    public RepeatedRoiInput Input
    {
        get;
    }
    public string CountText => $"{Session.Grid.Count:N0}개 ROI · {Session.Grid.Columns:N0} × {Session.Grid.Rows:N0}";
    public string SelectionText => Session.SelectedIndex is { } index
        ? $"선택: 행 {(index / Session.Grid.Columns) + 1:N0}, 열 {(index % Session.Grid.Columns) + 1:N0} · 수정 {Session.Grid.Overrides.Count:N0}개"
        : $"전체 {Session.Grid.Count:N0}개 선택 · 수정 {Session.Grid.Overrides.Count:N0}개";
    public string Status
    {
        get => _status; set => Set(ref _status, value);
    }
    public string Performance
    {
        get => _performance; set => Set(ref _performance, value);
    }
    public string X { get; set; } = "";
    public string Y { get; set; } = "";
    public string Width { get; set; } = "";
    public string Height { get; set; } = "";
    public string Angle { get; set; } = "";
    public string Row { get; set; } = "5001";
    public string Column { get; set; } = "5001";
    public RelayCommand SelectAll
    {
        get;
    }
    public RelayCommand SelectCell
    {
        get;
    }
    public RelayCommand Fit
    {
        get;
    }
    public RelayCommand Detail
    {
        get;
    }
    public RelayCommand Undo
    {
        get;
    }
    public RelayCommand Redo
    {
        get;
    }
    public RelayCommand RotateLeft
    {
        get;
    }
    public RelayCommand RotateRight
    {
        get;
    }
    public RelayCommand MoveLeft
    {
        get;
    }
    public RelayCommand MoveRight
    {
        get;
    }
    public RelayCommand MoveUp
    {
        get;
    }
    public RelayCommand MoveDown
    {
        get;
    }
    public RelayCommand Apply
    {
        get;
    }
    public RelayCommand Pan
    {
        get;
    }
    public RelayCommand Select
    {
        get;
    }

    public RepeatedRoiViewModel(Dx12Viewport viewport, ImageReference image)
    {
        _viewport = viewport;
        Session = new(new(10001, 10001, Math.Min(image.Width, image.Height) * .45 / 10001,
            new(image.Width / 2d, image.Height / 2d)), image);
        Input = new(Session);
        SelectAll = new(() => Session.Select(null));
        SelectCell = new(() => Guard(() =>
        {
            if (!int.TryParse(Row, out var row) || !int.TryParse(Column, out var column) ||
                row < 1 || row > Session.Grid.Rows || column < 1 || column > Session.Grid.Columns)
            {
                throw new ArgumentException($"행·열은 1~{Session.Grid.Columns} 사이의 정수입니다.");
            }
            Session.Select(((row - 1) * Session.Grid.Columns) + column - 1);
        }));
        Fit = new(() => Session.SetViewport(ViewportTransform.Fit(image.Width, image.Height, viewport.ActualWidth, viewport.ActualHeight)));
        Detail = new(() =>
        {
            var center = Session.SelectionGeometry.Center;
            var scale = Math.Clamp(12 / Session.Grid.Pitch, 1, 128);
            Session.SetViewport(new(scale, new PointD(viewport.ActualWidth / 2, viewport.ActualHeight / 2) - (center * scale)));
        });
        Undo = new(Session.Undo, () => Session.CanUndo);
        Redo = new(Session.Redo, () => Session.CanRedo);
        RotateLeft = new(() => Rotate(-15));
        RotateRight = new(() => Rotate(15));
        MoveLeft = new(() => Move(new(-50 / Session.Viewport.Scale, 0)));
        MoveRight = new(() => Move(new(50 / Session.Viewport.Scale, 0)));
        MoveUp = new(() => Move(new(0, -50 / Session.Viewport.Scale)));
        MoveDown = new(() => Move(new(0, 50 / Session.Viewport.Scale)));
        Apply = new(() => Guard(() => Session.SetGeometry(new(new(Number(X), Number(Y)), Number(Width), Number(Height), Number(Angle)))),
            () => Session.SelectedIndex is not null && !Session.IsPreviewing);
        Pan = new(() =>
        {
            Input.Cancel();
            Input.Tool = EditorTool.Pan;
            Status = "화면 이동 · 드래그";
        });
        Select = new(() =>
        {
            Input.Cancel();
            Input.Tool = EditorTool.Select;
            Status = "선택 · 더블클릭: 개별 ROI · 드래그: 이동 · 원형 핸들: 회전";
        });
        Session.Changed += Changed;
        viewport.LayoutChanged += Publish;
        Changed();
    }

    private static double Number(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) && double.IsFinite(value)
        ? value : throw new ArgumentException("유효한 숫자를 입력하세요.");

    private void Guard(Action action)
    {
        try
        {
            action();
            Status = "적용 완료";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Status = ex.Message;
        }
    }

    public void Move(PointD delta) => Guard(() =>
    {
        Input.Cancel();
        Session.Begin();
        Session.PreviewTranslation(delta);
        Session.Complete();
    });

    public void Rotate(double degrees) => Guard(() =>
    {
        Input.Cancel();
        Session.Begin();
        Session.PreviewRotation(degrees);
        Session.Complete();
    });

    public void SetSource(ITileSource source)
    {
        _image = source.Info;
        _generation = _viewport.SetSource(source);
        Fit.Execute(null);
        Status = "이미지 중심에 생성 완료 · 전체 드래그/회전 · 더블클릭으로 개별 선택 · Esc 취소";
    }

    private void Changed()
    {
        Publish();
        if (!Session.IsPreviewing)
        {
            var geometry = Session.SelectionGeometry;
            X = geometry.Center.X.ToString("G17", CultureInfo.CurrentCulture);
            Y = geometry.Center.Y.ToString("G17", CultureInfo.CurrentCulture);
            Width = geometry.Width.ToString("G17", CultureInfo.CurrentCulture);
            Height = geometry.Height.ToString("G17", CultureInfo.CurrentCulture);
            Angle = geometry.Angle.ToString("G17", CultureInfo.CurrentCulture);
            foreach (var property in new[] { nameof(X), nameof(Y), nameof(Width), nameof(Height), nameof(Angle), nameof(SelectionText) })
            {
                Notify(property);
            }
            Undo.Refresh();
            Redo.Refresh();
            Apply.Refresh();
        }
    }

    public void Publish()
    {
        Input.SetViewportSize(_viewport.ActualWidth, _viewport.ActualHeight);
        _viewport.Submit(new(DocumentState.Empty with
        {
            Image = Session.Image,
            Viewport = Session.Viewport
        }, _image, _generation,
            _viewport.ActualWidth, _viewport.ActualHeight, _viewport.Dpi, new())
        {
            RepeatedGrid = Session.Grid,
            RepeatedSelection = Session.SelectedIndex
        });
    }
}
