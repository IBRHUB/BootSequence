namespace BootSequence.Core;

public sealed class BootCoordinator(
    IBootConfigurationService boot,
    IDiagnosticLogger? logger = null)
{
    public PrepareResult Prepare(string targetId)
    {
        BootEntry? target;
        try
        {
            target = boot.ReadEntries().FirstOrDefault(entry =>
                string.Equals(entry.Id, targetId, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception)
        {
            logger?.Error("bcd.validate-target", exception);
            return PrepareResult.WriteFailed;
        }

        if (target is null || target.IsCurrent || !target.IsSelectable)
        {
            return PrepareResult.InvalidTarget;
        }

        try
        {
            return boot.TrySetOneTimeSequenceIfEmpty(targetId) switch
            {
                BootSequenceMutationResult.Written => PrepareResult.Ready,
                BootSequenceMutationResult.PendingExists => PrepareResult.PendingExists,
                BootSequenceMutationResult.PersistentSequence => PrepareResult.PersistentSequence,
                BootSequenceMutationResult.VerificationFailed => PrepareResult.VerificationFailed,
                _ => PrepareResult.WriteFailed
            };
        }
        catch (Exception exception)
        {
            logger?.Error("bcd.prepare-sequence", exception);
            RollBackIfOwned(targetId);
            return PrepareResult.WriteFailed;
        }
    }

    public void RollBackIfOwned(string targetId)
    {
        try
        {
            boot.ClearOneTimeSequenceIfMatches(targetId);
        }
        catch (Exception exception)
        {
            logger?.Error("bcd.rollback", exception);
        }
    }
}
