namespace Factory.Runtime;

/// <summary>
/// Which faults from a ledger append are tolerable to lose rather than treated as a defect. Shared
/// by every caller that writes to the ledger best-effort -- <see cref="LedgerMirroringWorkItemStore"/>
/// and <see cref="BacklogReconciler"/> -- so the two agree by construction rather than by two
/// comments promising they match.
/// </summary>
internal static class LedgerFaultTolerance
{
    /// <summary>
    /// Exactly the two ways the *file-backed* ledger refuses an append the backlog write has
    /// already outrun: the device would not take it, or this process may not write the file. Both
    /// are D2's tolerable loss -- the backlog write already committed, so only the local audit copy
    /// is lost, and it self-heals at the next reconcile. <see cref="UnauthorizedAccessException"/> is
    /// named separately because it is a <see cref="SystemException"/> and not an
    /// <see cref="IOException"/>, and a read-only ledger -- owned by another user, or on a mount that
    /// denies writes -- is how it arrives.
    ///
    /// Everything else is deliberately excluded. <see cref="ObjectDisposedException"/> says the
    /// ledger is closed rather than broken: no reconcile heals that and every later append repeats
    /// it, so tolerating it would drop the whole audit trail without saying so.
    /// <see cref="InvalidOperationException"/> can only reach a caller's try block from
    /// <c>FactoryState.Apply</c>, whose failure is a defect in the fold, not a ledger fault.
    /// Tolerating either would hide a bug in the factory to survive a fault in the environment.
    ///
    /// Scoped to <see cref="JsonlRunHistory"/> on purpose, because that is the only writer that
    /// ships: <see cref="IRunHistory"/> is resolved by provider name, and a plugin ledger over a
    /// database or an HTTP endpoint reports its transport faults as the very types excluded above --
    /// <see cref="InvalidOperationException"/> for a closed connection, <see cref="ObjectDisposedException"/>
    /// for a recycled client, and others again. Those stay loud rather than being tolerated here.
    /// Whether D2's tolerance should be provider-agnostic is a spec question, recorded on the
    /// sync-gate decision list in <c>docs/superpowers/notes/2026-08-18-phase-4-handoff.md</c>.
    /// </summary>
    public static bool IsTolerable(Exception ex) => ex is IOException or UnauthorizedAccessException;
}
