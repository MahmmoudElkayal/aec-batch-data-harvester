using System.Text.Json;
using AEC_Batch_Data_Haravester.Core;
using AEC_Batch_Data_Haravester.Output;
using AEC_Batch_Data_Haravester.Reporting;

namespace Harvest.Checks
{
    /// <summary>
    /// Acceptance checks for Phases 1 and 2. Every one of them is a bug that shipped and
    /// was invisible until it bit: a broken drawing reported as success, a status line that
    /// could not be parsed, a schema that could drift from its header.
    ///
    /// These do not need AutoCAD. Phase 2 put the schema, the escaping and the status
    /// payload in Core/, Output/ and Reporting/, none of which references an Autodesk type,
    /// so a plain console process can exercise them - and the fact that this program runs at
    /// all is evidence for that separation.
    /// </summary>
    internal static class Program
    {
        private static int _passed;
        private static int _failed;

        private static int Main(string[] args)
        {
            if (args.Contains("--self-test"))
            {
                return SelfTest();
            }

            Console.WriteLine("Harvest.Checks - acceptance for Phases 1-2");
            Console.WriteLine();

            GuardChecks();
            StatusJsonChecks();
            CoverageChecks();
            CsvFormatChecks();
            KeyChecks();
            ManifestChecks();
            TimeoutVerdictChecks();
            BatchSummaryChecks();
            ChunkFileChecks();
            SchemaChecks();

            Console.WriteLine();
            Console.WriteLine($"passed: {_passed}   failed: {_failed}");

            return _failed == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------------ guard

        private static void GuardChecks()
        {
            Section("model space guard");

            Check("unresolved model space throws", () =>
            {
                var ex = Throws<InvalidOperationException>(
                    () => ModelSpaceGuard.EnsureResolved(isNull: true, "Broken.dwg"));

                return ex.Message.Contains("Broken.dwg", StringComparison.Ordinal)
                    ? null
                    : "the message does not name the drawing: " + ex.Message;
            });

            Check("resolved model space does not throw", () =>
            {
                ModelSpaceGuard.EnsureResolved(isNull: false, "Fine.dwg");
                return null;
            });
        }

        // ------------------------------------------------------------- status JSON

        private static void StatusJsonChecks()
        {
            Section("status JSON");

            Check("success payload parses and carries every field", () =>
            {
                string json = StatusJson.Build(
                    success: true,
                    drawingName: "House Designer.dwg",
                    message: "20 rows, 0 skipped",
                    elapsedMs: 25,
                    rows: 20,
                    attributes: 0,
                    skipped: 0,
                    unsupported: null,
                    unitName: "Inches",
                    unitCode: 1);

                using JsonDocument doc = Parse(json);
                JsonElement root = doc.RootElement;

                return Expect(root.GetProperty("ok").GetBoolean(), "ok should be true")
                    ?? Expect(root.GetProperty("drawing").GetString() == "House Designer.dwg", "drawing")
                    ?? Expect(root.GetProperty("rows").GetInt32() == 20, "rows")
                    ?? Expect(root.GetProperty("attrs").GetInt32() == 0, "attrs")
                    ?? Expect(root.GetProperty("skipped").GetInt32() == 0, "skipped")
                    ?? Expect(root.GetProperty("ms").GetInt64() == 25, "ms")
                    ?? Expect(root.GetProperty("units").GetString() == "Inches", "units")
                    ?? Expect(root.GetProperty("unitCode").GetInt32() == 1, "unitCode");
            });

            Check("failure payload omits units and reports zero rows", () =>
            {
                string json = StatusJson.Build(
                    success: false,
                    drawingName: "Broken.dwg",
                    message: "AutoCAD error: Drawing file is not valid.",
                    elapsedMs: 3,
                    rows: 0,
                    attributes: 0,
                    skipped: 0,
                    unsupported: null,
                    unitName: null,
                    unitCode: null);

                using JsonDocument doc = Parse(json);
                JsonElement root = doc.RootElement;

                return Expect(!root.GetProperty("ok").GetBoolean(), "ok should be false")
                    ?? Expect(!root.TryGetProperty("units", out _), "units must be absent on failure")
                    ?? Expect(!root.TryGetProperty("unitCode", out _), "unitCode must be absent on failure")
                    ?? Expect(root.GetProperty("rows").GetInt32() == 0, "rows");
            });

            Check("hostile drawing name survives the round trip", () =>
            {
                // A drawing name is a file name, so a quote or a control character is
                // unlikely - but the one-line status file must stay parseable when it
                // happens, because a parse failure looks exactly like a missing file.
                const string name = "a\"b\\c\nd\te\u0001f.dwg";

                string json = StatusJson.Build(
                    success: true, drawingName: name, message: "m", elapsedMs: 1,
                    rows: 1, attributes: 0, skipped: 0, unsupported: null,
                    unitName: "Millimeters", unitCode: 4);

                using JsonDocument doc = Parse(json);
                string? readBack = doc.RootElement.GetProperty("drawing").GetString();

                return Expect(readBack == name, "drawing name changed: " + readBack);
            });

            Check("the payload is a single line", () =>
            {
                string json = StatusJson.Build(
                    success: true, drawingName: "line\nbreak.dwg", message: "m\nmessage",
                    elapsedMs: 0, rows: 0, attributes: 0, skipped: 0, unsupported: null,
                    unitName: null, unitCode: null);

                return Expect(json.IndexOfAny(new[] { '\r', '\n' }) < 0,
                    "raw newline in the status file");
            });

            Check("the verdict carries the schema and tool version", () =>
            {
                string json = StatusJson.Build(
                    success: true, drawingName: "x.dwg", message: "m", elapsedMs: 0,
                    rows: 0, attributes: 0, skipped: 0, unsupported: null,
                    unitName: null, unitCode: null);

                using JsonDocument doc = Parse(json);
                JsonElement root = doc.RootElement;

                return Expect(root.GetProperty("schema").GetInt32() == HarvestSchema.Version, "schema")
                    ?? Expect(root.GetProperty("tool").GetString() == HarvestSchema.ToolVersion, "tool")
                    ?? Expect(!HarvestSchema.ToolVersion.Contains('+'), "tool must not carry a build hash");
            });

            Check("unsupported classes are counted, sorted and escaped", () =>
            {
                var counts = new Dictionary<string, int>
                {
                    ["HATCH"] = 12,
                    ["DIMENSION"] = 4,
                    ["ACAD_PROXY_ENTITY"] = 2
                };

                string json = StatusJson.Build(
                    success: true, drawingName: "plan.dwg", message: "m", elapsedMs: 9,
                    rows: 10, attributes: 0, skipped: 0, unsupported: counts,
                    unitName: null, unitCode: null);

                using JsonDocument doc = Parse(json);
                JsonElement root = doc.RootElement;
                JsonElement unsupported = root.GetProperty("unsupported");
                string[] names = unsupported.EnumerateObject().Select(p => p.Name).ToArray();

                return Expect(unsupported.GetProperty("HATCH").GetInt32() == 12, "HATCH count")
                    ?? Expect(root.GetProperty("unsupportedTotal").GetInt32() == 18, "total")
                    ?? Expect(names.SequenceEqual(new[] { "ACAD_PROXY_ENTITY", "DIMENSION", "HATCH" }),
                        "keys must be ordinal-sorted: " + string.Join(",", names));
            });

            Check("an empty census is an empty object, not a missing field", () =>
            {
                string json = StatusJson.Build(
                    success: true, drawingName: "clean.dwg", message: "m", elapsedMs: 1,
                    rows: 1, attributes: 0, skipped: 0, unsupported: null,
                    unitName: null, unitCode: null);

                using JsonDocument doc = Parse(json);
                JsonElement root = doc.RootElement;

                return Expect(root.GetProperty("unsupported").ValueKind == JsonValueKind.Object,
                        "unsupported must be an object")
                    ?? Expect(!root.GetProperty("unsupported").EnumerateObject().Any(), "object must be empty")
                    ?? Expect(root.GetProperty("unsupportedTotal").GetInt32() == 0, "total must be 0");
            });
        }

        // --------------------------------------------------------------- coverage

        private static void CoverageChecks()
        {
            Section("unsupported census");

            Check("counts add up and stay separate by class", () =>
            {
                var census = new UnsupportedEntities();
                census.Add("HATCH");
                census.Add("HATCH");
                census.Add(null);

                IReadOnlyDictionary<string, int> counts = census.ToSortedDictionary();

                return Expect(census.Total == 3, "total")
                    ?? Expect(counts["HATCH"] == 2, "HATCH")
                    ?? Expect(counts[UnsupportedEntities.UnresolvedClass] == 1, "unresolved")
                    ?? Expect(counts.Keys.SequenceEqual(
                           new[] { UnsupportedEntities.UnresolvedClass, "HATCH" }), "sorted keys");
            });

            Check("merging two layout censuses sums both", () =>
            {
                var model = new UnsupportedEntities();
                model.Add("DIMENSION");

                var paper = new UnsupportedEntities();
                paper.Add("DIMENSION");
                paper.Add("RAY");

                model.Merge(paper);

                IReadOnlyDictionary<string, int> counts = model.ToSortedDictionary();

                return Expect(model.Total == 3, "total")
                    ?? Expect(counts["DIMENSION"] == 2, "DIMENSION")
                    ?? Expect(counts["RAY"] == 1, "RAY");
            });

            Check("a blank or missing name is never dropped", () =>
            {
                var census = new UnsupportedEntities();
                census.Add("   ");
                census.Add(null);

                return Expect(census.Total == 2, "total")
                    ?? Expect(census.ToSortedDictionary()[UnsupportedEntities.UnresolvedClass] == 2,
                        "unresolved bucket");
            });
        }

        // ------------------------------------------------------------- CSV format

        private static void CsvFormatChecks()
        {
            Section("CSV formatting");

            Check("plain values are not quoted", () =>
                Expect(CsvFormatter.Escape("Layer 0") == "Layer 0", "unnecessary quoting"));

            Check("empty stays empty", () =>
                Expect(CsvFormatter.Escape(string.Empty) == string.Empty, "empty field"));

            Check("comma forces quoting", () =>
                Expect(CsvFormatter.Escape("a,b") == "\"a,b\"", "comma not quoted"));

            Check("quote is doubled", () =>
                Expect(CsvFormatter.Escape("6\" pipe") == "\"6\"\" pipe\"", "quote not doubled"));

            Check("newline forces quoting", () =>
                Expect(CsvFormatter.Escape("a\nb") == "\"a\nb\"", "newline not quoted"));

            Check("numbers are invariant with three decimals", () =>
                Expect(CsvFormatter.Number(null) == string.Empty, "null number")
                ?? Expect(CsvFormatter.Number(0) == "0.000", "zero")
                ?? Expect(CsvFormatter.Number(1.5) == "1.500", "1.5")
                ?? Expect(CsvFormatter.Number(-2.25) == "-2.250", "-2.25")
                ?? Expect(CsvFormatter.Number(1234.5678) == "1234.568", "rounding"));

            Check("the Excel guard prefixes only the five risky characters", () =>
                Expect(CsvFormatter.ExcelGuard("=SUM(A1)") == "'=SUM(A1)", "equals")
                ?? Expect(CsvFormatter.ExcelGuard("+12") == "'+12", "plus")
                ?? Expect(CsvFormatter.ExcelGuard("-Layer") == "'-Layer", "minus")
                ?? Expect(CsvFormatter.ExcelGuard("@handle") == "'@handle", "at")
                ?? Expect(CsvFormatter.ExcelGuard("\tindent") == "'\tindent", "tab")
                ?? Expect(CsvFormatter.ExcelGuard("Layer 0") == "Layer 0", "plain text")
                ?? Expect(CsvFormatter.ExcelGuard(string.Empty) == string.Empty, "empty"));
        }

        // ------------------------------------------------------------- status keys

        private static void KeyChecks()
        {
            Section("status key");

            Check("the key is read from the environment verbatim", () =>
            {
                return WithKey("0007-Plan-3fa11b4c", () =>
                {
                    HarvestOptions options = HarvestOptions.FromEnvironment();
                    return Expect(options.Key == "0007-Plan-3fa11b4c",
                        "key changed: " + options.Key);
                });
            });

            Check("a missing key fails instead of falling back to the file name", () =>
            {
                return WithKey(null, () =>
                {
                    Throws<InvalidOperationException>(() => HarvestOptions.FromEnvironment());
                    return null;
                });
            });

            Check("a blank key fails too", () =>
            {
                return WithKey("   ", () =>
                {
                    Throws<InvalidOperationException>(() => HarvestOptions.FromEnvironment());
                    return null;
                });
            });

            Check("the opt-in switches are on only for exactly \"1\"", () =>
            {
                return WithKey("K-00000001", () =>
                {
                    string? previousSafe = Environment.GetEnvironmentVariable(
                        HarvestOptions.ExcelSafeVariable);
                    string? previousBom = Environment.GetEnvironmentVariable(
                        HarvestOptions.Utf8BomVariable);

                    try
                    {
                        Environment.SetEnvironmentVariable(HarvestOptions.ExcelSafeVariable, "1");
                        Environment.SetEnvironmentVariable(HarvestOptions.Utf8BomVariable, "true");

                        HarvestOptions options = HarvestOptions.FromEnvironment();

                        // "true" must not switch a behaviour that rewrites values: a typo
                        // should leave the default alone, not silently turn the guard on.
                        return Expect(options.ExcelSafe, "excel-safe should be on for \"1\"")
                            ?? Expect(!options.Utf8Bom, "a BOM needs \"1\", not \"true\"");
                    }
                    finally
                    {
                        Environment.SetEnvironmentVariable(
                            HarvestOptions.ExcelSafeVariable, previousSafe);
                        Environment.SetEnvironmentVariable(
                            HarvestOptions.Utf8BomVariable, previousBom);
                    }
                });
            });
        }

        private static string? WithKey(string? value, Func<string?> body)
        {
            string? previous = Environment.GetEnvironmentVariable(HarvestOptions.KeyVariable);
            try
            {
                Environment.SetEnvironmentVariable(HarvestOptions.KeyVariable, value);
                return body();
            }
            finally
            {
                Environment.SetEnvironmentVariable(HarvestOptions.KeyVariable, previous);
            }
        }

        // --------------------------------------------------------- batch manifest

        private static void ManifestChecks()
        {
            Section("batch manifest");

            Check("key|path lines parse in order", () =>
            {
                var entries = BatchManifest.Parse(new[]
                {
                    "House Designer-bbfb5689|C:\\In\\House Designer.dwg",
                    "Plan-f65fe327|C:\\In\\Plan.dwg"
                });

                return Expect(entries.Count == 2, "count")
                    ?? Expect(entries[0].Key == "House Designer-bbfb5689", "first key")
                    ?? Expect(entries[0].Path == "C:\\In\\House Designer.dwg", "first path");
            });

            Check("blank lines and comments are ignored", () =>
            {
                var entries = BatchManifest.Parse(new[]
                {
                    "",
                    "   ",
                    "# a comment",
                    "K-00000000|C:\\a.dwg"
                });

                return Expect(entries.Count == 1, "count: " + entries.Count);
            });

            Check("a line without a separator is rejected by line number", () =>
            {
                var ex = Throws<FormatException>(() => BatchManifest.Parse(new[]
                {
                    "K-00000000|C:\\a.dwg",
                    "not a manifest line"
                }));

                return Expect(ex.Message.Contains("line 2", StringComparison.Ordinal),
                    "does not name the line: " + ex.Message);
            });

            Check("an empty path is rejected", () =>
            {
                var ex = Throws<FormatException>(
                    () => BatchManifest.Parse(new[] { "K-00000000|" }));

                return Expect(ex.Message.Contains("not 'key|path'", StringComparison.Ordinal),
                    ex.Message);
            });

            Check("a key that is not a file name is rejected", () =>
            {
                // A key becomes <key>.csv and <key>.ok. A separator or colon in it would
                // put the status file somewhere the supervisor never looks.
                foreach (string bad in new[] { "a/b", "a\\b", "a:b", "a?b", "a*b" })
                {
                    var ex = Throws<FormatException>(
                        () => BatchManifest.Parse(new[] { bad + "|C:\\a.dwg" }));

                    if (!ex.Message.Contains("unusable key", StringComparison.Ordinal))
                    {
                        return "wrong rejection for '" + bad + "': " + ex.Message;
                    }
                }

                return null;
            });

            Check("a duplicate key is rejected", () =>
            {
                var ex = Throws<FormatException>(() => BatchManifest.Parse(new[]
                {
                    "K-00000000|C:\\a.dwg",
                    "K-00000000|C:\\b.dwg"
                }));

                return Expect(ex.Message.Contains("repeats the key", StringComparison.Ordinal),
                    ex.Message);
            });
        }

        // ---------------------------------------------------------- timeout verdict

        private static void TimeoutVerdictChecks()
        {
            Section("who a killed process is blamed on");

            var chunk = new[]
            {
                new BatchEntry("A", "a.dwg"),
                new BatchEntry("B", "b.dwg"),
                new BatchEntry("C", "c.dwg")
            };

            Check("the in-flight drawing is blamed", () =>
            {
                string? key = TimeoutVerdict.Choose("B", chunk, _ => false);
                return Expect(key == "B", "chose " + key);
            });

            Check("a drawing that already reported is never blamed", () =>
            {
                // The marker can outlive the drawing it names: killed between writing the
                // .ok and clearing the marker. Blaming it would overwrite a good result.
                string? key = TimeoutVerdict.Choose("A", chunk, k => k == "A");
                return Expect(key == "B", "chose " + key);
            });

            Check("with no marker the first unreported drawing is blamed", () =>
            {
                string? key = TimeoutVerdict.Choose(null, chunk, k => k == "A");
                return Expect(key == "B", "chose " + key);
            });

            Check("a marker outside the chunk is ignored", () =>
            {
                string? key = TimeoutVerdict.Choose("Z", chunk, _ => false);
                return Expect(key == "A", "chose " + key);
            });

            Check("nothing is blamed when everything reported", () =>
            {
                string? key = TimeoutVerdict.Choose("C", chunk, _ => true);
                return Expect(key is null, "chose " + key);
            });
        }

        // ------------------------------------------------------------ batch summary

        private static void BatchSummaryChecks()
        {
            Section("batch summary");

            Check("counts and per-drawing engine time parse", () =>
            {
                using JsonDocument doc = Parse(BatchSummaryJson.Build(3, 2, 1, 103));
                JsonElement root = doc.RootElement;

                return Expect(root.GetProperty("drawings").GetInt32() == 3, "drawings")
                    ?? Expect(root.GetProperty("ok").GetInt32() == 2, "ok")
                    ?? Expect(root.GetProperty("failed").GetInt32() == 1, "failed")
                    ?? Expect(root.GetProperty("engineMs").GetInt64() == 103, "engineMs")
                    ?? Expect(Math.Abs(root.GetProperty("perDrawingMs").GetDouble() - 34.3) < 0.05,
                        "perDrawingMs");
            });

            Check("an empty chunk has no divide by zero", () =>
            {
                using JsonDocument doc = Parse(BatchSummaryJson.Build(0, 0, 0, 0));
                return Expect(doc.RootElement.GetProperty("perDrawingMs").GetDouble() == 0.0,
                    "perDrawingMs");
            });
        }

        // ------------------------------------------------------------ chunk files

        private static void ChunkFileChecks()
        {
            Section("parallel chunk files");

            Check("a chunk id suffixes the files a supervisor reads", () =>
            {
                Environment.SetEnvironmentVariable(HarvestOptions.ChunkIdVariable, "007");
                try
                {
                    return Expect(HarvestOptions.CurrentMarkerFileName == "_current-007.txt",
                            "marker was " + HarvestOptions.CurrentMarkerFileName)
                        ?? Expect(HarvestOptions.BatchSummaryFileName == "_batch-007.json",
                            "summary was " + HarvestOptions.BatchSummaryFileName)
                        ?? Expect(HarvestOptions.DiagnosticFileName == "_harvest-007.error.txt",
                            "diagnostic was " + HarvestOptions.DiagnosticFileName);
                }
                finally
                {
                    Environment.SetEnvironmentVariable(HarvestOptions.ChunkIdVariable, null);
                }
            });

            Check("no chunk id keeps the historical file names", () =>
            {
                Environment.SetEnvironmentVariable(HarvestOptions.ChunkIdVariable, null);
                return Expect(HarvestOptions.CurrentMarkerFileName == "_current.txt", "marker")
                    ?? Expect(HarvestOptions.BatchSummaryFileName == "_batch.json", "summary")
                    ?? Expect(HarvestOptions.DiagnosticFileName == "_harvest.error.txt",
                        "diagnostic");
            });

            Check("an id that is not a file name is sanitised", () =>
            {
                Environment.SetEnvironmentVariable(HarvestOptions.ChunkIdVariable, "../a b\\c");
                try
                {
                    return Expect(HarvestOptions.CurrentMarkerFileName == "_current-abc.txt",
                        "marker was " + HarvestOptions.CurrentMarkerFileName);
                }
                finally
                {
                    Environment.SetEnvironmentVariable(HarvestOptions.ChunkIdVariable, null);
                }
            });
        }

        // ----------------------------------------------------------------- schema
        private static void SchemaChecks()
        {
            Section("CSV schema");

            var formatter = new CsvFormatter();

            Check("header is unique and non-empty", () =>
            {
                string[] header = CsvFormatter.ColumnNames();

                if (header.Length == 0) { return "header is empty"; }
                if (header.Any(string.IsNullOrWhiteSpace)) { return "a column name is blank"; }
                if (header.Distinct(StringComparer.Ordinal).Count() != header.Length)
                {
                    return "duplicate column name";
                }
                return null;
            });

            Check("a row has exactly one field per column", () =>
            {
                var row = new HarvestRow(
                    Drawing: "Drawing, with comma.dwg", Space: "Model", EntityType: "Block",
                    Name: "Bath Tub - 26 x 60 in.", BlockDefinition: "Bath Tub - 26 x 60 in.",
                    Layer: "0", Handle: "47", X: 20.593, Y: 287.084, Z: 0.0, Length: null,
                    Area: null, Unit: "Inches", UnitCode: 1);

                string[] fields = SplitCsv(formatter.Row(row));
                int expected = CsvFormatter.ColumnNames().Length;

                if (fields.Length != expected)
                {
                    return $"row has {fields.Length} fields, header has {expected}";
                }

                return Expect(fields[0] == "Drawing, with comma.dwg", "drawing field")
                    ?? Expect(fields[1] == "Model", "space field")
                    ?? Expect(fields[2] == "Block", "entity type field")
                    ?? Expect(fields[3] == "Bath Tub - 26 x 60 in.", "name field")
                    ?? Expect(fields[4] == "Bath Tub - 26 x 60 in.", "definition field")
                    ?? Expect(fields[5] == "0", "layer field")
                    ?? Expect(fields[6] == "47", "handle field")
                    ?? Expect(fields[7] == string.Empty, "top-level container must be blank")
                    ?? Expect(fields[8] == "0", "top-level depth")
                    ?? Expect(fields[9] == "0", "top-level xref flag")
                    ?? Expect(fields[10] == "20.593", "X field")
                    ?? Expect(fields[11] == "287.084", "Y field")
                    ?? Expect(fields[12] == "0.000", "Z field")
                    ?? Expect(fields[13] == string.Empty, "polyline-only Length must be blank")
                    ?? Expect(fields[14] == string.Empty, "an area with no meaning must be blank")
                    ?? Expect(fields[15] == "Inches", "unit field")
                    ?? Expect(fields[16] == "1", "unit code field");
            });

            Check("area is formatted like every other measurement", () =>
            {
                var row = new HarvestRow(
                    "D.dwg", "Model", "Hatch", "", "", "0", "1", 0, 0, 0, null, 12.5, "Inches", 1);

                string[] fields = SplitCsv(formatter.Row(row));

                return Expect(fields[13] == string.Empty, "a hatch has no length")
                    ?? Expect(fields[14] == "12.500", "area");
            });

            Check("nested provenance is formatted, not inferred by the reader", () =>
            {
                var row = new HarvestRow(
                    "D.dwg", "Model", "Line", "", "", "0", "A1", 0, 0, 0, 1, null, "Inches", 1,
                    Container: "Door@2A > Hinge@3B", Depth: 2, IsXref: true);

                string[] fields = SplitCsv(formatter.Row(row));

                return Expect(fields[7] == "Door@2A > Hinge@3B", "container")
                    ?? Expect(fields[8] == "2", "depth")
                    ?? Expect(fields[9] == "1", "xref flag");
            });

            Check("a polyline row leaves the block fields blank", () =>
            {
                var row = new HarvestRow(
                    Drawing: "D.dwg", Space: "Model", EntityType: "Polyline",
                    Name: string.Empty, BlockDefinition: string.Empty, Layer: "102",
                    Handle: "A50", X: null, Y: null, Z: null, Length: 306.124, Area: null,
                    Unit: "Undefined", UnitCode: 0);

                string[] fields = SplitCsv(formatter.Row(row));

                return Expect(fields.Length == CsvFormatter.ColumnNames().Length, "field count")
                    ?? Expect(fields[3] == string.Empty, "name must be blank")
                    ?? Expect(fields[4] == string.Empty, "definition must be blank")
                    ?? Expect(fields[10] == string.Empty, "X must be blank")
                    ?? Expect(fields[13] == "306.124", "length")
                    ?? Expect(fields[14] == string.Empty, "an open polyline has no area")
                    ?? Expect(fields[16] == "0", "unit code");
            });

            Check("header and row use the same column list", () =>
            {
                var row = new HarvestRow(
                    "D.dwg", "Model", "Line", "", "", "0", "1", 0, 0, 0, 1, null, "Inches", 1);

                string[] header = SplitCsv(formatter.Header);
                string[] values = SplitCsv(formatter.Row(row));

                return Expect(header.SequenceEqual(CsvFormatter.ColumnNames()),
                        "Header disagrees with ColumnNames()")
                    ?? Expect(header.Length == values.Length, "header/row length mismatch");
            });

            Check("the Excel guard is off by default", () =>
            {
                var row = new HarvestRow(
                    "D.dwg", "Model", "Line", "", "", "-Layer", "1", -5.5, 0, 0, 1, null, "Inches", 1);

                string[] fields = SplitCsv(formatter.Row(row));

                return Expect(fields[5] == "-Layer", "layer changed with the guard off")
                    ?? Expect(fields[10] == "-5.500", "negative X changed with the guard off");
            });

            Check("the Excel guard fires on text but never on a number", () =>
            {
                var guarded = new CsvFormatter(excelSafe: true);
                var row = new HarvestRow(
                    "D.dwg", "Model", "Line", "", "", "-Layer", "1", -5.5, 0, 0, 1, null, "Inches", 1);

                string[] fields = SplitCsv(guarded.Row(row));

                // An apostrophe on a numeric field would stop it being a number, which is
                // why the guard is applied per column rather than to the whole row.
                return Expect(fields[5] == "'-Layer", "text layer not guarded")
                    ?? Expect(fields[10] == "-5.500", "a negative coordinate must stay numeric")
                    ?? Expect(fields[13] == "1.000", "a positive length must stay numeric")
                    ?? Expect(fields[16] == "1", "the unit code must stay numeric");
            });

            Check("the attribute guard is opt-in too", () =>
            {
                var row = new HarvestAttributeRow("D.dwg", "1", "-TAG", "=1+1", IsConstant: false);

                string[] plain = SplitCsv(new AttributeCsvFormatter().Row(row));
                string[] guarded = SplitCsv(new AttributeCsvFormatter(excelSafe: true).Row(row));

                return Expect(plain[3] == "-TAG" && plain[4] == "=1+1",
                        "the default attribute formatter changed a value")
                    ?? Expect(guarded[3] == "'-TAG", "tag not guarded")
                    ?? Expect(guarded[4] == "'=1+1", "value not guarded")
                    ?? Expect(guarded[5] == "0", "the constant flag must not be guarded");
            });

            Check("attribute schema is the documented long format", () =>
            {
                string[] expected =
                    { "Drawing", "EntityHandle", "Container", "Tag", "Value", "IsConstant" };
                string[] actual = AttributeCsvFormatter.ColumnNames();

                return Expect(actual.SequenceEqual(expected),
                    "columns are " + string.Join(",", actual));
            });

            Check("an attribute value is escaped and the flag is 1 or 0", () =>
            {
                var formatter2 = new AttributeCsvFormatter();

                var row = new HarvestAttributeRow(
                    Drawing: "Floor, Plan.dwg",
                    EntityHandle: "2A7",
                    Tag: "#",
                    Value: "RIVET #511 X 3/16, \"long\"",
                    IsConstant: false);

                string[] fields = SplitCsv(formatter2.Row(row));

                return Expect(fields.Length == 6, "field count")
                    ?? Expect(fields[0] == "Floor, Plan.dwg", "drawing")
                    ?? Expect(fields[1] == "2A7", "entity handle")
                    ?? Expect(fields[2] == string.Empty, "top-level container")
                    ?? Expect(fields[3] == "#", "tag may be '#'")
                    ?? Expect(fields[4] == "RIVET #511 X 3/16, \"long\"", "value")
                    ?? Expect(fields[5] == "0", "false must be 0");
            });

            Check("a constant attribute is 1", () =>
            {
                var formatter2 = new AttributeCsvFormatter();
                var row = new HarvestAttributeRow("D.dwg", "1", "TAG", "", IsConstant: true);
                string[] fields = SplitCsv(formatter2.Row(row));

                return Expect(fields[5] == "1", "true must be 1")
                    ?? Expect(fields[4] == string.Empty, "an empty value stays empty");
            });
        }

        // ---------------------------------------------------------------- helpers

        /// <summary>
        /// Proves the runner can fail. Without this, "16 passed" could mean the checks never
        /// ran - the same class of bug this phase is fixing in the pipeline.
        /// </summary>
        private static int SelfTest()
        {
            Console.WriteLine("Harvest.Checks - self-test (a failure must be reported)");

            Check("deliberately failing check", () => "this check is supposed to fail");

            bool detected = _failed == 1 && _passed == 0;

            Console.WriteLine();
            Console.WriteLine(detected
                ? "SELF-TEST OK: the runner reports failures and would exit 1"
                : $"SELF-TEST FAILED: failed={_failed} passed={_passed}");

            return detected ? 0 : 1;
        }

        private static void Section(string title)
        {
            Console.WriteLine("[" + title + "]");
        }

        private static void Check(string name, Func<string?> body)
        {
            string? problem;
            try
            {
                problem = body();
            }
            catch (System.Exception ex)
            {
                problem = "threw " + ex.GetType().Name + ": " + ex.Message;
            }

            if (problem is null)
            {
                _passed++;
                Console.WriteLine("  PASS  " + name);
            }
            else
            {
                _failed++;
                Console.WriteLine("  FAIL  " + name);
                Console.WriteLine("        " + problem);
            }
        }

        /// <summary>Returns null when the condition holds, otherwise the failure text.</summary>
        private static string? Expect(bool condition, string description)
        {
            return condition ? null : description;
        }

        private static TException Throws<TException>(Action action) where TException : System.Exception
        {
            try
            {
                action();
            }
            catch (TException ex)
            {
                return ex;
            }

            throw new InvalidOperationException(
                "expected " + typeof(TException).Name + ", nothing was thrown");
        }

        private static JsonDocument Parse(string json)
        {
            try
            {
                return JsonDocument.Parse(json);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("status file is not valid JSON: " + json, ex);
            }
        }

        /// <summary>
        /// Minimal RFC 4180 splitter: enough to prove the row carries the right number of
        /// fields, and that a quoted field survives in one piece.
        /// </summary>
        private static string[] SplitCsv(string line)
        {
            var fields = new List<string>();
            var current = new System.Text.StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            current.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        current.Append(c);
                    }
                    continue;
                }

                if (c == '"')
                {
                    inQuotes = true;
                }
                else if (c == ',')
                {
                    fields.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }

            fields.Add(current.ToString());
            return fields.ToArray();
        }
    }
}
