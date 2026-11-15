namespace AEC_Batch_Data_Haravester.Core
{
    /// <summary>What one drawing produced.</summary>
    public sealed record HarvestResult(int RowsWritten, int Skipped);
}
