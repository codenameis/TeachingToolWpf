using TeachingToolWpf.Presentation;
using VisionCanvas.Controls;
using VisionCanvas.Layers;

namespace TeachingToolWpf.Controls;

/// <summary>표시 설정을 소유 Canvas의 가이드 레이어에 연결한다. ROI 문서에는 구독하지 않는다.</summary>
internal sealed class CrosshairLayerBinding : IDisposable
{
    private readonly CrosshairSettingsViewModel _settings;
    private readonly DrawingLayer _layer;
    private readonly Guid _id;
    private ImageCrosshairGraphic _graphic;

    internal CrosshairLayerBinding(Dx12Viewport viewport, CrosshairSettingsViewModel settings)
    {
        _settings = settings;
        _layer = viewport.Layers.Create("Guide", LayerSpace.Image);
        _layer.IsVisible = settings.IsVisible;
        _graphic = settings.Graphic;
        _id = _layer.Add(_graphic);
        settings.Changed += Refresh;
    }

    private void Refresh()
    {
        if (_graphic != _settings.Graphic)
        {
            _graphic = _settings.Graphic;
            _layer.Update(_id, _graphic);
        }
        _layer.IsVisible = _settings.IsVisible;
    }

    public void Dispose() => _settings.Changed -= Refresh;
}
