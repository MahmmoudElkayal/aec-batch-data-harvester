using System;
using System.Collections.Generic;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcException = Autodesk.AutoCAD.Runtime.Exception;

namespace AEC_Batch_Data_Haravester.Extraction
{
    /// <summary>
    /// Everything an extractor needs about the drawing it is reading. The transaction is
    /// here because resolving a block reference's definition is a database read; nothing
    /// in this type is written.
    ///
    /// It also owns the per-space cache of constant block attributes, because that lookup
    /// belongs to the transaction, not to the block extractor: a layout can place the same
    /// door block forty times, and its constant attribute inventory should be read once,
    /// not forty times.
    ///
    /// The transform and container chain are what make nested traversal honest. A child
    /// context is created for each block insertion, so a point read from a block definition
    /// is reported in drawing coordinates and carries the path that led to it. The
    /// top-level context has the identity transform and an empty chain, which is why every
    /// pre-nesting row is unchanged.
    /// </summary>
    public sealed class HarvestContext
    {
        private readonly Dictionary<ObjectId, ConstantAttribute[]> _constantAttributes;
        private readonly BlockContainer[] _containers;

        public HarvestContext(
            Transaction transaction, string drawing, string space, string unit, int unitCode)
            : this(
                transaction, drawing, space, unit, unitCode,
                Matrix3d.Identity, Array.Empty<BlockContainer>(), isXref: false,
                new Dictionary<ObjectId, ConstantAttribute[]>())
        {
        }

        private HarvestContext(
            Transaction transaction,
            string drawing,
            string space,
            string unit,
            int unitCode,
            Matrix3d transform,
            BlockContainer[] containers,
            bool isXref,
            Dictionary<ObjectId, ConstantAttribute[]> constantAttributes)
        {
            Transaction = transaction;
            Drawing = drawing;
            Space = space;
            Unit = unit;
            UnitCode = unitCode;
            Transform = transform;
            _containers = containers;
            IsXref = isXref;
            _constantAttributes = constantAttributes;
            Container = BuildContainer(containers);
        }

        public Transaction Transaction { get; }

        public string Drawing { get; }

        /// <summary>"Model" today; "Paper" arrives with paper-space support.</summary>
        public string Space { get; }

        public string Unit { get; }

        public int UnitCode { get; }

        /// <summary>
        /// Maps the coordinate system of the block currently being read to the drawing's
        /// world coordinate system. Identity at the top level. Points read from a nested
        /// block must be passed through <see cref="TransformPoint"/> before they are
        /// written, or they would be reported in the block's private microspace.
        /// </summary>
        public Matrix3d Transform { get; }

        /// <summary>The block references that contain the current entity, outermost first.</summary>
        public string Container { get; }

        /// <summary>How many block references contain the current entity. 0 = top level.</summary>
        public int Depth => _containers.Length;

        /// <summary>True when the entity is inside, or is itself, an external reference.</summary>
        public bool IsXref { get; }

        /// <summary>
        /// The context for an entity inside a block definition.
        ///
        /// <paramref name="blockTransform"/> maps the definition's microspace (MCS) to the
        /// coordinate system of its owner. Because <c>Point3d.TransformBy</c> applies a
        /// column-vector product (M x P), composing child-then-parent is
        /// <c>parent * child</c>; the reversed order would rotate about the wrong origin.
        /// The constant-attribute cache is shared, not copied: the same definition is
        /// already cached by the layout's read.
        /// </summary>
        public HarvestContext EnterBlock(Matrix3d blockTransform, BlockContainer container)
        {
            var containers = new BlockContainer[_containers.Length + 1];
            Array.Copy(_containers, containers, _containers.Length);
            containers[_containers.Length] = container;

            return new HarvestContext(
                Transaction,
                Drawing,
                Space,
                Unit,
                UnitCode,
                Transform * blockTransform,
                containers,
                IsXref || container.IsXref,
                _constantAttributes);
        }

        /// <summary>Map a point from the current block's space into drawing coordinates.</summary>
        public Point3d TransformPoint(Point3d point) => point.TransformBy(Transform);

        /// <summary>
        /// The scalar scale of the current transform. Uniformly scaled blocks are exact;
        /// a non-uniformly scaled block has no single length scale, and
        /// <see cref="Matrix3d.GetScale"/> returns the largest axis scale, which is the
        /// conservative choice (never under-reports the extent).
        /// </summary>
        public double Scale => Transform.GetScale();

        /// <summary>Apply <see cref="Scale"/> to a length measured in block space.</summary>
        public double ScaleLength(double length) => length * Scale;

        /// <summary>
        /// The constant attributes of a block definition, in definition order.
        ///
        /// Constant attributes are the ones AutoCAD does not copy onto the reference: the
        /// definition is used directly for every insertion (confirmed by
        /// <c>AttributeDefinition.Constant</c>: "no Attribute will be created during
        /// insertion"). Reading only <c>BlockReference.AttributeCollection</c> therefore
        /// loses every constant value in the drawing.
        ///
        /// A proxy or unreadable definition yields an empty list: a block with unreadable
        /// constants is still a block row.
        /// </summary>
        public IReadOnlyList<ConstantAttribute> ConstantAttributes(ObjectId definitionId)
        {
            if (definitionId.IsNull)
            {
                return Array.Empty<ConstantAttribute>();
            }

            if (_constantAttributes.TryGetValue(definitionId, out ConstantAttribute[]? cached))
            {
                return cached;
            }

            var found = new List<ConstantAttribute>();

            try
            {
                if (Transaction.GetObject(definitionId, OpenMode.ForRead) is BlockTableRecord definition)
                {
                    foreach (ObjectId id in definition)
                    {
                        if (id.IsErased)
                        {
                            continue;
                        }

                        try
                        {
                            if (Transaction.GetObject(id, OpenMode.ForRead) is AttributeDefinition attribute
                                && attribute.Constant)
                            {
                                found.Add(new ConstantAttribute(attribute.Tag, attribute.TextString));
                            }
                        }
                        catch (AcException)
                        {
                            // One unreadable definition object must not lose the others.
                        }
                    }
                }
            }
            catch (AcException)
            {
                // Dangling or proxy definition: no constants, but the block row survives.
            }

            ConstantAttribute[] result = found.ToArray();
            _constantAttributes[definitionId] = result;
            return result;
        }

        /// <summary>
        /// Render the ancestor chain as "Name@Handle &gt; Name@Handle".
        ///
        /// The handle is part of the path on purpose: two placements of the same outer
        /// block contain the same inner definition objects, so the inner entity handles
        /// repeat. The handle makes each placement distinguishable without adding a second
        /// path column.
        /// </summary>
        private static string BuildContainer(BlockContainer[] containers)
        {
            if (containers.Length == 0)
            {
                return string.Empty;
            }

            var sb = new StringBuilder(32);
            for (int i = 0; i < containers.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(" > ");
                }

                BlockContainer container = containers[i];
                if (container.Name.Length > 0)
                {
                    sb.Append(container.Name);
                }

                if (container.Handle.Length > 0)
                {
                    if (container.Name.Length > 0)
                    {
                        sb.Append('@');
                    }

                    sb.Append(container.Handle);
                }

                if (container.Name.Length == 0 && container.Handle.Length == 0)
                {
                    sb.Append("(block)");
                }
            }

            return sb.ToString();
        }
    }

    /// <summary>
    /// One block reference on the path to an entity. <see cref="IsXref"/> marks a
    /// reference whose definition is an external drawing, so every row below it can say
    /// that it came from outside this file.
    /// </summary>
    public readonly record struct BlockContainer(string Name, string Handle, bool IsXref);

    /// <summary>One constant attribute: the tag and the value every insertion shares.</summary>
    public readonly record struct ConstantAttribute(string Tag, string Value);
}
