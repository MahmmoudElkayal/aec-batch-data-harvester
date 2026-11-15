using System.Collections.Generic;
using Autodesk.AutoCAD.Runtime;

namespace AEC_Batch_Data_Haravester.Extraction
{
    /// <summary>
    /// Maps an entity's runtime class to its extractor.
    ///
    /// Matching is by inheritance: a custom object derived from Polyline must still be
    /// harvested as a polyline. The result is cached per class, so the base-chain walk
    /// happens once per class seen in a session rather than once per entity - a drawing
    /// with 4,000 lines should not pay 4,000 walks.
    /// </summary>
    public sealed class ExtractorRegistry
    {
        private readonly Dictionary<RXClass, IEntityExtractor> _byExactClass = new();
        private readonly Dictionary<RXClass, IEntityExtractor?> _cache = new();

        public ExtractorRegistry(IEnumerable<IEntityExtractor> extractors)
        {
            foreach (IEntityExtractor extractor in extractors)
            {
                _byExactClass[extractor.TargetClass] = extractor;
            }
        }

        /// <summary>The extractors the harvester ships with.</summary>
        public static ExtractorRegistry CreateDefault()
        {
            // Registration order does not affect matching: the registry is keyed by class
            // and walks the inheritance chain. Adding an entity type is therefore one new
            // extractor class and one line here.
            return new ExtractorRegistry(new IEntityExtractor[]
            {
                new BlockExtractor(),
                new PolylineExtractor(),
                new LineExtractor(),
                new ArcExtractor(),
                new CircleExtractor(),
                new DbTextExtractor(),
                new MTextExtractor(),
                new SolidExtractor()
            });
        }

        public IEntityExtractor? Resolve(RXClass entityClass)
        {
            if (_cache.TryGetValue(entityClass, out IEntityExtractor? cached))
            {
                return cached;
            }

            IEntityExtractor? found = null;
            for (RXClass? current = entityClass; current is not null; current = current.MyParent)
            {
                if (_byExactClass.TryGetValue(current, out found))
                {
                    break;
                }
            }

            _cache[entityClass] = found;
            return found;
        }
    }
}
