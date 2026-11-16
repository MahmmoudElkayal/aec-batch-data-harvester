using Autodesk.AutoCAD.DatabaseServices;
using AcException = Autodesk.AutoCAD.Runtime.Exception;

namespace AEC_Batch_Data_Haravester.Extraction
{
    /// <summary>
    /// The one place that answers "which block definition does this reference use, and is
    /// it an xref?". Three callers need the same answer - the block extractor for the row,
    /// the scanner for recursion, and the constant-attribute cache - so the dynamic-block
    /// branch and the failure policy live here once.
    /// </summary>
    internal static class BlockInfo
    {
        /// <summary>
        /// For a dynamic block, Name is an anonymous "*U12"-style name, so the definition
        /// is resolved through DynamicBlockTableRecord. A dangling or proxy reference
        /// yields <see cref="ObjectId.Null"/> rather than losing the whole row.
        /// </summary>
        public static ObjectId DefinitionId(BlockReference block)
        {
            try
            {
                return block.IsDynamicBlock
                    ? block.DynamicBlockTableRecord
                    : block.BlockTableRecord;
            }
            catch (AcException)
            {
                return ObjectId.Null;
            }
        }

        public static BlockTableRecord? Definition(Transaction tr, BlockReference block)
        {
            ObjectId id = DefinitionId(block);
            if (id.IsNull)
            {
                return null;
            }

            try
            {
                return tr.GetObject(id, OpenMode.ForRead) as BlockTableRecord;
            }
            catch (AcException)
            {
                return null;
            }
        }

        /// <summary>
        /// "Returns true if the block table record is describing an xref drawing"
        /// (managed reference, <c>BlockTableRecord.IsFromExternalReference</c>, DXF group
        /// 70 bit 4). An unreadable definition is treated as local: the row still exists,
        /// and a wrong external flag is worse than no flag.
        /// </summary>
        public static bool IsExternalReference(BlockTableRecord? definition)
        {
            if (definition is null)
            {
                return false;
            }

            try
            {
                return definition.IsFromExternalReference;
            }
            catch (AcException)
            {
                return false;
            }
        }

        public static string Name(BlockTableRecord? definition)
        {
            if (definition is null)
            {
                return string.Empty;
            }

            try
            {
                return definition.Name;
            }
            catch (AcException)
            {
                return string.Empty;
            }
        }
    }
}
