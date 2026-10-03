using Glossa.Core.Companions;

namespace Glossa.Tests.Companions;

public sealed class HeadTests
{
    private const int W = 200, H = 220;

    private static byte[] Figure()
    {
        var a = new byte[W * H];
        void Fill(int x0, int y0, int x1, int y1)
        {
            for (var y = y0; y <= y1; y++)
                for (var x = x0; x <= x1; x++)
                    a[y * W + x] = 255;
        }
        Fill(85, 30, 115, 60);   // the head, its middle at 100
        Fill(70, 61, 130, 200);  // the body
        Fill(40, 5, 43, 120);    // a raised staff, higher than the head
        Fill(118, 10, 120, 28);  // a thin ornament over the hair, beside the head
        return a;
    }

    [Fact]
    public void The_head_is_the_dense_blob_over_the_torso_not_the_highest_thing_held()
    {
        var head = CompanionHead.Find(Figure(), W, H);

        Assert.NotNull(head);
        Assert.InRange(head.Value.X, 98, 102);
        Assert.Equal(30, head.Value.Y);
    }

    [Fact]
    public void An_empty_or_short_frame_has_no_head()
    {
        Assert.Null(CompanionHead.Find(new byte[W * H], W, H));
        Assert.Null(CompanionHead.Find(new byte[10], W, H));
    }
}
