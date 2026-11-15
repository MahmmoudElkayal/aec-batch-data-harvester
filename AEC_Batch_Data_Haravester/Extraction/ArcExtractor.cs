using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using AEC_Batch_Data_Haravester.Core;

namespace AEC_Batch_Data_Haravester.Extraction
{
    /// <summary>Circular arc. Position is the centre; Length is the arc length.</summary>
    public sealed class ArcExtractor : IEntityExtractor
    {
        public RXClass TargetClass => RXClass.GetClass(typeof(Arc));

        public EntityExtraction Extract(Entity entity, HarvestContext context)
        {
            var arc = (Arc)entity;

            return EntityExtraction.FromRow(new HarvestRow(
                Drawing: context.Drawing,
                Space: context.Space,
                EntityType: EntityKind.Arc.ToString(),
                Name: string.Empty,
                BlockDefinition: string.Empty,
                Layer: arc.Layer,
                Handle: arc.Handle.ToString(),
                X: arc.Center.X,
                Y: arc.Center.Y,
                Z: arc.Center.Z,
                Length: CurveLength.TryMeasure(arc),
                Unit: context.Unit,
                UnitCode: context.UnitCode));
        }
    }
}
