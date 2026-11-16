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
            BlockTableRecord? definition = BlockInfo.Definition(context.Transaction, block);

            // A nested insertion's Position is in its owner block's microspace, so it is
            // mapped through the context transform. At the top level that transform is
            // identity and the value is exactly what it always was.
            var position = context.TransformPoint(block.Position);

            var row = new HarvestRow(
                Drawing: context.Drawing,
                Space: context.Space,
                EntityType: EntityKind.Block.ToString(),
                Name: block.Name,
                BlockDefinition: BlockInfo.Name(definition),
                Layer: block.Layer,
                Handle: handle,
                X: position.X,
                Y: position.Y,
                Z: position.Z,
                Length: null,
                Unit: context.Unit,
                UnitCode: context.UnitCode,
                IsXref: BlockInfo.IsExternalReference(definition));

            return new EntityExtraction(new[] { row }, AllAttributes(block, context, handle));
        }

        /// <summary>
        /// The reference's own attributes plus the definition's constant ones.
        ///
        /// Constant attributes never appear in <c>AttributeCollection</c> - AutoCAD reuses
        /// the definition instead of creating a copy - so a reader that only walks the
        /// collection reports <c>IsConstant: 0</c> forever and silently drops the values.
        /// Both sets join to the block row by handle; the tag says which kind it was.
        /// </summary>
        private static IReadOnlyList<HarvestAttributeRow> AllAttributes(
            BlockReference block, HarvestContext context, string handle)
        {
            List<HarvestAttributeRow> rows = Attributes(block, context, handle);

            foreach (ConstantAttribute constant in context.ConstantAttributes(BlockInfo.DefinitionId(block)))
            {
                rows.Add(new HarvestAttributeRow(
                    Drawing: context.Drawing,
                    EntityHandle: handle,
                    Tag: constant.Tag,
                    Value: TextValue.SingleLine(constant.Value),
                    IsConstant: true));
            }

            return rows;
        }

        private static List<HarvestAttributeRow> Attributes(
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
    }
}
