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
    /// <summary>How much of a command's output is retained. Verification only needs enough to
    /// explain a failure, but a caller parsing structured output must be able to tell a truncated
    /// capture from a malformed one — <see cref="ShellResult.Stdout"/> at this length may be cut.</summary>
    public const int MaxCapturedOutputChars = 64_000;

    /// <summary>Id of the work item on whose behalf the current async flow is running a shell
    /// command, if any. Set by the orchestrator around an item's processing so a command's
    /// start and completion can be attributed without threading an id through every call site.</summary>
    internal static readonly AsyncLocal<string?> CurrentItemId = new();

    /// <summary>Fired for <see cref="CurrentItemId"/> when a command starts and again when it
    /// finishes. Wired up by the orchestrator to feed per-item progress tracking.</summary>
    internal static Action<string>? OnCommandStarted;
    internal static Action<string>? OnCommandCompleted;

    /// <summary>
    /// Runs a short-lived local command synchronously. Deliberately not async: the storage
    /// ports are synchronous, and sync-over-async on a saturated thread pool deadlocks. Use
    /// only for fast local processes — never for network calls or builds.
    ///
    /// Draining stdout/stderr to end-of-file would block until every inherited grandchild
    /// (e.g. a daemon the command forked into the background) releases the pipe, not until
    /// the command itself exits — see <see cref="DrainGrace"/>. This waits on process exit
    /// alone and then drains for a bounded grace period, the same strategy <see
    /// cref="ExecCoreAsync"/> uses for the async path.
    /// </summary>
    public static ShellResult Run(
        string fileName,
        IEnumerable<string> args,
        string workingDirectory,
        IDictionary<string, string>? environment = null,
        int timeoutSeconds = 60)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (environment is not null)
            foreach (var (key, value) in environment) psi.Environment[key] = value;

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return new ShellResult(127, "", $"could not start {fileName}", false);

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            using var cts = new CancellationTokenSource();

            var outTask = ReadAsync(proc.StandardOutput, stdout, cts.Token);
            var errTask = ReadAsync(proc.StandardError, stderr, cts.Token);

            if (!proc.WaitForExit(timeoutSeconds * 1000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
                cts.Cancel();
                Task.WhenAll(outTask, errTask).Wait();
                return new ShellResult(124, stdout.ToString(), stderr.ToString(), TimedOut: true);
            }

            // Bounded drain: collect whatever output is buffered, but never wait on a pipe an
            // inherited grandchild is still holding open.
            Task.WhenAny(Task.WhenAll(outTask, errTask), Task.Delay(DrainGrace)).Wait();

            // Readers must be fully stopped — cancelled and awaited — before ToString() below;
            // reading the StringBuilder while a reader task might still be appending is a race.
            cts.Cancel();
            Task.WhenAll(outTask, errTask).Wait();

            return new ShellResult(proc.ExitCode, stdout.ToString(), stderr.ToString(), false);
        }
        // Catches every SystemException, not just IOException and InvalidOperationException (both
        // already derive from it, so naming them separately caught nothing the bare type does not).
        // Deliberately this broad: Process.Start reports a missing executable, a denied fork, or a
        // full process table as a plain Win32Exception, which is a SystemException and nothing
        // narrower. The cost is that a bug inside this method's own body -- a NullReferenceException,
        // say -- is caught here too, and surfaces to the caller as an ordinary failure result
        // (a non-zero exit, or false) rather than as an exception.
        catch (SystemException ex)
        {
            return new ShellResult(127, "", ex.Message, false);
        }
    }

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
        // Catches every SystemException, not just IOException and InvalidOperationException (both
        // already derive from it, so naming them separately caught nothing the bare type does not).
        // Deliberately this broad: Process.Start reports a missing executable, a denied fork, or a
        // full process table as a plain Win32Exception, which is a SystemException and nothing
        // narrower. The cost is that a bug inside this method's own body -- a NullReferenceException,
        // say -- is caught here too, and surfaces to the caller as an ordinary failure result
        // (a non-zero exit, or false) rather than as an exception.
        catch (SystemException)
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

    /// <summary>
    /// How long to keep draining output after the command itself has exited.
    ///
    /// Build tools leave daemons behind — MSBuild node reuse, gradle, watchman — and those
    /// children inherit the command's stdout and stderr. Reading to end-of-file therefore
    /// blocks until the *daemon* exits, not until the command finishes, which stalls a build
    /// gate for its entire timeout after the build has already succeeded.
    /// </summary>
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(2);

    private static async Task<ShellResult> ExecAsync(ProcessStartInfo psi, int timeoutSeconds, CancellationToken ct)
    {
        var itemId = CurrentItemId.Value;
        if (itemId is not null) OnCommandStarted?.Invoke(itemId);
        try
        {
            return await ExecCoreAsync(psi, timeoutSeconds, ct).ConfigureAwait(false);
        }
        finally
        {
            if (itemId is not null) OnCommandCompleted?.Invoke(itemId);
        }
    }

    private static async Task<ShellResult> ExecCoreAsync(ProcessStartInfo psi, int timeoutSeconds, CancellationToken ct)
    {
        // Stop build tools leaving daemons behind in the first place.
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        // Signals on process exit alone. Process.WaitForExitAsync also waits for the
        // redirected streams to reach end-of-file, which never happens while an inherited
        // daemon still holds them — so waiting on it hangs long after the command finished.
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        proc.Exited += (_, _) => exited.TrySetResult();

        try { proc.Start(); }
        catch (Exception ex) { return new ShellResult(127, "", ex.Message, false); }

        if (proc.HasExited) exited.TrySetResult();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var outTask = ReadAsync(proc.StandardOutput, stdout, cts.Token);
        var errTask = ReadAsync(proc.StandardError, stderr, cts.Token);

        try
        {
            await exited.Task.WaitAsync(cts.Token).ConfigureAwait(false);

            // Bounded drain: collect whatever output is still buffered, but never wait on a
            // pipe that an inherited daemon is holding open.
            await Task.WhenAny(
                Task.WhenAll(outTask, errTask),
                Task.Delay(DrainGrace, cts.Token)).ConfigureAwait(false);
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
                if (sink.Length < MaxCapturedOutputChars) sink.Append(buffer, 0, n);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }
}
