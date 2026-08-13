using System.Text.RegularExpressions;
using Factory.Core;

namespace Factory.Runtime;

/// <summary>
/// Checks acceptance criteria without invoking a model.
///
/// This is the factory's quality mechanism and its largest token saving at the same time.
/// Verification is the most frequently repeated check in the pipeline — it runs after every
/// implementation attempt and every retry — so moving it off the model removes the single
/// most repeated model call in the system. It is also the only form of verification that
/// cannot be talked out of a failure.
/// </summary>
public static class DeterministicVerifier
{
    public sealed record Outcome(VerificationReport Report, IReadOnlyList<AcceptanceCriterion> Deferred)
    {
        /// <summary>Criteria needing a model judgement, handed on to the review station.</summary>
        public bool HasDeferred => Deferred.Count > 0;

        public bool DeterministicPassed => Report.Results.Count == 0 || Report.AllPassed;
    }

    public static async Task<Outcome> VerifyAsync(
        WorkItem item, string workDir, CancellationToken ct = default)
    {
        var results = new List<CriterionResult>();
        var deferred = new List<AcceptanceCriterion>();

        foreach (var criterion in item.AcceptanceCriteria)
        {
            ct.ThrowIfCancellationRequested();

            switch (criterion.Verification)
            {
                case CommandVerification cmd:
                    results.Add(await RunCommandAsync(criterion, cmd.Command, cmd.ExpectExitCode,
                        cmd.ExpectStdoutMatch, cmd.TimeoutSeconds, workDir, ct).ConfigureAwait(false));
                    break;

                case TestsPassVerification tests:
                    results.Add(await RunCommandAsync(criterion, tests.Command, 0, null,
                        tests.TimeoutSeconds, workDir, ct).ConfigureAwait(false));
                    break;

                case FileExistsVerification file:
                {
                    var path = Path.IsPathRooted(file.Path) ? file.Path : Path.Combine(workDir, file.Path);
                    var exists = File.Exists(path) || Directory.Exists(path);
                    results.Add(exists
                        ? CriterionResult.Pass(criterion.Id, $"{file.Path} exists")
                        : CriterionResult.Fail(criterion.Id, $"{file.Path} does not exist"));
                    break;
                }

                case AgentJudgeVerification:
                    deferred.Add(criterion);
                    break;
            }
        }

        return new Outcome(new VerificationReport(results), deferred);
    }

    private static async Task<CriterionResult> RunCommandAsync(
        AcceptanceCriterion criterion, string command, int expectExit,
        string? expectMatch, int timeoutSeconds, string workDir, CancellationToken ct)
    {
        var run = await Shell.RunAsync(command, workDir, timeoutSeconds, ct).ConfigureAwait(false);

        if (run.TimedOut)
            return CriterionResult.Fail(criterion.Id, $"`{command}` timed out after {timeoutSeconds}s");

        if (run.ExitCode != expectExit)
            return CriterionResult.Fail(criterion.Id,
                $"`{command}` exited {run.ExitCode} (expected {expectExit}): {Tail(run.Combined)}");

        if (expectMatch is { Length: > 0 } && !Regex.IsMatch(run.Stdout, expectMatch))
            return CriterionResult.Fail(criterion.Id,
                $"`{command}` output did not match /{expectMatch}/: {Tail(run.Stdout)}");

        return CriterionResult.Pass(criterion.Id, $"`{command}` passed");
    }

    /// <summary>Failure messages are fed back to the implementation station, so they are
    /// trimmed to the tail — where the error normally is — to bound prompt growth.</summary>
    private static string Tail(string s, int max = 1200)
    {
        s = s.Trim();
        if (s.Length <= max) return s;
        return "…" + s[^max..];
    }
}
