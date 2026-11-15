namespace AEC_Batch_Data_Haravester.Reporting
{
    /// <summary>
    /// How one drawing's outcome is published. An interface because the supervisor's
    /// contract (an explicit .ok is the only proof of success) is worth testing, and a test
    /// should not have to write files to check it.
    /// </summary>
    public interface IStatusReporter
    {
        void Success(
            string drawingName, string message, long elapsedMs,
            int rows, int attributes, int skipped, string? unitName, int? unitCode);

        void Failure(string drawingName, string message, string? detail, long elapsedMs);
    }
}
