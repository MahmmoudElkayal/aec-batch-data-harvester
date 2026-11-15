using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using AEC_Batch_Data_Haravester.Core;
using AEC_Batch_Data_Haravester.Output;
using AcException = Autodesk.AutoCAD.Runtime.Exception;

namespace AEC_Batch_Data_Haravester.Extraction
{
    /// <summary>
    /// Walks every layout space and writes one row per supported entity. Read-only
    /// transaction: nothing is opened ForWrite, nothing is committed back.
    ///
    /// Model space first, then each paper layout in block-table order. Paper space is
    /// where title blocks and view labels live on production drawings; the sample corpus
    /// has only viewports there, which is why enabling it changes no row count on the
    /// samples but is still tested with a synthetic drawing.
    ///
    /// Failure policy, applied per entity: a proxy or corrupt object is skipped and
    /// counted, because that is the one place where "keep harvesting" is the right
    /// recovery. Anything else propagates to the command boundary and fails the drawing.
    ///
    /// Each entity's entity rows and attribute rows are buffered before they reach the
    /// sink, so an extractor that throws half way through cannot publish a partial result.
    /// </summary>
    public sealed class DrawingScanner
    {
        private const string ModelSpaceLabel = "Model";
        private const string PaperSpaceLabel = "Paper";

        private readonly ExtractorRegistry _registry;
        private readonly IModelSpaceResolver _resolver;

        public DrawingScanner(ExtractorRegistry registry, IModelSpaceResolver resolver)
        {
            _registry = registry;
            _resolver = resolver;
        }

        public HarvestResult Scan(Database db, string drawingName, IRowSink sink)
        {
            string unit = db.Insunits.ToString();
            int unitCode = (int)db.Insunits;

            ObjectId modelSpaceId = _resolver.Resolve(db);
            ModelSpaceGuard.EnsureResolved(modelSpaceId.IsNull, drawingName);

            using Transaction tr = db.TransactionManager.StartTransaction();

            if (tr.GetObject(modelSpaceId, OpenMode.ForRead) is not BlockTableRecord modelSpace)
            {
                throw new InvalidOperationException(
                    "model space object is not a BlockTableRecord for '" + drawingName + "'");
            }

            SpaceTotals totals = ScanSpace(
                modelSpace, ModelSpaceLabel, tr, drawingName, unit, unitCode, sink);

            // Every paper layout is a BlockTableRecord flagged IsLayout. Enumerating the
            // block table needs no document or LayoutManager, which matters because the
            // batch engine opens side databases that have neither.
            if (tr.GetObject(db.BlockTableId, OpenMode.ForRead) is BlockTable blockTable)
            {
                foreach (ObjectId id in blockTable)
                {
                    if (id == modelSpaceId)
                    {
                        continue;
                    }

                    if (tr.GetObject(id, OpenMode.ForRead) is not BlockTableRecord space)
                    {
                        continue;
                    }

                    if (!space.IsLayout)
                    {
                        continue;
                    }

                    totals += ScanSpace(
                        space, PaperSpaceLabel, tr, drawingName, unit, unitCode, sink);
                }
            }

            return new HarvestResult(
                totals.Rows, totals.Attributes, totals.Skipped, unit, unitCode);
        }

        private SpaceTotals ScanSpace(
            BlockTableRecord space,
            string spaceLabel,
            Transaction tr,
            string drawingName,
            string unit,
            int unitCode,
            IRowSink sink)
        {
            var context = new HarvestContext(tr, drawingName, spaceLabel, unit, unitCode);
            var rowBuffer = new List<HarvestRow>(1);
            var attributeBuffer = new List<HarvestAttributeRow>(0);

            int written = 0;
            int attributes = 0;
            int skipped = 0;

            foreach (ObjectId id in space)
            {
                if (id.IsErased)
                {
                    continue;
                }

                // Resolve the class before opening the object. RXClass is metadata: it
                // costs nothing to ask, while GetObject opens the object, reads its
                // common data and - for a proxy - may fail. On a drawing full of hatches
                // or dimensions, every unsupported entity used to pay that read for no
                // result. The registry is consulted first so only supported classes are
                // opened at all.
                IEntityExtractor? extractor;
                try
                {
                    extractor = _registry.Resolve(id.ObjectClass);
                }
                catch (AcException)
                {
                    // An object whose class cannot even be resolved is not readable.
                    skipped++;
                    continue;
                }

                if (extractor is null)
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
                    skipped++;
                    continue;
                }

                if (entity is null)
                {
                    continue;
                }

                rowBuffer.Clear();
                attributeBuffer.Clear();

                try
                {
                    EntityExtraction extraction = extractor.Extract(entity, context);
                    rowBuffer.AddRange(extraction.Rows);
                    attributeBuffer.AddRange(extraction.Attributes);
                }
                catch (AcException)
                {
                    // An entity the extractor cannot read is one lost row set, not a lost
                    // drawing. Counted, never silent.
                    skipped++;
                    continue;
                }

                sink.Write(rowBuffer);

                if (attributeBuffer.Count > 0)
                {
                    sink.WriteAttributes(attributeBuffer);
                }

                written += rowBuffer.Count;
                attributes += attributeBuffer.Count;
            }

            return new SpaceTotals(written, attributes, skipped);
        }

        private readonly struct SpaceTotals
        {
            public SpaceTotals(int rows, int attributes, int skipped)
            {
                Rows = rows;
                Attributes = attributes;
                Skipped = skipped;
            }

            public int Rows { get; }

            public int Attributes { get; }

            public int Skipped { get; }

            public static SpaceTotals operator +(SpaceTotals a, SpaceTotals b)
            {
                return new SpaceTotals(
                    a.Rows + b.Rows, a.Attributes + b.Attributes, a.Skipped + b.Skipped);
            }
        }
    }
}
