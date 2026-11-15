using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using AEC_Batch_Data_Haravester.Core;

namespace AEC_Batch_Data_Haravester.Extraction
{
    /// <summary>
    /// 2D solid / filled area - section markers and arrow heads. It has no insertion point
    /// or length in the schema's sense, so only identity fields are exported; the row is
    /// still the evidence that the object exists and which layer it belongs to.
    /// </summary>
    public sealed class SolidExtractor : IEntityExtractor
    {
        public RXClass TargetClass => RXClass.GetClass(typeof(Solid));

        public EntityExtraction Extract(Entity entity, HarvestContext context)
        {
            var solid = (Solid)entity;

            return EntityExtraction.FromRow(new HarvestRow(
                Drawing: context.Drawing,
                Space: context.Space,
                EntityType: EntityKind.Solid.ToString(),
                Name: string.Empty,
                BlockDefinition: string.Empty,
                Layer: solid.Layer,
                Handle: solid.Handle.ToString(),
                X: null,
                Y: null,
                Z: null,
                Length: null,
                Unit: context.Unit,
                UnitCode: context.UnitCode));
        }
    }
}
