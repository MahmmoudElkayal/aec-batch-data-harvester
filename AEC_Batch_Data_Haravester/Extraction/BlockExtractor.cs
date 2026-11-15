using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using AEC_Batch_Data_Haravester.Core;
using AcException = Autodesk.AutoCAD.Runtime.Exception;

namespace AEC_Batch_Data_Haravester.Extraction
{
    /// <summary>
    /// Placed block (mechanical equipment, furniture, doors, ...), plus the attributes it
    /// carries. One block reference is one entity row and N attribute rows, in two files.
    ///
    /// An unreadable attribute loses that attribute, never the block: the block row is the
    /// join target, and a missing tag is visibly missing whereas a missing block is
    /// invisible.
    /// </summary>
    public sealed class BlockExtractor : IEntityExtractor
    {
        public RXClass TargetClass => RXClass.GetClass(typeof(BlockReference));

        public EntityExtraction Extract(Entity entity, HarvestContext context)
        {
            var block = (BlockReference)entity;
            string handle = block.Handle.ToString();

            var row = new HarvestRow(
                Drawing: context.Drawing,
                Space: context.Space,
                EntityType: EntityKind.Block.ToString(),
                Name: block.Name,
                BlockDefinition: DefinitionName(context.Transaction, block),
                Layer: block.Layer,
                Handle: handle,
                X: block.Position.X,
                Y: block.Position.Y,
                Z: block.Position.Z,
                Length: null,
                Unit: context.Unit,
                UnitCode: context.UnitCode);

            return new EntityExtraction(new[] { row }, Attributes(block, context, handle));
        }

        private static IReadOnlyList<HarvestAttributeRow> Attributes(
            BlockReference block, HarvestContext context, string handle)
        {
            var rows = new List<HarvestAttributeRow>();

            // Constant attributes live on the block definition, not on the reference, so
            // this collection holds the referenced (variable) ones. The flag is still read
            // from the object rather than assumed, because the corpus has no constant
            // attribute to generalise from.
            foreach (ObjectId id in block.AttributeCollection)
            {
                if (id.IsErased)
                {
                    continue;
                }

                try
                {
                    if (context.Transaction.GetObject(id, OpenMode.ForRead)
                        is not AttributeReference attribute)
                    {
                        continue;
                    }

                    rows.Add(new HarvestAttributeRow(
                        Drawing: context.Drawing,
                        EntityHandle: handle,
                        Tag: attribute.Tag,
                        Value: TextValue.SingleLine(attribute.TextString),
                        IsConstant: attribute.IsConstant));
                }
                catch (AcException)
                {
                    // A proxy or erased attribute: skip it, keep the block and the rest.
                }
            }

            return rows;
        }

        /// <summary>
        /// The block definition's real name. For a dynamic block, Name is an anonymous
        /// "*U12"-style name, so the definition is resolved through DynamicBlockTableRecord.
        /// The equality of the two properties for non-dynamic blocks is undocumented,
        /// hence the explicit branch rather than relying on it.
        /// </summary>
        private static string DefinitionName(Transaction tr, BlockReference block)
        {
            try
            {
                ObjectId definitionId = block.IsDynamicBlock
                    ? block.DynamicBlockTableRecord
                    : block.BlockTableRecord;

                if (definitionId.IsNull)
                {
                    return string.Empty;
                }

                return tr.GetObject(definitionId, OpenMode.ForRead) is BlockTableRecord definition
                    ? definition.Name
                    : string.Empty;
            }
            catch (AcException)
            {
                // A dangling or proxy definition must not lose the whole entity row.
                return string.Empty;
            }
        }
    }
}
