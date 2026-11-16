using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AEC_Batch_Data_Haravester.Core;

namespace AEC_Batch_Data_Haravester.Extraction
{
    /// <summary>
    /// Multi-line text. Name carries the plain text (not the formatting codes): the point
    /// of extracting these rows is the text they contain. Newlines are CSV-escaped by the
    /// formatter, so a multi-paragraph note stays one row.
    /// </summary>
    public sealed class MTextExtractor : IEntityExtractor
    {
        public RXClass TargetClass => RXClass.GetClass(typeof(MText));

        public EntityExtraction Extract(Entity entity, HarvestContext context)
        {
            var text = (MText)entity;
            Point3d location = context.TransformPoint(text.Location);

            return EntityExtraction.FromRow(new HarvestRow(
                Drawing: context.Drawing,
                Space: context.Space,
                EntityType: EntityKind.MText.ToString(),
                Name: TextValue.SingleLine(text.Text),
                BlockDefinition: string.Empty,
                Layer: text.Layer,
                Handle: text.Handle.ToString(),
                X: location.X,
                Y: location.Y,
                Z: location.Z,
                Length: null,
                Unit: context.Unit,
                UnitCode: context.UnitCode));
        }
    }
}
