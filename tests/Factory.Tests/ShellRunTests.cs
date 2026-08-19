using Factory.Runtime;

namespace Factory.Tests;

public class ShellRunTests
{
    [Fact]
    public void Run_captures_stdout_and_exit_code()
    {
        var result = Shell.Run("/bin/echo", ["hello"], Directory.GetCurrentDirectory());

        Assert.True(result.Ok);
        Assert.Contains("hello", result.Stdout);
    }

    [Fact]
    public void Run_reports_a_non_zero_exit()
    {
        var result = Shell.Run("/bin/sh", ["-c", "exit 3"], Directory.GetCurrentDirectory());

        Assert.False(result.Ok);
        Assert.Equal(3, result.ExitCode);
    }

    [Fact]
    public void Run_passes_environment_variables_through()
    {
        var result = Shell.Run("/bin/sh", ["-c", "echo $FACTORY_PROBE"],
            Directory.GetCurrentDirectory(),
            new Dictionary<string, string> { ["FACTORY_PROBE"] = "set" });

        Assert.Contains("set", result.Stdout);
    }

    [Fact]
    public void Run_times_out_rather_than_hanging()
    {
        var result = Shell.Run("/bin/sh", ["-c", "sleep 5"], Directory.GetCurrentDirectory(),
            timeoutSeconds: 1);

        Assert.True(result.TimedOut);
    }

    [Fact]
    public void Run_does_not_wait_for_a_lingering_grandchild_to_release_the_pipe()
    {
        var started = System.Diagnostics.Stopwatch.StartNew();

        var result = Shell.Run("/bin/sh", ["-c", "(sleep 8 & ) ; echo done"],
            Directory.GetCurrentDirectory(), timeoutSeconds: 30);

        started.Stop();

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("done", result.Stdout);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(6),
            $"Run waited {started.Elapsed.TotalSeconds:F1}s for a grandchild holding the pipe.");
    }

    [Fact]
    public void Run_reports_rather_than_throws_when_the_executable_does_not_exist()
    {
        // Process.Start on a nonexistent file throws System.ComponentModel.Win32Exception -- a
        // SystemException that is neither an IOException nor an InvalidOperationException. That
        // makes this the case that proves the catch's SystemException arm is the one actually
        // doing the work: IOException and InvalidOperationException both already derive from it,
        // so naming them separately in the filter catches nothing the bare type would not.
        var result = Shell.Run("/definitely/does/not/exist-xyz", [], Directory.GetCurrentDirectory());

        Assert.Equal(127, result.ExitCode);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public void Run_bounds_how_much_output_it_retains()
    {
        // 200k characters against a 64k bound. Callers that parse structured output rely on this
        // bound being knowable, because a capture cut at it is not valid JSON.
        var result = Shell.Run("/bin/sh", ["-c", "yes 0123456789 | head -20000"],
            Directory.GetCurrentDirectory(), timeoutSeconds: 30);

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Stdout.Length >= Shell.MaxCapturedOutputChars,
            $"expected the capture to reach the bound, got {result.Stdout.Length} characters");
        Assert.True(result.Stdout.Length < Shell.MaxCapturedOutputChars * 2,
            $"expected the capture to be bounded near {Shell.MaxCapturedOutputChars}, got {result.Stdout.Length}");
    }
}
