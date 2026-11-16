using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AEC_Batch_Data_Haravester.Core;

namespace AEC_Batch_Data_Haravester.Extraction
{
    /// <summary>
    /// Full circle. Position is the centre; Length is the circumference, so a circle is
    /// measured the same way as every other curve rather than given a radius field that
    /// only this type would use.
    /// </summary>
    public sealed class CircleExtractor : IEntityExtractor
    {
        public RXClass TargetClass => RXClass.GetClass(typeof(Circle));

        public EntityExtraction Extract(Entity entity, HarvestContext context)
        {
            var circle = (Circle)entity;
            Point3d center = context.TransformPoint(circle.Center);

            return EntityExtraction.FromRow(new HarvestRow(
                Drawing: context.Drawing,
                Space: context.Space,
                EntityType: EntityKind.Circle.ToString(),
                Name: string.Empty,
                BlockDefinition: string.Empty,
                Layer: circle.Layer,
                Handle: circle.Handle.ToString(),
                X: center.X,
                Y: center.Y,
                Z: center.Z,
                Length: CurveLength.TryMeasure(circle, context),
                Unit: context.Unit,
                UnitCode: context.UnitCode));
        }
    }
}
