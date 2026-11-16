using System;
using System.Globalization;
using System.IO;

namespace AEC_Batch_Data_Haravester.Reporting
{
    /// <summary>
    /// A fixed-name file naming the drawing the engine is working on right now.
    ///
    /// It exists for one reason: when the process is killed - a crash, or the supervisor's
    /// timeout - nobody is left to say which drawing caused it. Without this, a supervisor
    /// can only report "something in this chunk died", and the next run repeats it. With
    /// it, the culprit is one <c>.fail</c> and the rest of the chunk proceeds.
    ///
    /// Best effort throughout: an unwritable marker must not fail a drawing that is
    /// otherwise fine. The supervisor falls back to the first unreported drawing.
    /// </summary>
    public sealed class CurrentDrawingMarker
    {
        private readonly string _path;

        public CurrentDrawingMarker(string statusFolder)
        {
            _path = System.IO.Path.Combine(statusFolder, Core.HarvestOptions.CurrentMarkerFileName);
        }

        public void Mark(string key)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, key);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        public void Clear()
        {
            try
            {
                if (File.Exists(_path))
                {
                    File.Delete(_path);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// The engine's own account of a chunk, in one line. Pure string work so the numbers
    /// the Phase 4 acceptance gate reads can be checked without a CAD host.
    /// </summary>
    public static class BatchSummaryJson
    {
        public static string Build(int drawings, int ok, int failed, long engineMs)
        {
            double perDrawing = drawings > 0 ? (double)engineMs / drawings : 0.0;

            return "{"
                + "\"drawings\":" + drawings.ToString(CultureInfo.InvariantCulture)
                + ",\"ok\":" + ok.ToString(CultureInfo.InvariantCulture)
                + ",\"failed\":" + failed.ToString(CultureInfo.InvariantCulture)
                + ",\"engineMs\":" + engineMs.ToString(CultureInfo.InvariantCulture)
                + ",\"perDrawingMs\":" + perDrawing.ToString("0.0", CultureInfo.InvariantCulture)
                + "}";
        }
    }
}
