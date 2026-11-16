using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AEC_Batch_Data_Haravester.Core;

namespace AEC_Batch_Data_Haravester.Extraction
{
    /// <summary>
    /// Single-line text. The text itself goes in the Name column - it is the only free
    /// text column, and a row whose content is invisible would add a count without adding
    /// data. Position is the insertion point.
    /// </summary>
    public sealed class DbTextExtractor : IEntityExtractor
    {
        public RXClass TargetClass => RXClass.GetClass(typeof(DBText));

        public EntityExtraction Extract(Entity entity, HarvestContext context)
        {
            var text = (DBText)entity;
            Point3d position = context.TransformPoint(text.Position);

            return EntityExtraction.FromRow(new HarvestRow(
                Drawing: context.Drawing,
                Space: context.Space,
                EntityType: EntityKind.DBText.ToString(),
                Name: TextValue.SingleLine(text.TextString),
                BlockDefinition: string.Empty,
                Layer: text.Layer,
                Handle: text.Handle.ToString(),
                X: position.X,
                Y: position.Y,
                Z: position.Z,
                Length: null,
                Unit: context.Unit,
                UnitCode: context.UnitCode));
        }
    }
}
