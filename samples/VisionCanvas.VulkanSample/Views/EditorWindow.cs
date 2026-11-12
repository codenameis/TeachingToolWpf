using VisionCanvas.SampleShell.Views;
using VisionCanvas.Vulkan.Controls;

namespace VisionCanvas.VulkanSample.Views;

public sealed class EditorWindow : SampleEditorWindowBase
{
    public EditorWindow()
        : this(new VulkanViewport())
    {
    }

    private EditorWindow(VulkanViewport viewport)
        : base("VisionCanvas · Vulkan", viewport)
    {
        Viewport = viewport;
    }

    public VulkanViewport Viewport { get; }
}
