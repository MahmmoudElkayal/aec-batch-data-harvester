namespace AEC_Batch_Data_Haravester.Core
{
    /// <summary>
    /// One harvested entity, independent of AutoCAD. Everything downstream of extraction
    /// (formatting, sinks, reporting) works on this type only, which is what keeps those
    /// layers testable without a CAD host.
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
        string Unit,
        int UnitCode);
}
