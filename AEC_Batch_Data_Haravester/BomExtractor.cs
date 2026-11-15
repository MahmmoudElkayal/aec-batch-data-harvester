using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using AcException = Autodesk.AutoCAD.Runtime.Exception;

namespace AEC_Batch_Data_Haravester
{
    /// <summary>
    /// Resolves the model-space <see cref="ObjectId"/> for a database.
    /// The only reason this is an interface: a valid drawing always has a model space,
    /// so the "cannot resolve" path cannot be reached from a real file. A seam makes it
    /// testable without AutoCAD instead of leaving it unexercised until production.
    /// </summary>
    public interface IModelSpaceResolver
    {
        ObjectId Resolve(Database db);
    }

    /// <summary>The production resolver: asks the database directly, no name lookup.</summary>
    public sealed class SymbolModelSpaceResolver : IModelSpaceResolver
    {
        public ObjectId Resolve(Database db)
        {
            return SymbolUtilityServices.GetBlockModelSpaceId(db);
        }
    }

    /// <summary>
    /// The policy for an unresolved model space, isolated from AutoCAD types so it can be
    /// tested. A broken drawing must fail loudly: returning no rows would make it look
    /// exactly like an empty drawing, and the batch would report success.
    /// </summary>
    internal static class ModelSpaceGuard
    {
        internal static void EnsureResolved(bool isNull, string drawingName)
        {
            if (isNull)
            {
                throw new InvalidOperationException(
                    "model space id could not be resolved for '" + drawingName + "'");
            }
        }
    }

    /// <summary>
    /// Harvests a flat entity table from the drawing that is currently loaded.
    ///
    /// Design constraints (see docs/autocad-2026/batch-harvester-notes.md):
    ///  * Runs in AutoCAD desktop AND in accoreconsole.exe, so it must not touch
    ///    AcMgd.dll types (no Document, no Editor, no dialogs). Core Console does expose
    ///    a Document and an Editor, but only through acmgd - taking a dependency on them
    ///    would stop one DLL from loading in both hosts.
    ///  * HostApplicationServices.WorkingDatabase works in both hosts, so no host
    ///    detection is needed.
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

        /// <summary>Model space today. Paper space arrives in Phase 5 without a schema change.</summary>
        private const string ModelSpaceLabel = "Model";

        private static readonly IModelSpaceResolver DefaultResolver = new SymbolModelSpaceResolver();

        [CommandMethod("Extract_BOM")]
        public void ExtractBomData()
        {
            var stopwatch = Stopwatch.StartNew();
            string drawingName = "(unknown)";

            try
            {
                Database? db = HostApplicationServices.WorkingDatabase;
                if (db is null)
                {
                    Report(false, drawingName, "no working database - is a drawing loaded?",
                        null, stopwatch.ElapsedMilliseconds, 0, 0, null);
                    return;
                }

                drawingName = string.IsNullOrEmpty(db.Filename)
                    ? "(unsaved drawing)"
                    : Path.GetFileName(db.Filename);

                int skipped;
                List<string> rows = CollectRows(db, drawingName, DefaultResolver, out skipped);

                string csvPath = AppendRows(rows);

                Report(
                    true,
                    drawingName,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "{0} rows, {1} skipped from {2} -> {3}",
                        rows.Count, skipped, drawingName, csvPath),
                    null,
                    stopwatch.ElapsedMilliseconds,
                    rows.Count,
                    skipped,
                    db.Insunits);
            }
            catch (AcException acEx)
            {
                // ex.ToString() is kept in full: when drawing 437 of 1,000 fails, the
                // stack trace is the only thing that identifies where.
                Report(
                    false,
                    drawingName,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "AutoCAD error: {0} (ErrorStatus.{1})",
                        acEx.Message, acEx.ErrorStatus),
                    acEx.ToString(),
                    stopwatch.ElapsedMilliseconds, 0, 0, null);
            }
            catch (System.Exception ex)
            {
                Report(
                    false,
                    drawingName,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        ".NET error: {0}: {1}",
                        ex.GetType().Name, ex.Message),
                    ex.ToString(),
                    stopwatch.ElapsedMilliseconds, 0, 0, null);
            }
        }

        /// <summary>
        /// Walks model space and produces one CSV row per supported entity.
        /// Read-only transaction: nothing is opened ForWrite, nothing is committed back.
        ///
        /// Failure policy: an unresolvable model space THROWS. Returning an empty list
        /// would make a broken drawing indistinguishable from an empty one, and the
        /// caller would report success with 0 rows.
        /// </summary>
        private static List<string> CollectRows(
            Database db, string drawingName, IModelSpaceResolver resolver, out int skipped)
        {
            skipped = 0;
            var rows = new List<string>();

            string unit = db.Insunits.ToString();
            int unitCode = (int)db.Insunits;

            ObjectId modelSpaceId = resolver.Resolve(db);
            ModelSpaceGuard.EnsureResolved(modelSpaceId.IsNull, drawingName);

            using Transaction tr = db.TransactionManager.StartTransaction();

            if (!(tr.GetObject(modelSpaceId, OpenMode.ForRead) is BlockTableRecord modelSpace))
            {
                throw new InvalidOperationException(
                    "model space object is not a BlockTableRecord for '" + drawingName + "'");
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
                catch (AcException)
                {
                    // Proxy / corrupt object. Narrow catch on purpose: this is the one
                    // place where "skip it and keep harvesting" is the right recovery.
                    // It is counted, never silent. Anything else propagates to the
                    // boundary catch and fails the drawing.
                    skipped++;
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
                        rows.Add(HarvestFormat.Row(
                            drawingName, ModelSpaceLabel, "Block", block.Name,
                            BlockDefinitionName(tr, block), block.Layer,
                            block.Handle.ToString(),
                            block.Position.X, block.Position.Y, block.Position.Z,
                            null, unit, unitCode));
                        break;

                    // Lightweight polyline (LWPOLYLINE) - walls, runs, routes.
                    case Polyline polyline:
                        rows.Add(HarvestFormat.Row(
                            drawingName, ModelSpaceLabel, "Polyline", string.Empty,
                            string.Empty, polyline.Layer,
                            polyline.Handle.ToString(),
                            null, null, null,
                            SafeLength(polyline), unit, unitCode));
                        break;
                }
            }

            return rows;
        }

        /// <summary>
        /// The block definition's real name. For a dynamic block, Name is an anonymous
        /// "*U12"-style name, so the definition is resolved through DynamicBlockTableRecord.
        /// The equality of the two properties for non-dynamic blocks is undocumented,
        /// hence the explicit branch rather than relying on it.
        /// </summary>
        private static string BlockDefinitionName(Transaction tr, BlockReference block)
        {
            try
            {
                ObjectId definitionId = block.IsDynamicBlock
                    ? block.DynamicBlockTableRecord
                    : block.BlockTableRecord;

                if (definitionId.IsNull)
                {
                    return string.Empty;
                }

                return tr.GetObject(definitionId, OpenMode.ForRead) is BlockTableRecord definition
                    ? definition.Name
                    : string.Empty;
            }
            catch (AcException)
            {
                // A dangling or proxy definition must not lose the whole entity row.
                return string.Empty;
            }
        }

        private static double? SafeLength(Polyline polyline)
        {
            try
            {
                return polyline.Length;
            }
            catch (AcException)
            {
                // Degenerate/self-intersecting polyline can fail to measure.
                return null;
            }
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
                    writer.WriteLine(string.Join(",", HarvestFormat.CsvHeader));
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
                return fromEnvironment;
            }

            string? root = Environment.GetEnvironmentVariable(RootFolderVariable);
            if (!string.IsNullOrWhiteSpace(root))
            {
                return Path.Combine(root, "Output");
            }

            string assemblyDirectory =
                Path.GetDirectoryName(typeof(BomExtractor).Assembly.Location)
                ?? Directory.GetCurrentDirectory();

            return Path.Combine(assemblyDirectory, "Output");
        }

        /// <summary>
        /// Records the outcome three ways:
        ///  1. a line on stdout (best effort - accoreconsole with redirected output drops
        ///     writes from managed code, so this is a convenience only);
        ///  2. an authoritative one-line JSON status file under &lt;output&gt;\_status\,
        ///     which is what RunPipeline.bat checks to decide pass/fail;
        ///  3. on failure, the full exception text in &lt;key&gt;.error.txt, because the
        ///     status line must stay short enough to echo.
        /// Never throws: reporting must not mask the real result.
        /// </summary>
        private static void Report(
            bool success,
            string drawingName,
            string message,
            string? detail,
            long elapsedMs,
            int rows,
            int skipped,
            UnitsValue? units)
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

                string stem = HarvestFormat.SanitizeFileName(Path.GetFileNameWithoutExtension(drawingName));
                string okPath = Path.Combine(statusFolder, stem + ".ok");
                string failPath = Path.Combine(statusFolder, stem + ".fail");

                // A re-run must not leave the previous verdict lying next to the new one.
                TryDelete(success ? failPath : okPath);

                string json = HarvestFormat.BuildStatusJson(
                    success, drawingName, message, elapsedMs, rows, skipped,
                    units?.ToString(), units.HasValue ? (int?)units.Value : null);

                File.WriteAllText(success ? okPath : failPath, json);

                // Digits only, so the batch runner can echo it without escaping risks.
                if (success)
                {
                    File.WriteAllText(Path.Combine(statusFolder, stem + ".rows"),
                        rows.ToString(CultureInfo.InvariantCulture));
                }

                if (!success && !string.IsNullOrEmpty(detail))
                {
                    File.WriteAllText(
                        Path.Combine(statusFolder, stem + ".error.txt"),
                        detail);
                }
                else
                {
                    TryDelete(Path.Combine(statusFolder, stem + ".error.txt"));
                }
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

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
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
}
