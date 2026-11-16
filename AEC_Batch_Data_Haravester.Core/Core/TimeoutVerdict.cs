using System;
using System.Collections.Generic;

namespace AEC_Batch_Data_Haravester.Core
{
    /// <summary>
    /// Which drawing to blame when the engine process dies without reporting.
    ///
    /// The engine isolates normal failures itself - a corrupt drawing becomes a .fail and
    /// the batch carries on. This handles the other case: a hard crash, or a drawing that
    /// never returns. The process is gone and said nothing, so the supervisor has to decide
    /// which drawing was in flight, and it must never decide "none" and then leave a hung
    /// drawing to look like a drawing that was simply not reached.
    ///
    /// The rule, in order:
    ///  1. the drawing the engine had marked as current, if it has not already reported;
    ///  2. otherwise the first drawing of the chunk that has not reported - because the
    ///     process died somewhere, and the first unreported drawing is the only defensible
    ///     answer when the marker is unavailable (it can be missing if the crash happened
    ///     before the first marker was written);
    ///  3. otherwise nothing, which means the process died after finishing its work.
    /// </summary>
    public static class TimeoutVerdict
    {
        public static string? Choose(
            string? inFlightKey,
            IReadOnlyList<BatchEntry> chunk,
            Func<string, bool> hasReported)
        {
            if (!string.IsNullOrEmpty(inFlightKey) && !hasReported(inFlightKey))
            {
                foreach (BatchEntry entry in chunk)
                {
                    if (string.Equals(entry.Key, inFlightKey, StringComparison.Ordinal))
                    {
                        return entry.Key;
                    }
                }
            }

            foreach (BatchEntry entry in chunk)
            {
                if (!hasReported(entry.Key))
                {
                    return entry.Key;
                }
            }

            return null;
        }
    }
}
