using System.Numerics;
using VisionCanvas.Core.Geometry;
using VisionGeometry;

namespace VisionCanvas.Rendering;

internal static class MinimapPainter
{
    public static void Draw(SpriteMesh mesh, RenderScene scene, GpuTexture white, GpuTexture? overview, Func<string, GpuTexture> label)
    {
        if (scene.Document.Image is not { } image || MinimapLayout.Create(image.Width, image.Height, scene.Width, scene.Height) is not { } map)
        {
            return;
        }

        var accent = new Vector4(.22f, .52f, 1, 1);
        mesh.Rectangle(white, new(map.Panel.Left - 2, map.Panel.Top - 2, map.Panel.Right + 2, map.Panel.Bottom + 2), new(0, 0, 0, .35f));
        mesh.Rectangle(white, map.Panel, new(.10f, .13f, .18f, .98f));
        Outline(map.Panel, 1, new(.38f, .44f, .55f, 1));
        var title = label("미니맵 · 클릭/드래그 이동");
        mesh.Rectangle(title, new(map.Panel.Left + 10, map.Panel.Top + 7, map.Panel.Left + 10 + (title.Width / scene.Dpi), map.Panel.Top + 7 + (title.Height / scene.Dpi)), Vector4.One);
        mesh.Rectangle(white, map.ImageBounds, new(.04f, .05f, .07f, 1));
        if (overview is not null)
        {
            mesh.Rectangle(overview, map.ImageBounds, Vector4.One);
        }

        var visible = map.VisibleBounds(scene.Document.Viewport);
        // Keep a subpixel/off-image viewport locatable without changing the navigation geometry.
        var box = new BoundsD(Math.Max(map.ImageBounds.Left, visible.Center.X - Math.Max(3, visible.Width / 2)),
            Math.Max(map.ImageBounds.Top, visible.Center.Y - Math.Max(3, visible.Height / 2)),
            Math.Min(map.ImageBounds.Right, visible.Center.X + Math.Max(3, visible.Width / 2)),
            Math.Min(map.ImageBounds.Bottom, visible.Center.Y + Math.Max(3, visible.Height / 2)));
        mesh.Rectangle(white, box, accent with
        {
            W = .15f
        });
        Outline(box, 3, new(0, 0, 0, .7f));
        Outline(box, 1.5, accent);
        void Outline(BoundsD b, double thickness, Vector4 color) => mesh.Outline(white,
            [new(b.Left, b.Top), new(b.Right, b.Top), new(b.Right, b.Bottom), new(b.Left, b.Bottom)], thickness, color);
    }
}
