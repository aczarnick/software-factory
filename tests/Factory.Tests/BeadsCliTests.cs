using System.Text.Json;
using Factory.Runtime;

namespace Factory.Tests;

/// <summary>
/// <see cref="BeadsCli"/>'s JSON handling against a canned <c>bd</c> response, so the capture
/// bound can be pinned exactly without spending a real process start. Overriding <see
/// cref="BeadsCli.Exec"/> is the same seam <see cref="BeadsWorkItemStoreTests"/> uses to interpose
/// on a specific <c>bd</c> call.
/// </summary>
public class BeadsCliTests
{
    private sealed record IdOnly(string Id);

    private sealed class ReturnsCannedStdout(string stdout) : BeadsCli("unused", "test-machine")
    {
        public override ShellResult Exec(params string[] args) => new(0, stdout, "", false);
    }

    private const string Prefix = "[{\"id\":\"";
    private const string Suffix = "\"}]";

    [Fact]
    public void A_complete_document_exactly_at_the_capture_bound_is_not_rejected_as_truncated()
    {
        // Shell.ReadAsync appends a whole 4096-char buffer whenever sink.Length is still under the
        // bound, so a genuine, complete document can legitimately land at exactly
        // Shell.MaxCapturedOutputChars. Rejecting it here would mean a real backlog of exactly this
        // size can never be read.
        var padding = new string('a', Shell.MaxCapturedOutputChars - Prefix.Length - Suffix.Length);
        var document = Prefix + padding + Suffix;
        Assert.Equal(Shell.MaxCapturedOutputChars, document.Length);

        var items = new ReturnsCannedStdout(document).Json<IdOnly>("list");

        Assert.Single(items);
    }

    [Fact]
    public void A_document_cut_off_at_the_capture_bound_is_reported_as_truncated_not_as_malformed_json()
    {
        // A document whose complete form is well past the bound, cut exactly where Shell would have
        // cut it -- mid-string, so it is not valid JSON on its own. This is what a real truncation
        // looks like: naming the real cause (the backlog outgrew one command's output) is the whole
        // point of this guard, rather than reporting it as though bd had emitted garbage.
        var padding = new string('a', Shell.MaxCapturedOutputChars);
        var whole = Prefix + padding + Suffix;
        var truncated = whole[..Shell.MaxCapturedOutputChars];

        var ex = Assert.Throws<InvalidOperationException>(
            () => new ReturnsCannedStdout(truncated).Json<IdOnly>("list"));

        Assert.Contains("truncated", ex.Message);

        // The truncation report must not discard the parse failure that triggered it -- otherwise a
        // genuinely malformed over-bound response is reported as "truncated" with nothing left to
        // contradict that claim.
        Assert.IsType<JsonException>(ex.InnerException);
    }

    [Fact]
    public void A_complete_short_document_is_read_normally()
    {
        var items = new ReturnsCannedStdout("[{\"id\":\"wi-aaaa11112222\"}]").Json<IdOnly>("list");

        Assert.Equal("wi-aaaa11112222", Assert.Single(items).Id);
    }

    [Fact]
    public void Malformed_json_well_under_the_bound_is_reported_as_malformed_not_as_truncated()
    {
        // Nowhere near the bound, so a JsonException here has nothing to do with truncation. The
        // guard has to consult the length rather than converting every parse failure, or a genuinely
        // malformed short response would be misreported as a backlog that outgrew its capture.
        Assert.Throws<JsonException>(
            () => new ReturnsCannedStdout("not json at all").Json<IdOnly>("list"));
    }
}
