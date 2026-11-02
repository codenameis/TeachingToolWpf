using System.Runtime.ExceptionServices;
using VisionCanvas.Controls;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Interaction;
using VisionCanvas.Imaging;
using VisionCanvas.Rendering;
using VisionGeometry;

namespace VisionCanvas.Tests;

public sealed class ViewportInputLockTests
{
    [Fact]
    public void DisabledViewportBlocksWheelAndKeysAndCancelsCapturedGesture()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var editor = new EditorSession();
                editor.Add(new(new(100, 100), 50, 50, 0));
                var input = new InteractionController(editor);
                using var viewport = new TestViewport { PointerInput = input };
                var keys = 0;
                viewport.KeyAction = (_, _, _) => { keys++; return true; };
                var before = editor.State;
                input.Down(new(100, 100));
                input.Move(new(120, 100));
                viewport.IsInputEnabled = false;
                Assert.False(input.IsDragging);
                Assert.Equal(before, editor.State);
                Assert.True(viewport.Send(0x0100, 0x2E, 0));
                Assert.True(viewport.Send(0x020A, 120 << 16, 0));
                Assert.Equal(0, keys);
                Assert.Equal(before, editor.State);
            }
            catch (Exception error)
            {
                failure = error;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private sealed class TestViewport : ViewportControlBase
    {
        protected override IRenderWorker CreateRenderer(
            nint handle,
            TileWorker tiles,
            ITextRasterizer rasterizer,
            LatestFrameMailbox frames,
            IFramePreparer preparer)
            => throw new NotSupportedException();

        public bool Send(int message, nint parameter, nint position)
        {
            var handled = false;
            WndProc(0, message, parameter, position, ref handled);
            return handled;
        }
    }
}
