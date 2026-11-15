using System;
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
    /// Harvests the drawing that is currently loaded, then reports the outcome. This is
    /// the only place that owns the try/catch boundary; everything below it either returns
    /// a result or throws.
    /// </summary>
    public static class HarvestRunner
    {
        private static readonly IModelSpaceResolver Resolver = new SymbolModelSpaceResolver();
        private static readonly ExtractorRegistry Registry = ExtractorRegistry.CreateDefault();

        public static void Run()
        {
            var stopwatch = Stopwatch.StartNew();
            string drawingName = "(unknown)";
            HarvestOptions? options = null;

            try
            {
                Database? db = HostApplicationServices.WorkingDatabase;
                if (db is null)
                {
                    options = HarvestOptions.FromEnvironment(drawingName);
                    new FileStatusReporter(options).Failure(
                        drawingName, "no working database - is a drawing loaded?", null,
                        stopwatch.ElapsedMilliseconds);
                    return;
                }

                drawingName = string.IsNullOrEmpty(db.Filename)
                    ? "(unsaved drawing)"
                    : Path.GetFileName(db.Filename);

                options = HarvestOptions.FromEnvironment(drawingName);
                var reporter = new FileStatusReporter(options);
                var formatter = new CsvFormatter();

                using var sink = new CsvRowSink(options, formatter);

                HarvestResult result = new ModelSpaceScanner(Registry, Resolver)
                    .Scan(db, drawingName, sink);

                sink.Complete();

                reporter.Success(
                    drawingName,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "{0} rows, {1} skipped from {2} -> {3}",
                        result.RowsWritten, result.Skipped, drawingName, sink.Path),
                    stopwatch.ElapsedMilliseconds,
                    result.RowsWritten,
                    result.Skipped,
                    db.Insunits.ToString(),
                    (int)db.Insunits);
            }
            catch (AcException acEx)
            {
                // ex.ToString() is kept in full: when drawing 437 of 1,000 fails, the
                // stack trace is the only thing that identifies where.
                ReportFailure(
                    options,
                    drawingName,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "AutoCAD error: {0} (ErrorStatus.{1})",
                        acEx.Message, acEx.ErrorStatus),
                    acEx.ToString(),
                    stopwatch.ElapsedMilliseconds);
            }
            catch (System.Exception ex)
            {
                ReportFailure(
                    options,
                    drawingName,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        ".NET error: {0}: {1}",
                        ex.GetType().Name, ex.Message),
                    ex.ToString(),
                    stopwatch.ElapsedMilliseconds);
            }
        }

        /// <summary>
        /// Options can be unavailable (no harvest key, unusable output folder). That must
        /// still fail the drawing loudly, so say it on stderr and write no .ok - the
        /// supervisor treats a missing status file as failure by design.
        /// </summary>
        private static void ReportFailure(
            HarvestOptions? options, string drawingName, string message, string? detail, long elapsedMs)
        {
            if (options is not null)
            {
                new FileStatusReporter(options).Failure(drawingName, message, detail, elapsedMs);
                return;
            }

            try
            {
                Console.Error.WriteLine(FileStatusReporter.FailMarker + " " + message);
                Console.Error.Flush();

                if (!string.IsNullOrEmpty(detail))
                {
                    Console.Error.WriteLine(detail);
                    Console.Error.Flush();
                }
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
