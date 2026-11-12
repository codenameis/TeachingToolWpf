using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Interaction;
using VisionCanvas.Presentation;
using VisionCanvas.SampleShell.Services;
using VisionCanvas.SampleShell.ViewModels;

namespace VisionCanvas.VulkanSample.ViewModels;

public sealed class EditorViewModel(
    EditorSession editor,
    InteractionController input,
    ViewportLayout layout,
    ImageCoordinator images,
    IImageFilePicker picker)
    : EditorViewModelBase(editor, input, layout, images, picker, rendererLabel: "Vulkan");
