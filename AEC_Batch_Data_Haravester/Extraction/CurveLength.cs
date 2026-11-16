using System;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcException = Autodesk.AutoCAD.Runtime.Exception;

namespace AEC_Batch_Data_Haravester.Extraction
{
    /// <summary>
    /// Measuring an entity is the one geometric read that can legitimately fail on a valid
    /// object (a degenerate or open curve cannot be measured). A missing measurement must
    /// not cost the row, so every measured extractor goes through here.
    ///
    /// This is per type rather than on the Curve base class because the managed API does
    /// not put Length on Curve: Polyline, Line and Arc each expose their own, and a circle
    /// is measured by its circumference. One helper keeps the failure policy identical
    /// without pretending the API is more uniform than it is.
    ///
    /// Every measurement takes the context so a nested block's length is reported in
    /// drawing units rather than in the block's microspace. A line is measured from its
    /// transformed endpoints, which stays exact under a non-uniform block scale; the other
    /// curves use the transform's scalar scale. Areas use <see cref="HarvestContext.ScaleArea"/>,
    /// which is exact for a planar affine transform.
    /// </summary>
    internal static class CurveLength
    {
        public static double? TryMeasure(Polyline polyline, HarvestContext context)
            => Try(() => context.ScaleLength(polyline.Length));

        public static double? TryMeasure(Line line, HarvestContext context)
            => Try(() =>
            {
                Point3d start = context.TransformPoint(line.StartPoint);
                Point3d end = context.TransformPoint(line.EndPoint);
                return start.DistanceTo(end);
            });

        public static double? TryMeasure(Arc arc, HarvestContext context)
            => Try(() => context.ScaleLength(arc.Length));

        public static double? TryMeasure(Circle circle, HarvestContext context)
            => Try(() => context.ScaleLength(circle.Circumference));

        /// <summary>
        /// The fallback curve measurement: distance from the start parameter to the end
        /// parameter. A ray or xline is unbounded, so its "length" is infinite and is
        /// reported blank rather than as a number no consumer can use.
        /// </summary>
        public static double? TryMeasure(Curve curve, HarvestContext context)
            => Try(() =>
            {
                double length = curve.GetDistanceAtParameter(curve.EndParam);
                return double.IsFinite(length) && length > 0.0
                    ? context.ScaleLength(length)
                    : double.NaN;
            });

        /// <summary>
        /// A lightweight polyline's area is only defined when it is closed; an open one is
        /// a route, not an enclosure, so its area stays blank instead of becoming 0.000.
        /// </summary>
        public static double? TryArea(Polyline polyline, HarvestContext context)
            => Try(() => polyline.Closed
                ? context.ScaleArea(polyline.Area)
                : double.NaN);

        /// <summary>Curve.Area throws for open curves, so a failure is a blank cell.</summary>
        public static double? TryArea(Curve curve, HarvestContext context)
            => Try(() => context.ScaleArea(curve.Area));

        public static double? TryArea(Hatch hatch, HarvestContext context)
            => Try(() => context.ScaleArea(hatch.Area));

        private static double? Try(Func<double> read)
        {
            try
            {
                double value = read();
                return double.IsFinite(value) ? value : (double?)null;
            }
            catch (AcException)
            {
                return null;
            }
        }
    }
}
