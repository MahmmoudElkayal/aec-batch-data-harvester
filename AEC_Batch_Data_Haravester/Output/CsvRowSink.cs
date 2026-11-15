using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AEC_Batch_Data_Haravester.Core;

namespace AEC_Batch_Data_Haravester.Output
{
    /// <summary>
    /// Writes one drawing's rows to its own file, temp-then-rename.
    ///
    /// Three properties come from that one decision:
    ///  - a crash mid-drawing leaves a .tmp, never a half-written .csv that a merge would
    ///    happily publish as real data;
    ///  - a re-run replaces the drawing's file instead of appending to it, so the same
    ///    batch run twice produces the same bytes;
    ///  - drawings can be processed in any order, or in parallel later, because no two
    ///    write to the same target.
    /// </summary>
    public sealed class CsvRowSink : IRowSink
    {
        private readonly CsvFormatter _formatter;
        private readonly string _tempPath;
        private readonly StreamWriter _writer;
        private bool _completed;

        public CsvRowSink(HarvestOptions options, CsvFormatter formatter)
        {
            _formatter = formatter;

            Directory.CreateDirectory(options.RowsFolder);

            Path = System.IO.Path.Combine(options.RowsFolder, options.Key + ".csv");
            _tempPath = Path + ".tmp";

            // No BOM, so the file opens cleanly in Excel and in every CSV/parquet tool.
            _writer = new StreamWriter(
                _tempPath,
                append: false,
                encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            // Every per-drawing file is standalone; the merge drops the header from all but
            // the first, so a single _rows file is still a valid CSV on its own.
            _writer.WriteLine(_formatter.Header);
        }

        /// <summary>The published path. It does not exist until <see cref="Complete"/>.</summary>
        public string Path { get; }

        public void Write(IReadOnlyList<HarvestRow> rows)
        {
            foreach (HarvestRow row in rows)
            {
                _writer.WriteLine(_formatter.Row(row));
            }
        }

        public void Complete()
        {
            _writer.Dispose();

            // Overwrite is deliberate: a re-run of a drawing must replace its previous
            // result, not fail because one is already there.
            File.Move(_tempPath, Path, overwrite: true);
            _completed = true;
        }

        public void Dispose()
        {
            if (_completed)
            {
                return;
            }

            // Not completed: the drawing failed, so delete the partial file instead of
            // leaving something a later merge could pick up. A hard kill skips this, which
            // is why the merge only reads .csv files and the supervisor only trusts .ok.
            try
            {
                _writer.Dispose();

                if (File.Exists(_tempPath))
                {
                    File.Delete(_tempPath);
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
