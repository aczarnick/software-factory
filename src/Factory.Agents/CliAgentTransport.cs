using System.Diagnostics;
using System.Text;
using Factory.Core;

namespace Factory.Agents;

/// <summary>
/// Drives the Claude Agent SDK through the <c>claude</c> CLI in headless streaming mode —
/// the same transport the official SDK uses:
/// <c>claude -p --output-format stream-json --input-format text</c>.
///
/// The prompt goes in on stdin rather than as an argument, so prompt size is not bounded by
/// the OS argument limit. The terminal <c>result</c> message supplies cost, token usage,
/// turn count and session id, which become the factory's unit of telemetry and the input to
/// prompt evaluation.
/// </summary>
public sealed class CliAgentTransport(string? executable = null, TimeSpan? timeout = null) : IAgentTransport
{
    private readonly string _exe = executable
        ?? Environment.GetEnvironmentVariable("FACTORY_CLAUDE_BIN")
        ?? "claude";

    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromMinutes(30);

    public async Task<AgentRunResult> RunAsync(
        AgentRequest request,
        Action<AgentEvent>? onEvent = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var psi = new ProcessStartInfo
        {
            FileName = _exe,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = request.WorkingDirectory ?? Environment.CurrentDirectory
        };

        foreach (var a in BuildArgs(request)) psi.ArgumentList.Add(a);
        ApplyEnvironment(psi, request);

        using var proc = new Process { StartInfo = psi };

        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            return AgentRunResult.Failure($"could not start '{_exe}': {ex.Message}", sw.ElapsedMilliseconds);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_timeout);
        var token = timeoutCts.Token;

        var stderr = new StringBuilder();
        var stderrTask = DrainAsync(proc.StandardError, stderr, token);

        // Prompt on stdin; closing the stream is what tells the CLI the turn is complete.
        try
        {
            await proc.StandardInput.WriteAsync(request.Prompt.AsMemory(), token).ConfigureAwait(false);
            proc.StandardInput.Close();
        }
        catch (IOException)
        {
            // Process died early; the result path below reports the real reason.
        }

        AgentEvent? resultEvent = null;
        string? sessionId = null;
        var assistantText = new StringBuilder();
        var toolsUsed = new List<string>();

        try
        {
            while (await proc.StandardOutput.ReadLineAsync(token).ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (AgentEvent.TryParse(line) is not { } evt) continue;

                onEvent?.Invoke(evt);

                sessionId ??= evt.SessionId;
                if (evt.AssistantText is { } text) assistantText.Append(text);
                toolsUsed.AddRange(evt.ToolUses);
                if (evt.IsResult) resultEvent = evt;
            }

            await proc.WaitForExitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(proc);
            var reason = ct.IsCancellationRequested ? "cancelled" : $"timed out after {_timeout.TotalMinutes:F0}m";
            return AgentRunResult.Failure(reason, sw.ElapsedMilliseconds);
        }

        await stderrTask.ConfigureAwait(false);

        if (resultEvent is null)
        {
            var err = stderr.Length > 0 ? stderr.ToString().Trim() : $"transport exited {proc.ExitCode} with no result message";
            return AgentRunResult.Failure(Truncate(err, 2000), sw.ElapsedMilliseconds);
        }

        return FromResultEvent(resultEvent, sessionId, assistantText.ToString(), toolsUsed, sw.ElapsedMilliseconds);
    }

    internal static AgentRunResult FromResultEvent(
        AgentEvent evt, string? sessionId, string assistantText,
        IReadOnlyList<string> toolsUsed, long durationMs)
    {
        var isError = evt.Bool("is_error") || evt.Subtype is not null && evt.Subtype != "success";
        var text = evt.Str("result") ?? assistantText;

        var usage = TokenUsage.Zero;
        if (evt.Raw.TryGetProperty("usage", out var u))
        {
            usage = new TokenUsage(
                Num(u, "input_tokens"),
                Num(u, "output_tokens"),
                Num(u, "cache_read_input_tokens"),
                Num(u, "cache_creation_input_tokens"));
        }

        return new AgentRunResult
        {
            Success = !isError,
            Text = text,
            SessionId = sessionId ?? evt.Str("session_id"),
            CostUsd = evt.Dec("total_cost_usd"),
            Usage = usage,
            Turns = evt.Num("num_turns"),
            StopReason = evt.Str("stop_reason") ?? evt.Subtype,
            // Carried as its own flag rather than left for a caller to recognise in the error text:
            // the turn ceiling arrives as the subtype while stop_reason still reads "tool_use", so
            // matching on the message would be matching on prose.
            ExhaustedTurns = evt.Subtype == "error_max_turns",
            Error = isError ? DescribeError(evt) : null,
            DurationMs = durationMs,
            ToolsUsed = toolsUsed.Distinct().ToList(),
            RawResult = isError ? evt.Raw.GetRawText() : null
        };

        static int Num(System.Text.Json.JsonElement e, string p) =>
            e.TryGetProperty(p, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number
                ? v.GetInt32() : 0;
    }

    /// <summary>
    /// Turns a failed result into something a person can act on. The transport can report
    /// <c>is_error</c> while the subtype still reads "success" — an abnormally ended turn
    /// rather than a rejected request — and naively echoing the subtype produced the
    /// spectacularly unhelpful message "gate failed: success".
    /// </summary>
    private static string DescribeError(AgentEvent evt)
    {
        if (evt.Str("api_error_status") is { Length: > 0 } apiStatus) return apiStatus;

        if (evt.Subtype is { Length: > 0 } subtype && subtype != "success") return subtype;

        var stop = evt.Str("stop_reason");
        var turns = evt.Num("num_turns");

        return stop is { Length: > 0 }
            ? $"run ended abnormally after {turns} turn(s) (stop_reason: {stop})"
            : $"agent reported an error after {turns} turn(s) without giving a reason";
    }

    internal static List<string> BuildArgs(AgentRequest request)
    {
        var structured = request.JsonSchema is { Length: > 0 };
        var args = request.Profile.ToArgs(structured);

        if (request.JsonSchema is { Length: > 0 } schema)
            args.AddRange(["--json-schema", schema]);

        if (request.MaxBudgetUsd is { } budget && budget > 0)
            args.AddRange(["--max-budget-usd", budget.ToString("0.####")]);

        if (request.ResumeSessionId is { Length: > 0 } sid)
            args.AddRange(["--resume", sid]);

        foreach (var dir in request.AddDirs)
            args.AddRange(["--add-dir", dir]);

        return args;
    }

    /// <summary>
    /// Thick stations run with <c>--permission-mode bypassPermissions</c>, which the CLI treats
    /// as <c>--dangerously-skip-permissions</c> and refuses outright under uid 0:
    /// <c>"--dangerously-skip-permissions cannot be used with root/sudo privileges"</c>. Every
    /// station then fails to produce a result message, so `factory up` dies on the first
    /// dispatch. Containers routinely run as root, and the CLI's sanctioned escape for exactly
    /// that case is <c>IS_SANDBOX=1</c> — the container boundary is the sandbox the permission
    /// prompt would otherwise be standing in for.
    ///
    /// An inherited value always wins, so a caller that has deliberately set (or cleared) it
    /// keeps control.
    /// </summary>
    internal static void ApplyEnvironment(ProcessStartInfo psi, AgentRequest request)
    {
        if (NeedsSandboxOptIn(
                request.Profile,
                Environment.GetEnvironmentVariable("IS_SANDBOX"),
                IsRoot()))
        {
            psi.Environment["IS_SANDBOX"] = "1";
        }
    }

    /// <summary>The decision behind <see cref="ApplyEnvironment"/>, separated so it is testable
    /// without a root process.</summary>
    internal static bool NeedsSandboxOptIn(AgentProfile profile, string? inherited, bool isRoot) =>
        isRoot && inherited is null && !profile.IsThin && profile.PermissionMode == "bypassPermissions";

    /// <summary>True when this process is running as uid 0 on a Unix-like host.</summary>
    internal static bool IsRoot()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return false;

        try { return Geteuid() == 0; }
        catch (EntryPointNotFoundException) { return false; }
        catch (DllNotFoundException) { return false; }
    }

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(
        System.Runtime.InteropServices.DllImportSearchPath.SafeDirectories)]
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint Geteuid();

    private static async Task DrainAsync(StreamReader reader, StringBuilder sink, CancellationToken ct)
    {
        try
        {
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                sink.AppendLine(line);
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (IOException) { /* stream closed with the process */ }
    }

    private static void TryKill(Process proc)
    {
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
        catch { /* already gone */ }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
