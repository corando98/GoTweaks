namespace Shared.Data
{
    /// <summary>
    /// Optional, additive opt-in for a FunctionalProperty whose NotifyPropertyChanged override
    /// calls a hardware/WMI/native API that can fail, clamp, or silently no-op. GenericProperty's
    /// SetValue always returns true once a value passes timestamp/equality arbitration - it has
    /// no visibility into whether the subsequent hardware apply actually succeeded, so without
    /// this a Set request is reported "Applied" purely because the in-memory cache accepted it
    /// (the permanent confirmed-state rule, CLAUDE.md/AGENTS.md section 13, calls this out:
    /// cache acceptance must never be presented as hardware application).
    ///
    /// Deliberately NOT exception-based: NotifyPropertyChanged is also reached from many
    /// internal helper call sites (profile restore, AC/DC transitions, quick actions) that batch
    /// several properties' SetValue calls inside one try/catch - a thrown exception on field N
    /// would abort applying fields N+1.. in the same batch, a real regression risk in that
    /// already-fragile area. This interface is instead only consulted by the pipe Set path
    /// (FunctionalProperties.HandlePipeMessage) right after SetValue returns, so batch-apply
    /// callers that just call .SetValue() and move on are completely unaffected.
    /// </summary>
    public interface IHardwareApplyResult
    {
        /// <summary>Whether the most recent NotifyPropertyChanged hardware apply succeeded.</summary>
        bool LastApplySucceeded { get; }

        /// <summary>User-facing reason when LastApplySucceeded is false; otherwise null.</summary>
        string LastApplyFailureReason { get; }
    }
}
