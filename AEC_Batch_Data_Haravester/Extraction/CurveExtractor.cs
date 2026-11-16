using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AEC_Batch_Data_Haravester.Core;
using AcException = Autodesk.AutoCAD.Runtime.Exception;

namespace AEC_Batch_Data_Haravester.Extraction
{
    /// <summary>
    /// The fallback for every curve the harvester does not name individually: 2D/3D
    /// polylines, ellipses, splines, rays, xlines, helices, and any future Curve subclass.
    /// Registering one class here is what closes the "eight new extractors" gap: the
    /// registry matches by inheritance from the entity's class upward and stops at the
    /// first hit, so Line, Arc, Circle and LWPOLYLINE keep their specific extractors.
    ///
    /// The row's EntityType is the DXF name in CamelCase ("Ellipse", "Spline", "Ray")
    /// rather than "Curve", so a type that leaves the unsupported census keeps a name a
    /// reader can act on. A ray or xline has no finite length and no area; those cells stay
    /// blank while the row still records that the object exists, on which layer.
    /// </summary>
    public sealed class CurveExtractor : IEntityExtractor
    {
        public RXClass TargetClass => RXClass.GetClass(typeof(Curve));

        public EntityExtraction Extract(Entity entity, HarvestContext context)
        {
            var curve = (Curve)entity;
            Point3d? anchor = Anchor(curve, context);

            return EntityExtraction.FromRow(new HarvestRow(
                Drawing: context.Drawing,
                Space: context.Space,
                EntityType: TypeName(entity),
                Name: string.Empty,
                BlockDefinition: string.Empty,
                Layer: curve.Layer,
                Handle: curve.Handle.ToString(),
                X: anchor?.X,
                Y: anchor?.Y,
                Z: anchor?.Z,
                Length: CurveLength.TryMeasure(curve, context),
                Area: CurveLength.TryArea(curve, context),
                Unit: context.Unit,
                UnitCode: context.UnitCode));
        }

        /// <summary>
        /// The DXF name in CamelCase, so a fallback row says which kind of curve it is.
        /// </summary>
        private static string TypeName(Entity entity)
        {
            string? dxf = entity.GetRXClass().DxfName;
            if (string.IsNullOrWhiteSpace(dxf))
            {
                return EntityKind.Curve.ToString();
            }

            return char.ToUpperInvariant(dxf![0]) + dxf.Substring(1).ToLowerInvariant();
        }

        /// <summary>
        /// Curve.StartPoint is the one anchor every curve publishes, but some proxy curves
        /// throw. A missing anchor leaves X/Y/Z blank; the row still exists.
        /// </summary>
        private static Point3d? Anchor(Curve curve, HarvestContext context)
        {
            try
            {
                return context.TransformPoint(curve.StartPoint);
            }
            catch (AcException)
            {
                return null;
            }
        }
    }
}
