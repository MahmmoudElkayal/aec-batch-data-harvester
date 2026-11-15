using System;
using Autodesk.AutoCAD.DatabaseServices;
using AcException = Autodesk.AutoCAD.Runtime.Exception;

namespace AEC_Batch_Data_Haravester.Extraction
{
    /// <summary>
    /// Measuring an entity is the one geometric read that can legitimately fail on a valid
    /// object (a degenerate or self-intersecting curve cannot be measured). A missing
    /// length must not cost the row, so every measured extractor goes through here.
    ///
    /// This is per type rather than on the Curve base class because the managed API does
    /// not put Length on Curve: Polyline, Line and Arc each expose their own, and a circle
    /// is measured by its circumference. One helper keeps the failure policy identical
    /// without pretending the API is more uniform than it is.
    /// </summary>
    internal static class CurveLength
    {
        public static double? TryMeasure(Polyline polyline) => Try(() => polyline.Length);

        public static double? TryMeasure(Line line) => Try(() => line.Length);

        public static double? TryMeasure(Arc arc) => Try(() => arc.Length);

        public static double? TryMeasure(Circle circle) => Try(() => circle.Circumference);

        private static double? Try(Func<double> read)
        {
            try
            {
                return read();
            }
            catch (AcException)
            {
                return null;
            }
        }
    }
}
