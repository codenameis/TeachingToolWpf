using TeachingToolWpf.ViewModels;
using VisionCanvas.Controls;
using VisionCanvas.Layers;

namespace TeachingToolWpf.Controls;

/// <summary>표시 설정을 소유 Canvas의 가이드 레이어에 연결한다. ROI 문서에는 구독하지 않는다.</summary>
internal sealed class CrosshairLayerLink : IDisposable
{
    private readonly CrosshairSettingsViewModel _settings;
    private readonly DrawingLayer _guideLayer;
    private readonly Guid _graphicId;
    private ImageCrosshairGraphic _graphic;

    internal CrosshairLayerLink(Dx12Viewport viewport, CrosshairSettingsViewModel settings)
    {
        _settings = settings;
        _guideLayer = viewport.Layers.Create("Guide", LayerSpace.Image);
        _guideLayer.IsVisible = settings.IsVisible;
        _graphic = settings.Graphic;
        _graphicId = _guideLayer.Add(_graphic);
        settings.Changed += Refresh;
    }

    private void Refresh()
    {
        if (_graphic != _settings.Graphic)
        {
            _graphic = _settings.Graphic;
            _guideLayer.Update(_graphicId, _graphic);
        }
        _guideLayer.IsVisible = _settings.IsVisible;
    }

    public void Dispose() => _settings.Changed -= Refresh;
}
