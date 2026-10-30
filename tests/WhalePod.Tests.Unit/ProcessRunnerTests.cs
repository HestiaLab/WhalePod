using System.Diagnostics;

using WhalePod.DeviceCli;

namespace WhalePod.Tests.Unit;

public sealed class ProcessRunnerTests
{
    [Fact]
    public void Hanging_process_is_killed_at_timeout()
    {
        var (fileName, arguments) = OperatingSystem.IsWindows()
            ? ("cmd.exe", "/c timeout /t 30 /nobreak > nul")
            : ("sleep", "30");
        var stopwatch = Stopwatch.StartNew();
        var output = ProcessRunner.Run(fileName, arguments, TimeSpan.FromSeconds(1));
        stopwatch.Stop();

        Assert.Null(output);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"process runner took {stopwatch.Elapsed}");
    }

    [Fact]
    public void Large_stderr_is_drained_without_deadlock()
    {
        var (fileName, arguments) = OperatingSystem.IsWindows()
            ? ("cmd.exe", "/c (for /l %i in (1,1,5000) do @echo 1234567890 1>&2) & echo done")
            : ("sh", "-c \"for i in $(seq 1 5000); do echo 1234567890 >&2; done; echo done\"");
        var output = ProcessRunner.Run(fileName, arguments, TimeSpan.FromSeconds(15));

        Assert.Equal("done", output);
    }
}