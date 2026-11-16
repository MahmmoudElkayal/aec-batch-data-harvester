using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AEC_Batch_Data_Haravester.Core;

namespace AEC_Batch_Data_Haravester.Extraction
{
    /// <summary>Straight line - dimension witnesses, section marks, construction geometry.</summary>
    public sealed class LineExtractor : IEntityExtractor
    {
        public RXClass TargetClass => RXClass.GetClass(typeof(Line));

        public EntityExtraction Extract(Entity entity, HarvestContext context)
        {
            var line = (Line)entity;
            Point3d start = context.TransformPoint(line.StartPoint);

            // A line has two positions; the start point is the anchor, and Length carries
            // the extent. Exporting both endpoints would need a schema change.
            return EntityExtraction.FromRow(new HarvestRow(
                Drawing: context.Drawing,
                Space: context.Space,
                EntityType: EntityKind.Line.ToString(),
                Name: string.Empty,
                BlockDefinition: string.Empty,
                Layer: line.Layer,
                Handle: line.Handle.ToString(),
                X: start.X,
                Y: start.Y,
                Z: start.Z,
                Length: CurveLength.TryMeasure(line, context),
                Unit: context.Unit,
                UnitCode: context.UnitCode));
        }
    }
}
