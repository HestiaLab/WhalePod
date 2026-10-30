using System.Diagnostics;

namespace WhalePod.DeviceCli;

public static class ProcessRunner
{
    public static string? Run(string fileName, string arguments, TimeSpan timeout)
    {
        try
        {
            var startInfo = new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit((int)Math.Max(1, timeout.TotalMilliseconds)))
            {
                TryKill(process);
                stdout.Wait(TimeSpan.FromSeconds(1));
                stderr.Wait(TimeSpan.FromSeconds(1));
                return null;
            }

            if (!stdout.Wait(TimeSpan.FromSeconds(2)) || !stderr.Wait(TimeSpan.FromSeconds(2)))
            {
                return null;
            }

            return process.ExitCode == 0 ? stdout.Result.Trim() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
        }
    }
}