using Factory.Runtime;

namespace Factory.Tests;

/// <summary>
/// The canary for the 65 beads-backed tests that guard themselves with <c>if (Unavailable) return;</c>
/// or <c>if (!Available) return;</c>. xunit 2.9.2 has no dynamic skip, so every one of those guards
/// reports the test as <em>passed</em> rather than skipped: on a machine without <c>bd</c> — a
/// container, a new contributor's laptop, the first CI job this repository gets — the same green total
/// prints while none of the beads behaviour is exercised at all.
///
/// This one red names the vacuum instead. It is the branch's own thesis applied to its own gate: an
/// assertion that cannot fail is not evidence, and "the suite is green" is the only evidence offered
/// for any of the beads work.
/// </summary>
public class BeadsAvailabilityTests
{
    [Fact]
    public void Bd_is_on_path_so_the_beads_tests_are_not_passing_vacuously()
    {
        Assert.True(Shell.Which("bd"),
            "bd is not on PATH, so every beads-backed test returned at its availability guard and " +
            "passed without exercising anything. Install bd, or filter those classes out " +
            "deliberately rather than reading this run as evidence that the beads backlog works.");
    }
}
