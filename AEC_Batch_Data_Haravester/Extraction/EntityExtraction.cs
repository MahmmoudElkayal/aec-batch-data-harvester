using System;
using System.Collections.Generic;
using AEC_Batch_Data_Haravester.Core;

namespace AEC_Batch_Data_Haravester.Extraction
{
    /// <summary>
    /// What one entity produced. Two lists, not one, because entity rows and attribute
    /// rows are written to two different files with two different schemas.
    ///
    /// Extraction returns a whole result rather than streaming into the sink row by row:
    /// the scanner can then buffer an entity's output and drop it as a unit if the
    /// extractor throws, so a failure never publishes half an entity.
    /// </summary>
    public readonly struct EntityExtraction
    {
        private static readonly HarvestRow[] NoRows = Array.Empty<HarvestRow>();
        private static readonly HarvestAttributeRow[] NoAttributes = Array.Empty<HarvestAttributeRow>();

        public EntityExtraction(
            IReadOnlyList<HarvestRow> rows,
            IReadOnlyList<HarvestAttributeRow> attributes)
        {
            Rows = rows;
            Attributes = attributes;
        }

        public IReadOnlyList<HarvestRow> Rows { get; }

        public IReadOnlyList<HarvestAttributeRow> Attributes { get; }

        /// <summary>One entity row, no attributes - the common case.</summary>
        public static EntityExtraction FromRow(HarvestRow row)
        {
            return new EntityExtraction(new[] { row }, NoAttributes);
        }

        public static EntityExtraction Empty => new EntityExtraction(NoRows, NoAttributes);
    }
}
