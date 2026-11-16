using System;
using System.Collections.Generic;

namespace AEC_Batch_Data_Haravester.Core
{
    /// <summary>
    /// What the harvester walked past because no extractor covers it, counted by class.
    ///
    /// The registry is deliberately small, and an entity type without an extractor is
    /// skipped before the object is even opened - that is what keeps a hatch-heavy drawing
    /// cheap. The cost of the design is that "this drawing has no data" and "this drawing's
    /// data is all unsupported" produce the same CSV. This census is the only thing that
    /// tells them apart, so it is reported on every run rather than behind a switch.
    ///
    /// Kept free of AutoCAD types: the scanner supplies a display name, and the counters
    /// are testable without the runtime.
    /// </summary>
    public sealed class UnsupportedEntities
    {
        /// <summary>Used when even the object's class cannot be resolved.</summary>
        public const string UnresolvedClass = "(unresolved)";

        private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

        /// <summary>Total unsupported objects, including the unresolved ones.</summary>
        public int Total { get; private set; }

        public bool IsEmpty => _counts.Count == 0;

        /// <summary>
        /// Count one object. A blank name is recorded as <see cref="UnresolvedClass"/>
        /// rather than dropped: an object we cannot name is still an object we did not read.
        /// </summary>
        public void Add(string? className)
        {
            string key = string.IsNullOrWhiteSpace(className) ? UnresolvedClass : className!;
            _counts.TryGetValue(key, out int count);
            _counts[key] = count + 1;
            Total++;
        }

        /// <summary>Fold one layout's counts into the drawing's.</summary>
        public void Merge(UnsupportedEntities other)
        {
            if (other is null)
            {
                return;
            }

            foreach (KeyValuePair<string, int> pair in other._counts)
            {
                _counts.TryGetValue(pair.Key, out int count);
                _counts[pair.Key] = count + pair.Value;
            }

            Total += other.Total;
        }

        /// <summary>
        /// Ordinal order, so the same drawing always produces the same status line. The
        /// status file is compared byte for byte by the integrity checks.
        /// </summary>
        public IReadOnlyDictionary<string, int> ToSortedDictionary()
        {
            var sorted = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, int> pair in _counts)
            {
                sorted[pair.Key] = pair.Value;
            }

            return sorted;
        }
    }
}
