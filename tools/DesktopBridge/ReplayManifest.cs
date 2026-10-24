namespace MotionVfx;

internal sealed record ReplayFrame(string Path, double Time);
internal sealed record ReplayManifest(string Id, int Fps, int Width, int Height, double Duration,
    string OutputDirectory, string ManifestPath, IReadOnlyList<ReplayFrame> Frames)
{
    internal static ReplayManifest Create(string id, int fps, int width, int height, string directory,
        IReadOnlyList<ReplayFrame> frames)
    {
        if (frames.Count == 0) throw new InvalidOperationException("没有录到游戏画面；请保持游戏在前台后重试。");
        if (fps <= 0 || width < 1 || height < 1) throw new InvalidOperationException("录制画面尺寸或帧率无效。");
        double first = frames[0].Time, previous = first;
        foreach (var frame in frames)
        {
            if (!double.IsFinite(frame.Time) || frame.Time < previous || string.IsNullOrWhiteSpace(frame.Path))
                throw new InvalidOperationException("录制帧的路径或时间无效。");
            previous = frame.Time;
        }
        // Time before the first valid image and time spent outside the game are not footage.
        var normalized = frames.Select(f => new ReplayFrame(f.Path, f.Time - first)).ToArray();
        string output = Path.GetFullPath(directory);
        return new ReplayManifest(id, fps, width, height, normalized[^1].Time + 1.0 / fps,
            output, Path.Combine(output, "replay.json"), normalized);
    }
}
