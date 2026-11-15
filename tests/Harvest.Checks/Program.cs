using System.Text.Json;
using AEC_Batch_Data_Haravester;

namespace Harvest.Checks
{
    /// <summary>
    /// Acceptance checks for the defects Phase 1 fixed. Every one of them is a bug that
    /// shipped and was invisible until it bit: a broken drawing reported as success, a
    /// status line that could not be parsed, a schema that could drift from its header.
    ///
    /// These do not need AutoCAD: the guard, the status JSON and the CSV formatting are
    /// plain C#, reached through InternalsVisibleTo. Anything that does need a Database is
    /// covered by running the pipeline against the two real hosts instead.
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

            Console.WriteLine("Harvest.Checks - Phase 1 acceptance");
            Console.WriteLine();

            GuardChecks();
            StatusJsonChecks();
            CsvFormatChecks();
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
                string json = HarvestFormat.BuildStatusJson(
                    success: true,
                    drawingName: "House Designer.dwg",
                    message: "20 rows, 0 skipped",
                    elapsedMs: 25,
                    rows: 20,
                    skipped: 0,
                    unitName: "Inches",
                    unitCode: 1);

                using JsonDocument doc = Parse(json);
                JsonElement root = doc.RootElement;

                return Expect(root.GetProperty("ok").GetBoolean(), "ok should be true")
                    ?? Expect(root.GetProperty("drawing").GetString() == "House Designer.dwg", "drawing")
                    ?? Expect(root.GetProperty("rows").GetInt32() == 20, "rows")
                    ?? Expect(root.GetProperty("skipped").GetInt32() == 0, "skipped")
                    ?? Expect(root.GetProperty("ms").GetInt64() == 25, "ms")
                    ?? Expect(root.GetProperty("units").GetString() == "Inches", "units")
                    ?? Expect(root.GetProperty("unitCode").GetInt32() == 1, "unitCode");
            });

            Check("failure payload omits units and reports zero rows", () =>
            {
                string json = HarvestFormat.BuildStatusJson(
                    success: false,
                    drawingName: "Broken.dwg",
                    message: "AutoCAD error: Drawing file is not valid.",
                    elapsedMs: 3,
                    rows: 0,
                    skipped: 0,
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

                string json = HarvestFormat.BuildStatusJson(
                    success: true, drawingName: name, message: "m", elapsedMs: 1,
                    rows: 1, skipped: 0, unitName: "Millimeters", unitCode: 4);

                using JsonDocument doc = Parse(json);
                string? readBack = doc.RootElement.GetProperty("drawing").GetString();

                return Expect(readBack == name, "drawing name changed: " + readBack);
            });

            Check("the payload is a single line", () =>
            {
                string json = HarvestFormat.BuildStatusJson(
                    success: true, drawingName: "line\nbreak.dwg", message: "m\nmessage",
                    elapsedMs: 0, rows: 0, skipped: 0, unitName: null, unitCode: null);

                return Expect(json.IndexOfAny(new[] { '\r', '\n' }) < 0,
                    "raw newline in the status file");
            });
        }

        // ------------------------------------------------------------- CSV format

        private static void CsvFormatChecks()
        {
            Section("CSV formatting");

            Check("plain values are not quoted", () =>
                Expect(HarvestFormat.Escape("Layer 0") == "Layer 0", "unnecessary quoting"));

            Check("empty stays empty", () =>
                Expect(HarvestFormat.Escape(string.Empty) == string.Empty, "empty field"));

            Check("comma forces quoting", () =>
                Expect(HarvestFormat.Escape("a,b") == "\"a,b\"", "comma not quoted"));

            Check("quote is doubled", () =>
                Expect(HarvestFormat.Escape("6\" pipe") == "\"6\"\" pipe\"", "quote not doubled"));

            Check("newline forces quoting", () =>
                Expect(HarvestFormat.Escape("a\nb") == "\"a\nb\"", "newline not quoted"));

            Check("numbers are invariant with three decimals", () =>
                Expect(HarvestFormat.Number(null) == string.Empty, "null number")
                ?? Expect(HarvestFormat.Number(0) == "0.000", "zero")
                ?? Expect(HarvestFormat.Number(1.5) == "1.500", "1.5")
                ?? Expect(HarvestFormat.Number(-2.25) == "-2.250", "-2.25")
                ?? Expect(HarvestFormat.Number(1234.5678) == "1234.568", "rounding"));

            Check("status file names drop illegal characters", () =>
            {
                string cleaned = HarvestFormat.SanitizeFileName("a<b>c:d|e");
                bool invalidLeft = cleaned.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0;

                return Expect(!invalidLeft, "illegal character survived: " + cleaned)
                    ?? Expect(HarvestFormat.SanitizeFileName("   ") == "drawing", "empty name fallback");
            });
        }

        // ----------------------------------------------------------------- schema

        private static void SchemaChecks()
        {
            Section("CSV schema");

            Check("header is unique and non-empty", () =>
            {
                string[] header = HarvestFormat.CsvHeader;

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
                string row = HarvestFormat.Row(
                    drawing: "Drawing, with comma.dwg", space: "Model", entityType: "Block",
                    name: "Bath Tub - 26 x 60 in.", blockDefinition: "Bath Tub - 26 x 60 in.",
                    layer: "0", handle: "47", x: 20.593, y: 287.084, z: 0.0, length: null,
                    unit: "Inches", unitCode: 1);

                string[] fields = SplitCsv(row);
                int expected = HarvestFormat.CsvHeader.Length;

                if (fields.Length != expected)
                {
                    return $"row has {fields.Length} fields, header has {expected}: {row}";
                }

                return Expect(fields[0] == "Drawing, with comma.dwg", "drawing field")
                    ?? Expect(fields[1] == "Model", "space field")
                    ?? Expect(fields[2] == "Block", "entity type field")
                    ?? Expect(fields[3] == "Bath Tub - 26 x 60 in.", "name field")
                    ?? Expect(fields[4] == "Bath Tub - 26 x 60 in.", "definition field")
                    ?? Expect(fields[5] == "0", "layer field")
                    ?? Expect(fields[6] == "47", "handle field")
                    ?? Expect(fields[7] == "20.593", "X field")
                    ?? Expect(fields[8] == "287.084", "Y field")
                    ?? Expect(fields[9] == "0.000", "Z field")
                    ?? Expect(fields[10] == string.Empty, "polyline-only Length must be blank")
                    ?? Expect(fields[11] == "Inches", "unit field")
                    ?? Expect(fields[12] == "1", "unit code field");
            });

            Check("a polyline row leaves the block fields blank", () =>
            {
                string row = HarvestFormat.Row(
                    drawing: "D.dwg", space: "Model", entityType: "Polyline",
                    name: string.Empty, blockDefinition: string.Empty, layer: "102",
                    handle: "A50", x: null, y: null, z: null, length: 306.124,
                    unit: "Undefined", unitCode: 0);

                string[] fields = SplitCsv(row);

                return Expect(fields.Length == HarvestFormat.CsvHeader.Length, "field count")
                    ?? Expect(fields[3] == string.Empty, "name must be blank")
                    ?? Expect(fields[4] == string.Empty, "definition must be blank")
                    ?? Expect(fields[7] == string.Empty, "X must be blank")
                    ?? Expect(fields[10] == "306.124", "length")
                    ?? Expect(fields[12] == "0", "unit code");
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
