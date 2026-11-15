using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using AEC_Batch_Data_Haravester.Core;

namespace AEC_Batch_Data_Haravester.Extraction
{
    /// <summary>Lightweight polyline (LWPOLYLINE) - walls, runs, routes.</summary>
    public sealed class PolylineExtractor : IEntityExtractor
    {
        public RXClass TargetClass => RXClass.GetClass(typeof(Polyline));

        public EntityExtraction Extract(Entity entity, HarvestContext context)
        {
            var polyline = (Polyline)entity;

            return EntityExtraction.FromRow(new HarvestRow(
                Drawing: context.Drawing,
                Space: context.Space,
                EntityType: EntityKind.Polyline.ToString(),
                Name: string.Empty,
                BlockDefinition: string.Empty,
                Layer: polyline.Layer,
                Handle: polyline.Handle.ToString(),
                X: null,
                Y: null,
                Z: null,
                Length: CurveLength.TryMeasure(polyline),
                Unit: context.Unit,
                UnitCode: context.UnitCode));
        }
    }
}
