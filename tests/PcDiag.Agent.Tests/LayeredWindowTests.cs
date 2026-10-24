using System.Drawing;
using PcDiag.Agent.Overlay;

namespace PcDiag.Agent.Tests;

public sealed class LayeredWindowTests
{
    [Fact]
    public void Panels_over_each_other_are_stacked_in_one_go_and_keep_their_order()
    {
        // Three panels over each other (invisible: fully transparent pixels).
        var windows = Enumerable.Range(0, 3).Select(_ => new LayeredWindow(hideFromCapture: true)).ToList();
        try
        {
            using var bitmap = new Bitmap(4, 4);
            foreach (var window in windows)
            {
                window.Show(bitmap, new Point(20, 20), 255);
            }

            var (front, middle, back) = (windows[2], windows[0], windows[1]);
            LayeredWindow.Stack([front, middle, back]);
            AssertOrder(front, middle, back);

            // Raised again: nothing moves (the order is already right, each under the one before it).
            LayeredWindow.Stack([front, middle, back]);
            AssertOrder(front, middle, back);

            // The panel at the back raised over the others (as raising each in turn did): put back under them.
            back.KeepOnTop();
            front.WindowAbove.ShouldNotBe(0);
            LayeredWindow.Stack([front, middle, back]);
            AssertOrder(front, middle, back);
        }
        finally
        {
            foreach (var window in windows)
            {
                window.Dispose();
            }
        }
    }

    private static void AssertOrder(LayeredWindow front, LayeredWindow middle, LayeredWindow back)
    {
        middle.WindowAbove.ShouldBe(front.Handle);
        back.WindowAbove.ShouldBe(middle.Handle);
    }
}
