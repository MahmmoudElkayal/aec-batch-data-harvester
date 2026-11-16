namespace AEC_Batch_Data_Haravester.Core
{
    /// <summary>
    /// The entity types the harvester understands. The CSV's EntityType column is
    /// <see cref="object.ToString"/> of this value, so the enum is the single place that
    /// decides spelling ("Block", "Polyline", "DBText", ...).
    /// </summary>
    public enum EntityKind
    {
        Block,
        Polyline,
        Line,
        Arc,
        Circle,
        DBText,
        MText,
        Solid,
        Curve,
        Hatch
    }
}
