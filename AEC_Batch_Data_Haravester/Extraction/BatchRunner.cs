using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Autodesk.AutoCAD.DatabaseServices;
using AEC_Batch_Data_Haravester.Core;
using AEC_Batch_Data_Haravester.Output;
using AEC_Batch_Data_Haravester.Reporting;
using AcException = Autodesk.AutoCAD.Runtime.Exception;

namespace AEC_Batch_Data_Haravester.Extraction
{
    /// <summary>
    /// Harvests a chunk of drawings in one process, each opened as a side database.
    ///
    /// Why this is not a replacement for one-process-per-drawing, but a demotion of it:
    /// AutoCAD startup is ~1.6 s per process and the work is ~10 ms per drawing. Opening a
    /// side database keeps the work and drops the startup. What it also drops is physical
    /// isolation - an access violation takes down every drawing in the chunk - so the
    /// supervisor keeps the process boundary as the second line of defence, and re-runs
    /// what did not report.
    ///
    /// Logical isolation stays here: every drawing gets its own try/catch, its own
    /// transaction and its own row file, so a corrupt drawing is one .fail and the chunk
    /// continues.
    /// </summary>
    public static class BatchRunner
    {
        private static readonly IModelSpaceResolver Resolver = new SymbolModelSpaceResolver();
        private static readonly ExtractorRegistry Registry = ExtractorRegistry.CreateDefault();

        /// <summary>Returns the number of drawings that failed. Status files are the contract.</summary>
        public static int Run(string manifestPath)
        {
            IReadOnlyList<BatchEntry> entries = BatchManifest.ReadFile(manifestPath);
            string outputFolder = HarvestOptions.ResolveOutputFolder();
            var formatter = new CsvFormatter();
            var attributeFormatter = new AttributeCsvFormatter();
            var marker = new CurrentDrawingMarker(
                Path.Combine(outputFolder, HarvestOptions.StatusFolderName));

            // A marker left by a previous run cannot belong to this one.
            marker.Clear();

            int ok = 0;
            int failed = 0;
            long engineMs = 0;

            foreach (BatchEntry entry in entries)
            {
                marker.Mark(entry.Key);

                var stopwatch = Stopwatch.StartNew();
                try
                {
                    HarvestOptions options = HarvestOptions.FromKey(entry.Key);
                    HarvestResult result = HarvestOne(entry, options, formatter, attributeFormatter);

                    stopwatch.Stop();

                    new FileStatusReporter(options).Success(
                        Path.GetFileName(entry.Path),
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "{0} rows, {1} attributes, {2} skipped, {3} unsupported from {4}",
                            result.RowsWritten, result.AttributesWritten, result.Skipped,
                            result.UnsupportedTotal, Path.GetFileName(entry.Path)),
                        stopwatch.ElapsedMilliseconds,
                        result.RowsWritten,
                        result.AttributesWritten,
                        result.Skipped,
                        result.Unsupported,
                        result.Unit,
                        result.UnitCode);

                    Console.WriteLine(string.Format(
                        CultureInfo.InvariantCulture,
                        "[AEC] {0}: {1} rows, {2} attributes, {3} unsupported in {4} ms",
                        entry.Key, result.RowsWritten, result.AttributesWritten,
                        result.UnsupportedTotal, stopwatch.ElapsedMilliseconds));

                    ok++;
                }
                catch (AcException acEx)
                {
                    stopwatch.Stop();
                    Fail(
                        entry,
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "AutoCAD error: {0} (ErrorStatus.{1})",
                            acEx.Message, acEx.ErrorStatus),
                        acEx.ToString(),
                        stopwatch.ElapsedMilliseconds);
                    failed++;
                }
                catch (System.Exception ex)
                {
                    stopwatch.Stop();
                    Fail(
                        entry,
                        string.Format(
                            CultureInfo.InvariantCulture,
                            ".NET error: {0}: {1}",
                            ex.GetType().Name, ex.Message),
                        ex.ToString(),
                        stopwatch.ElapsedMilliseconds);
                    failed++;
                }
                finally
                {
                    marker.Clear();
                    engineMs += stopwatch.ElapsedMilliseconds;
                }
            }

            WriteSummary(outputFolder, entries.Count, ok, failed, engineMs);

            return failed;
        }

        /// <summary>
        /// Everything that can fail for one drawing, in one place. The sink publishes on
        /// Complete(), so a throw anywhere before it leaves no .csv - a half-written row
        /// file is worse than a missing one, because a merge would publish it as data.
        /// </summary>
        private static HarvestResult HarvestOne(
            BatchEntry entry,
            HarvestOptions options,
            CsvFormatter formatter,
            AttributeCsvFormatter attributeFormatter)
        {
            // A side database needs no Document and cannot disturb an open drawing. Read
            // share is deliberate: it fails loudly if AutoCAD has the file open for write,
            // rather than reading a file that is being changed underneath us.
            using var db = new Database(buildDefaultDrawing: false, noDocument: true);
            db.ReadDwgFile(entry.Path, FileShare.Read, allowCPConversion: true, password: "");

            using var sink = new CsvRowSink(options, formatter, attributeFormatter);
            HarvestResult result = new DrawingScanner(Registry, Resolver, options.IncludeNestedBlocks)
                .Scan(db, Path.GetFileName(entry.Path), sink);

            sink.Complete();

            return result;
        }

        private static void Fail(BatchEntry entry, string message, string? detail, long elapsedMs)
        {
            try
            {
                new FileStatusReporter(HarvestOptions.FromKey(entry.Key))
                    .Failure(Path.GetFileName(entry.Path), message, detail, elapsedMs);
            }
            catch (System.Exception)
            {
                // No status file, so the supervisor counts the drawing as unreported and
                // writes the .fail itself. Reporting must not throw.
            }
        }

        private static void WriteSummary(
            string outputFolder, int drawings, int ok, int failed, long engineMs)
        {
            try
            {
                string statusFolder = Path.Combine(outputFolder, HarvestOptions.StatusFolderName);
                Directory.CreateDirectory(statusFolder);

                File.WriteAllText(
                    Path.Combine(statusFolder, HarvestOptions.BatchSummaryFileName),
                    BatchSummaryJson.Build(drawings, ok, failed, engineMs));
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
