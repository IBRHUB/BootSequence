namespace BootSequence.Core;

public static class BootSequenceTransaction
{
    public static BootSequenceMutationResult TrySetIfEmpty(
        string id,
        Func<IReadOnlyList<string>> readSequence,
        Func<bool> isPersistent,
        Func<string, bool> setSequence,
        Func<bool> clearSequence)
    {
        if (readSequence().Count != 0)
        {
            return BootSequenceMutationResult.PendingExists;
        }

        if (isPersistent())
        {
            return BootSequenceMutationResult.PersistentSequence;
        }

        // This is the final pre-write read. The caller must hold its mutation lock
        // across this method so cooperating processes cannot write between steps.
        if (readSequence().Count != 0)
        {
            return BootSequenceMutationResult.PendingExists;
        }

        bool writeReportedSuccess = setSequence(id);
        IReadOnlyList<string> verification = readSequence();
        bool ownsCurrentValue = verification.Count == 1 &&
            string.Equals(verification[0], id, StringComparison.OrdinalIgnoreCase);

        if (!writeReportedSuccess)
        {
            if (ownsCurrentValue)
            {
                clearSequence();
            }

            return BootSequenceMutationResult.WriteFailed;
        }

        return ownsCurrentValue
            ? BootSequenceMutationResult.Written
            : BootSequenceMutationResult.VerificationFailed;
    }

    public static bool ClearIfMatches(
        string id,
        Func<IReadOnlyList<string>> readSequence,
        Func<bool> clearSequence)
    {
        IReadOnlyList<string> sequence = readSequence();
        return sequence.Count == 1 &&
               string.Equals(sequence[0], id, StringComparison.OrdinalIgnoreCase) &&
               clearSequence();
    }
}
