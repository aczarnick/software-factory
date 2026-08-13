using System.Diagnostics;
using System.Text;

namespace Factory.Runtime;

public readonly record struct ShellResult(int ExitCode, string Stdout, string Stderr, bool TimedOut)
{
    public bool Ok => ExitCode == 0 && !TimedOut;
    public string Combined => string.Join("\n", new[] { Stdout, Stderr }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

public static class Shell
{
    public static async Task<ShellResult> RunAsync(
        string command, string workingDirectory, int timeoutSeconds = 300, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(command);

        return await ExecAsync(psi, timeoutSeconds, ct).ConfigureAwait(false);
    }

    /// <summary>Whether a tool is on PATH. Deliberately synchronous: this is called during
    /// toolchain detection, and blocking on an async call there risks deadlocking a
    /// saturated thread pool for what is a five-millisecond question.</summary>
    public static bool Which(string tool)
    {
        try
        {
            var psi = new ProcessStartInfo("/bin/sh")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add($"command -v {tool}");

            using var proc = Process.Start(psi);
            if (proc is null) return false;

            proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();

            if (proc.WaitForExit(5000)) return proc.ExitCode == 0;

            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return false;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or SystemException)
        {
            return false;
        }
    }

    public static async Task<ShellResult> GitAsync(
        string workingDirectory, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return await ExecAsync(psi, 120, ct).ConfigureAwait(false);
    }

    private static async Task<ShellResult> ExecAsync(ProcessStartInfo psi, int timeoutSeconds, CancellationToken ct)
    {
        using var proc = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        try { proc.Start(); }
        catch (Exception ex) { return new ShellResult(127, "", ex.Message, false); }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var outTask = ReadAsync(proc.StandardOutput, stdout, cts.Token);
        var errTask = ReadAsync(proc.StandardError, stderr, cts.Token);

        try
        {
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            await Task.WhenAll(outTask, errTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* gone */ }
            return new ShellResult(124, stdout.ToString(), stderr.ToString(), TimedOut: true);
        }

        return new ShellResult(proc.ExitCode, stdout.ToString(), stderr.ToString(), false);
    }

    private static async Task ReadAsync(StreamReader reader, StringBuilder sink, CancellationToken ct)
    {
        try
        {
            var buffer = new char[4096];
            int n;
            while ((n = await reader.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                // Bound retained output: verification only needs the tail to explain a failure.
                if (sink.Length < 64_000) sink.Append(buffer, 0, n);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }
}
