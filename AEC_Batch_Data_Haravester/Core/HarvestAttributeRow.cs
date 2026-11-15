namespace AEC_Batch_Data_Haravester.Core
{
    /// <summary>
    /// One block attribute reference, long-format. Attributes get their own file rather
    /// than a set of columns: a tag can legitimately be "#" (measured in the corpus), so a
    /// wide schema would need a column named "#" and would change shape per batch.
    ///
    /// <see cref="EntityHandle"/> is the handle of the owning block reference, so this file
    /// joins to the entity file on Handle. A block with three attributes contributes three
    /// rows with the same handle and different tags.
    /// </summary>
    public sealed record HarvestAttributeRow(
        string Drawing,
        string EntityHandle,
        string Tag,
        string Value,
        bool IsConstant);
}
