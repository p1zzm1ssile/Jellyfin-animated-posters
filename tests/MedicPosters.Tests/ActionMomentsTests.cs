using Jellyfin.Plugin.MedicPosters.Posters;

namespace MedicPosters.Tests;

/// <summary>Finding the busiest spots of a video, for "Prefer action and fight scenes".</summary>
public class ActionMomentsTests
{
    [Fact]
    public void Motion_is_the_average_of_the_printed_changes()
    {
        const string printed = "frame:0    pts:0       pts_time:0\nlavfi.signalstats.YDIF=0\n"
            + "frame:1    pts:1       pts_time:0.166667\nlavfi.signalstats.YDIF=12.5\r\n"
            + "frame:2    pts:2       pts_time:0.333333\nlavfi.signalstats.YDIF=7.5\n";
        Assert.Equal(20.0 / 3, PosterMaker.Motion(printed), 6);
    }

    [Theory]
    [InlineData("")]
    [InlineData("frame:0 pts:0 pts_time:0\nlavfi.signalstats.YDIF=nan\n")]
    public void Motion_is_0_when_nothing_was_measured(string printed)
    {
        Assert.Equal(0, PosterMaker.Motion(printed));
    }

    [Fact]
    public void The_busiest_starts_are_kept_in_video_order()
    {
        var scored = new List<(double Start, double Motion)> { (100, 2), (200, 30), (300, 5), (400, 25), (500, 1) };
        Assert.Equal(new[] { 200.0, 400.0 }, PosterMaker.BusiestStarts(scored, 2));
    }

    [Fact]
    public void Motion_is_measured_on_small_frames_from_the_start()
    {
        var args = PosterMaker.MotionArgs(new Clip("/films/a.mkv", 61.5, false, 5400), 1.5, "motion.txt");
        Assert.Equal("61.5", args[args.ToList().IndexOf("-ss") + 1]);
        Assert.Equal("1.5", args[args.ToList().IndexOf("-t") + 1]);
        Assert.Contains(args, a => a.Contains("signalstats") && a.Contains("file=motion.txt"));
        Assert.Equal("-", args[^1]);
    }
}
