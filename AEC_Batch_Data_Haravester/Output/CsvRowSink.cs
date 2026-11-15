using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AEC_Batch_Data_Haravester.Core;

namespace AEC_Batch_Data_Haravester.Output
{
    /// <summary>
    /// Writes one drawing's rows to its own pair of files, temp-then-rename.
    ///
    /// Three properties come from that one decision:
    ///  - a crash mid-drawing leaves .tmp files, never a half-written .csv that a merge
    ///    would happily publish as real data;
    ///  - a re-run replaces the drawing's files instead of appending to them, so the same
    ///    batch run twice produces the same bytes;
    ///  - drawings can be processed in any order, or in parallel later, because no two
    ///    write to the same target.
    ///
    /// The attribute file is written even when the drawing has no attributes: the header
    /// alone is a valid, empty table, and its presence is part of the drawing's output
    /// contract, so a resume can tell a complete drawing from an interrupted one.
    /// </summary>
    public sealed class CsvRowSink : IRowSink
    {
        private readonly CsvFormatter _formatter;
        private readonly AttributeCsvFormatter _attributeFormatter;
        private readonly Encoding _encoding;
        private readonly string _tempPath;
        private readonly string _attributeTempPath;
        private readonly StreamWriter _writer;
        private readonly StreamWriter _attributeWriter;
        private bool _completed;

        public CsvRowSink(
            HarvestOptions options, CsvFormatter formatter, AttributeCsvFormatter attributeFormatter)
        {
            _formatter = formatter;
            _attributeFormatter = attributeFormatter;

            // No BOM by default, so the file opens cleanly in parsers. Windows Excel reads
            // BOM-less UTF-8 as ANSI, so AEC_HARVEST_UTF8_BOM=1 is the opt-in for that.
            _encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: options.Utf8Bom);

            Directory.CreateDirectory(options.RowsFolder);

            Path = System.IO.Path.Combine(options.RowsFolder, options.Key + ".csv");
            AttributePath = System.IO.Path.Combine(
                options.RowsFolder, options.Key + ".attributes.csv");

            _tempPath = Path + ".tmp";
            _attributeTempPath = AttributePath + ".tmp";

            _writer = Open(_tempPath);
            _attributeWriter = Open(_attributeTempPath);

            // Every per-drawing file is standalone; the merge drops the header from all but
            // the first, so a single _rows file is still a valid CSV on its own.
            _writer.WriteLine(_formatter.Header);
            _attributeWriter.WriteLine(_attributeFormatter.Header);
        }

        /// <summary>The published entity path. It does not exist until <see cref="Complete"/>.</summary>
        public string Path { get; }

        /// <summary>The published attribute path. It does not exist until <see cref="Complete"/>.</summary>
        public string AttributePath { get; }

        public void Write(IReadOnlyList<HarvestRow> rows)
        {
            foreach (HarvestRow row in rows)
            {
                _writer.WriteLine(_formatter.Row(row));
            }
        }

        public void WriteAttributes(IReadOnlyList<HarvestAttributeRow> rows)
        {
            foreach (HarvestAttributeRow row in rows)
            {
                _attributeWriter.WriteLine(_attributeFormatter.Row(row));
            }
        }

        public void Complete()
        {
            _writer.Dispose();
            _attributeWriter.Dispose();

            // Overwrite is deliberate: a re-run of a drawing must replace its previous
            // result, not fail because one is already there.
            File.Move(_tempPath, Path, overwrite: true);
            File.Move(_attributeTempPath, AttributePath, overwrite: true);
            _completed = true;
        }

        public void Dispose()
        {
            if (_completed)
            {
                return;
            }

            // Not completed: the drawing failed, so delete the partial files instead of
            // leaving something a later merge could pick up. A hard kill skips this, which
            // is why the merge only reads published .csv files and only trusts .ok.
            TryDelete(_writer, _tempPath);
            TryDelete(_attributeWriter, _attributeTempPath);
        }

        private StreamWriter Open(string path)
        {
            return new StreamWriter(path, append: false, encoding: _encoding);
        }

        private static void TryDelete(StreamWriter writer, string path)
        {
            try
            {
                writer.Dispose();

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
