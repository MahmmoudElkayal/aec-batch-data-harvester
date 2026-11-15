namespace AEC_Batch_Data_Haravester.Core
{
    /// <summary>
    /// What one drawing produced. Units travel with the result because the drawing is
    /// disposed by the time the status file is written, so they cannot be read later.
    /// </summary>
    public sealed record HarvestResult(
        int RowsWritten, int Skipped, string Unit = "", int UnitCode = 0);
}
