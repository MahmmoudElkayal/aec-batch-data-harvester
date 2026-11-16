namespace AEC_Batch_Data_Haravester.Core
{
    /// <summary>
    /// One harvested entity, independent of AutoCAD. Everything downstream of extraction
    /// (formatting, sinks, reporting) works on this type only, which is what keeps those
    /// layers testable without a CAD host.
    ///
    /// <see cref="Container"/>, <see cref="Depth"/> and <see cref="IsXref"/> describe where
    /// the row came from once nested block traversal is enabled. They default to the
    /// top-level case, so an extractor never has to know about nesting: the scanner stamps
    /// the provenance on every row it writes.
    /// </summary>
    public sealed record HarvestRow(
        string Drawing,
        string Space,
        string EntityType,
        string Name,
        string BlockDefinition,
        string Layer,
        string Handle,
        double? X,
        double? Y,
        double? Z,
        double? Length,
        double? Area,
        string Unit,
        int UnitCode,
        string Container = "",
        int Depth = 0,
        bool IsXref = false);
}
