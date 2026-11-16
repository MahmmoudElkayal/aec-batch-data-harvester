using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using AEC_Batch_Data_Haravester.Core;

namespace AEC_Batch_Data_Haravester.Extraction
{
    /// <summary>
    /// A hatch has no single insertion point and no length, but its area is the number a
    /// takeoff usually wants. The row therefore carries identity fields plus
    /// <c>Area</c>; layer and handle still say which hatch it was.
    ///
    /// Hatch does not derive from Curve, so it needs its own registration even though the
    /// measurement policy is the same as the curve fallback.
    /// </summary>
    public sealed class HatchExtractor : IEntityExtractor
    {
        public RXClass TargetClass => RXClass.GetClass(typeof(Hatch));

        public EntityExtraction Extract(Entity entity, HarvestContext context)
        {
            var hatch = (Hatch)entity;

            return EntityExtraction.FromRow(new HarvestRow(
                Drawing: context.Drawing,
                Space: context.Space,
                EntityType: EntityKind.Hatch.ToString(),
                Name: string.Empty,
                BlockDefinition: string.Empty,
                Layer: hatch.Layer,
                Handle: hatch.Handle.ToString(),
                X: null,
                Y: null,
                Z: null,
                Length: null,
                Area: CurveLength.TryArea(hatch, context),
                Unit: context.Unit,
                UnitCode: context.UnitCode));
        }
    }
}
