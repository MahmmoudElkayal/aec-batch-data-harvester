using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
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
    /// Nested blocks are opt-in (<c>AEC_HARVEST_NESTED=1</c>). When enabled, the scanner
    /// descends into a block reference's definition and reports the geometry it contains,
    /// with the insertion transform applied and the ancestor chain recorded. The default
    /// is off because it multiplies rows on any drawing with block geometry, and because
    /// the same inner definition object is reached once per placement.
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

        /// <summary>
        /// How deep a nested block chain may go before the walk stops. A block can legally
        /// contain itself, directly or through a cycle, so depth alone cannot make the
        /// walk terminate; it is the second guard behind the per-path visited set. Eight
        /// levels is far past any real drawing and cheap even for a pathological one.
        /// </summary>
        private const int MaxBlockDepth = 8;

        private readonly ExtractorRegistry _registry;
        private readonly IModelSpaceResolver _resolver;
        private readonly bool _includeNestedBlocks;

        public DrawingScanner(
            ExtractorRegistry registry,
            IModelSpaceResolver resolver,
            bool includeNestedBlocks = false)
        {
            _registry = registry;
            _resolver = resolver;
            _includeNestedBlocks = includeNestedBlocks;
        }

        public HarvestResult Scan(Database db, string drawingName, IRowSink sink)
        {
            string unit = db.Insunits.ToString();
            int unitCode = (int)db.Insunits;

            ObjectId modelSpaceId = _resolver.Resolve(db);
            ModelSpaceGuard.EnsureResolved(modelSpaceId.IsNull, drawingName);

            // A side database opened with ReadDwgFile does not load xref contents: the
            // xref's BlockTableRecord exists (and says IsFromExternalReference) but is
            // empty until the xrefs are resolved. Resolving only when nested traversal is
            // requested keeps the default run's cost and behaviour identical, and a
            // missing xref file must not fail the drawing - the block row is still written
            // and still says it came from outside.
            if (_includeNestedBlocks)
            {
                try
                {
                    db.ResolveXrefs(useThreadEngine: false, doNewOnly: false);
                }
                catch (AcException)
                {
                    // Unresolved xref: reported as a block row, with no children.
                }
            }

            var unsupported = new UnsupportedEntities();

            using Transaction tr = db.TransactionManager.StartTransaction();

            if (tr.GetObject(modelSpaceId, OpenMode.ForRead) is not BlockTableRecord modelSpace)
            {
                throw new InvalidOperationException(
                    "model space object is not a BlockTableRecord for '" + drawingName + "'");
            }

            SpaceTotals totals = ScanSpace(
                modelSpace, ModelSpaceLabel, tr, drawingName, unit, unitCode, sink, unsupported);

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
                        space, PaperSpaceLabel, tr, drawingName, unit, unitCode, sink, unsupported);
                }
            }

            return new HarvestResult(
                totals.Rows, totals.Attributes, totals.Skipped, unit, unitCode,
                unsupported.ToSortedDictionary());
        }

        private SpaceTotals ScanSpace(
            BlockTableRecord space,
            string spaceLabel,
            Transaction tr,
            string drawingName,
            string unit,
            int unitCode,
            IRowSink sink,
            UnsupportedEntities unsupported)
        {
            var context = new HarvestContext(tr, drawingName, spaceLabel, unit, unitCode);

            // The layout itself is seeded into the path so a malformed block reference
            // that points back at model space cannot re-enter it.
            var path = new HashSet<ObjectId> { space.ObjectId };

            return ScanEntities(space, context, sink, unsupported, path);
        }

        private SpaceTotals ScanEntities(
            BlockTableRecord container,
            HarvestContext context,
            IRowSink sink,
            UnsupportedEntities unsupported,
            HashSet<ObjectId> path)
        {
            var rowBuffer = new List<HarvestRow>(1);
            var attributeBuffer = new List<HarvestAttributeRow>(0);

            int written = 0;
            int attributes = 0;
            int skipped = 0;

            foreach (ObjectId id in container)
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
                // opened at all - and every class it does not know is counted, because
                // otherwise "nothing here" and "nothing we can read" look identical.
                RXClass entityClass;
                try
                {
                    entityClass = id.ObjectClass;
                }
                catch (AcException)
                {
                    unsupported.Add(UnsupportedEntities.UnresolvedClass);
                    skipped++;
                    continue;
                }

                IEntityExtractor? extractor;
                try
                {
                    extractor = _registry.Resolve(entityClass);
                }
                catch (AcException)
                {
                    // An object whose class cannot even be resolved is not readable.
                    unsupported.Add(ClassKey(entityClass));
                    skipped++;
                    continue;
                }

                if (extractor is null)
                {
                    unsupported.Add(ClassKey(entityClass));
                    continue;
                }

                Entity? entity;
                try
                {
                    entity = context.Transaction.GetObject(id, OpenMode.ForRead) as Entity;
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

                // An AttributeDefinition is the definition of an attribute, not visible
                // text. Inside a block definition it would otherwise match the DBText
                // extractor and publish a row per tag. The reference's AttributeReference
                // rows are the visible values, and they are handled by BlockExtractor.
                if (_includeNestedBlocks && context.Depth > 0 && entity is AttributeDefinition)
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

                Stamp(rowBuffer, attributeBuffer, context);

                sink.Write(rowBuffer);

                if (attributeBuffer.Count > 0)
                {
                    sink.WriteAttributes(attributeBuffer);
                }

                written += rowBuffer.Count;
                attributes += attributeBuffer.Count;

                if (_includeNestedBlocks && entity is BlockReference block)
                {
                    SpaceTotals nested = ScanBlockDefinition(
                        block, context, sink, unsupported, path);

                    written += nested.Rows;
                    attributes += nested.Attributes;
                    skipped += nested.Skipped;
                }
            }

            return new SpaceTotals(written, attributes, skipped);
        }

        /// <summary>
        /// Walk one block reference's definition, depth-first.
        ///
        /// The visited set is the path, not a global set: the same definition is legitimately
        /// reached once per placement, and a global set would report only the first. The
        /// depth cap is the second line of defence for a definition that reaches itself.
        /// </summary>
        private SpaceTotals ScanBlockDefinition(
            BlockReference block,
            HarvestContext parent,
            IRowSink sink,
            UnsupportedEntities unsupported,
            HashSet<ObjectId> path)
        {
            if (parent.Depth >= MaxBlockDepth)
            {
                return default;
            }

            ObjectId definitionId = BlockInfo.DefinitionId(block);
            if (definitionId.IsNull || path.Contains(definitionId))
            {
                return default;
            }

            BlockTableRecord? definition;
            try
            {
                definition = parent.Transaction.GetObject(definitionId, OpenMode.ForRead)
                    as BlockTableRecord;
            }
            catch (AcException)
            {
                // Dangling or unreadable definition: the block row is already written,
                // and there is nothing below it to report.
                return default;
            }

            if (definition is null)
            {
                return default;
            }

            Autodesk.AutoCAD.Geometry.Matrix3d blockTransform;
            try
            {
                blockTransform = block.BlockTransform;
            }
            catch (AcException)
            {
                return default;
            }

            HarvestContext child = parent.EnterBlock(
                blockTransform,
                new BlockContainer(
                    BlockInfo.Name(definition),
                    block.Handle.ToString(),
                    BlockInfo.IsExternalReference(definition)));

            path.Add(definitionId);
            try
            {
                return ScanEntities(definition, child, sink, unsupported, path);
            }
            finally
            {
                path.Remove(definitionId);
            }
        }

        /// <summary>
        /// Copy the context's provenance onto every row an extractor produced. Doing it
        /// here rather than in each extractor keeps that knowledge in one place and means
        /// no extractor can forget it.
        /// </summary>
        private static void Stamp(
            List<HarvestRow> rows,
            List<HarvestAttributeRow> attributes,
            HarvestContext context)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                HarvestRow row = rows[i];
                rows[i] = row with
                {
                    Container = context.Container,
                    Depth = context.Depth,
                    IsXref = row.IsXref || context.IsXref
                };
            }

            for (int i = 0; i < attributes.Count; i++)
            {
                attributes[i] = attributes[i] with { Container = context.Container };
            }
        }

        /// <summary>
        /// The name a person would use for the entity: the DXF name ("HATCH", "DIMENSION",
        /// "ACAD_PROXY_ENTITY"), falling back to the .NET class name for classes that do
        /// not publish one. DXF names are stable across releases and match the entity list
        /// in the drawing, which is what makes the census actionable.
        /// </summary>
        private static string ClassKey(RXClass entityClass)
        {
            string? dxf = entityClass.DxfName;
            return string.IsNullOrWhiteSpace(dxf) ? entityClass.Name : dxf!;
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
