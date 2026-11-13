using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;

namespace AEC_Batch_Data_Haravester
{
    /// <summary>
    /// Harvests a flat entity table from the drawing that is currently loaded.
    ///
    /// Design constraints (see docs/autocad-2026/batch-harvester-notes.md):
    ///  * Runs in AutoCAD desktop AND in accoreconsole.exe, so it must not touch
    ///    AcMgd.dll types (no Document, no Editor, no dialogs).
    ///  * HostApplicationServices.WorkingDatabase works in both hosts; there is no
    ///    MdiActiveDocument when running headless.
    ///  * Output location comes from an environment variable so nothing is hardcoded.
    /// </summary>
    public class BomExtractor
    {
        /// <summary>Set by RunPipeline.bat before launching accoreconsole.</summary>
        public const string OutputFolderVariable = "AEC_HARVEST_OUTPUT";

        /// <summary>Alternative: root folder of the project; output goes to \Output.</summary>
        public const string RootFolderVariable = "AEC_HARVEST_ROOT";

        public const string OutputFileName = "Extracted_BOM.csv";

        /// <summary>Per-drawing outcome files, so the batch runner never has to parse stdout.</summary>
        public const string StatusFolderName = "_status";

        // Markers used in the console line and inside the status file. Do not rename casually.
        public const string DoneMarker = "[AEC] DONE";
        public const string FailMarker = "[AEC] FAIL";

        private const string NumericFormat = "0.000";

        private static readonly string[] CsvHeader =
        {
            "Drawing", "EntityType", "Name", "Layer", "Handle", "X", "Y", "Z", "Length"
        };

        [CommandMethod("Extract_BOM")]
        public void ExtractBomData()
        {
            string drawingName = "(unknown)";

            try
            {
                Database db = HostApplicationServices.WorkingDatabase;
                if (db is null)
                {
                    Report(false, drawingName, "no working database - is a drawing loaded?");
                    return;
                }

                drawingName = string.IsNullOrEmpty(db.Filename)
                    ? "(unsaved drawing)"
                    : Path.GetFileName(db.Filename);

                List<string> rows = CollectRows(db, drawingName);
                string csvPath = AppendRows(rows);

                Report(true, drawingName, string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} rows from {1} -> {2}",
                    rows.Count, drawingName, csvPath));
            }
            catch (Autodesk.AutoCAD.Runtime.Exception acEx)
            {
                Report(false, drawingName, string.Format(
                    CultureInfo.InvariantCulture,
                    "AutoCAD error in {0}: {1} (ErrorStatus.{2})",
                    drawingName, acEx.Message, acEx.ErrorStatus));
            }
            catch (System.Exception ex)
            {
                Report(false, drawingName, string.Format(
                    CultureInfo.InvariantCulture,
                    ".NET error in {0}: {1}: {2}",
                    drawingName, ex.GetType().Name, ex.Message));
            }
        }

        /// <summary>
        /// Walks model space and produces one CSV row per supported entity.
        /// Read-only transaction: nothing is opened ForWrite, nothing is committed back.
        /// </summary>
        private static List<string> CollectRows(Database db, string drawingName)
        {
            var rows = new List<string>();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                if (!(tr.GetObject(db.BlockTableId, OpenMode.ForRead) is BlockTable blockTable))
                {
                    return rows;
                }

                if (!(tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead) is BlockTableRecord modelSpace))
                {
                    return rows;
                }

                foreach (ObjectId id in modelSpace)
                {
                    if (id.IsErased)
                    {
                        continue;
                    }

                    Entity? entity;
                    try
                    {
                        entity = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        // Proxy / corrupt object: skip it, keep harvesting the rest.
                        continue;
                    }

                    if (entity is null)
                    {
                        continue;
                    }

                    switch (entity)
                    {
                        // Placed block (mechanical equipment, furniture, doors, ...).
                        case BlockReference block:
                            rows.Add(Row(
                                drawingName, "Block", block.Name, block.Layer,
                                block.Handle.ToString(),
                                block.Position.X, block.Position.Y, block.Position.Z,
                                null));
                            break;

                        // Lightweight polyline (LWPOLYLINE) - walls, runs, routes.
                        case Polyline polyline:
                            rows.Add(Row(
                                drawingName, "Polyline", string.Empty, polyline.Layer,
                                polyline.Handle.ToString(),
                                null, null, null,
                                SafeLength(polyline)));
                            break;
                    }
                }

                // Read-only transaction: Commit is a no-op, but keeps the pattern uniform.
                tr.Commit();
            }

            return rows;
        }

        private static double? SafeLength(Polyline polyline)
        {
            try
            {
                return polyline.Length;
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                // Degenerate/self-intersecting polyline can fail to measure.
                return null;
            }
        }

        private static string Row(
            string drawing, string entityType, string name, string layer, string handle,
            double? x, double? y, double? z, double? length)
        {
            return string.Join(",", new[]
            {
                Escape(drawing),
                Escape(entityType),
                Escape(name),
                Escape(layer),
                Escape(handle),
                Number(x),
                Number(y),
                Number(z),
                Number(length)
            });
        }

        /// <summary>Writes the header once, then appends one row per extracted entity.</summary>
        private static string AppendRows(List<string> rows)
        {
            string folder = ResolveOutputFolder();
            Directory.CreateDirectory(folder);

            string path = Path.Combine(folder, OutputFileName);
            bool needsHeader = !File.Exists(path) || new FileInfo(path).Length == 0;

            // No BOM, so the file opens cleanly in Excel and in every CSV/parquet tool.
            using (var writer = new StreamWriter(
                       path,
                       append: true,
                       encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                if (needsHeader)
                {
                    writer.WriteLine(string.Join(",", CsvHeader));
                }

                foreach (string row in rows)
                {
                    writer.WriteLine(row);
                }
            }

            return path;
        }

        /// <summary>
        /// AEC_HARVEST_OUTPUT wins; then AEC_HARVEST_ROOT\Output;
        /// otherwise an Output folder beside the loaded assembly.
        /// </summary>
        private static string ResolveOutputFolder()
        {
            string? fromEnvironment = Environment.GetEnvironmentVariable(OutputFolderVariable);
            if (!string.IsNullOrWhiteSpace(fromEnvironment))
            {
                return fromEnvironment!;
            }

            string? root = Environment.GetEnvironmentVariable(RootFolderVariable);
            if (!string.IsNullOrWhiteSpace(root))
            {
                return Path.Combine(root!, "Output");
            }

            string assemblyDirectory =
                Path.GetDirectoryName(typeof(BomExtractor).Assembly.Location)
                ?? Directory.GetCurrentDirectory();

            return Path.Combine(assemblyDirectory, "Output");
        }

        private static string Number(double? value)
        {
            return value.HasValue
                ? value.Value.ToString(NumericFormat, CultureInfo.InvariantCulture)
                : string.Empty;
        }

        /// <summary>Minimal RFC 4180 quoting for values that contain , or " or a newline.</summary>
        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            bool mustQuote = value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0;
            return mustQuote
                ? "\"" + value.Replace("\"", "\"\"") + "\""
                : value;
        }

        /// <summary>
        /// Records the outcome twice:
        ///  1. a line on stdout (best effort - accoreconsole with redirected output drops
        ///     writes from managed code, so this is a convenience only);
        ///  2. an authoritative status file under &lt;output&gt;\_status\, which is what
        ///     RunPipeline.bat checks to decide pass/fail.
        /// Never throws: reporting must not mask the real result.
        /// </summary>
        private static void Report(bool success, string drawingName, string message)
        {
            string line = string.Format(
                CultureInfo.InvariantCulture,
                "{0} {1}",
                success ? DoneMarker : FailMarker,
                message);

            try
            {
                Console.WriteLine(line);
                Console.Out.Flush();
            }
            catch (IOException)
            {
                // No usable stdout (desktop AutoCAD). Nothing to do.
            }
            catch (ObjectDisposedException)
            {
            }

            try
            {
                string statusFolder = Path.Combine(ResolveOutputFolder(), StatusFolderName);
                Directory.CreateDirectory(statusFolder);

                string path = Path.Combine(
                    statusFolder,
                    SanitizeFileName(Path.GetFileNameWithoutExtension(drawingName))
                        + (success ? ".ok" : ".fail"));

                File.WriteAllText(path, line);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (ArgumentException)
            {
            }
        }

        /// <summary>Drawing name -> a legal Windows file name stem.</summary>
        private static string SanitizeFileName(string name)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(name.Length);

            foreach (char c in name)
            {
                builder.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            }

            string result = builder.ToString().Trim();
            return string.IsNullOrEmpty(result) ? "drawing" : result;
        }
    }
}
