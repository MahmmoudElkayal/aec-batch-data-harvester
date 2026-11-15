namespace AEC_Batch_Data_Haravester.Core
{
    /// <summary>
    /// What one drawing produced. Units travel with the result because the drawing is
    /// disposed by the time the status file is written, so they cannot be read later.
    ///
    /// Entity and attribute rows are counted separately: they go to different files, and
    /// the supervisor's row count is the entity count that the merge must reproduce. An
    /// attribute count that silently equalled the entity count would hide a broken join.
    /// </summary>
    public sealed record HarvestResult(
        int RowsWritten,
        int AttributesWritten,
        int Skipped,
        string Unit = "",
        int UnitCode = 0);
}
