using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using AEC_Batch_Data_Haravester.Core;
using AcException = Autodesk.AutoCAD.Runtime.Exception;

namespace AEC_Batch_Data_Haravester.Extraction
{
    /// <summary>Placed block (mechanical equipment, furniture, doors, ...).</summary>
    public sealed class BlockExtractor : IEntityExtractor
    {
        public RXClass TargetClass => RXClass.GetClass(typeof(BlockReference));

        public IEnumerable<HarvestRow> Extract(Entity entity, HarvestContext context)
        {
            var block = (BlockReference)entity;

            yield return new HarvestRow(
                Drawing: context.Drawing,
                Space: context.Space,
                EntityType: EntityKind.Block.ToString(),
                Name: block.Name,
                BlockDefinition: DefinitionName(context.Transaction, block),
                Layer: block.Layer,
                Handle: block.Handle.ToString(),
                X: block.Position.X,
                Y: block.Position.Y,
                Z: block.Position.Z,
                Length: null,
                Unit: context.Unit,
                UnitCode: context.UnitCode);
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
