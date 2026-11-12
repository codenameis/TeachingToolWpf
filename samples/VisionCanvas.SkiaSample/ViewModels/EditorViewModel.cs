using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Interaction;
using VisionCanvas.Presentation;
using VisionCanvas.SampleShell.Services;
using VisionCanvas.SampleShell.ViewModels;

namespace VisionCanvas.SkiaSample.ViewModels;

public sealed class EditorViewModel(
    EditorSession editor,
    InteractionController input,
    ViewportLayout layout,
    ImageCoordinator images,
    IImageFilePicker picker)
    : EditorViewModelBase(editor, input, layout, images, picker, rendererLabel: "Skia", initialRendererStatus: "Skia 준비 중…");
