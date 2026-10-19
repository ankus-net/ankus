namespace Ankus.Build;

/// <summary>
/// Projects C anonymous containers into their actual parent's addressable members without inventing native object identities.
/// </summary>
/// <param name="graph">The complete measured native declaration graph.</param>
internal sealed class NativeBindingRecordMembers(NativeRecordGraph graph)
{
    private readonly HashSet<int> _containers = FindInternalContainers(graph);
    private readonly Dictionary<int, IReadOnlyList<NativeBindingRecordMember>> _members = [];

    /// <summary>
    /// Reports whether a declaration has only anonymous parent storage and no independently exposed native type.
    /// </summary>
    internal bool IsInternalDeclaration(int index) => _containers.Contains(index);

    /// <summary>
    /// Reports whether a type spelling denotes an internal anonymous container. Other types are exposed native values.
    /// </summary>
    internal bool IsInternalType(int index)
        => graph.Types[graph.Types[index].Canonical].Declaration is int declaration && IsInternalDeclaration(declaration);

    /// <summary>
    /// Gets addressable members with cumulative native offsets and qualification inherited through anonymous parents.
    /// </summary>
    internal IReadOnlyList<NativeBindingRecordMember> Get(int index)
    {
        if (_members.TryGetValue(index, out IReadOnlyList<NativeBindingRecordMember>? previous))
        {
            return previous;
        }

        var members = new List<NativeBindingRecordMember>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var pending = new HashSet<int>();
        Include(index, 0, NativeHeaderQualifiers.None);
        IReadOnlyList<NativeBindingRecordMember> result = members.AsReadOnly();
        _members.Add(index, result);
        return result;

        void Include(int declaration, long offset, NativeHeaderQualifiers qualifiers)
        {
            if (pending.Count >= 128 || !pending.Add(declaration))
            {
                throw new FormatException("Native anonymous member promotion contains a cycle or exceeds its nesting limit.");
            }

            foreach (NativeRecordField field in graph.Declarations[declaration].Fields)
            {
                long absolute = checked(offset + field.OffsetBits);
                if (field.IsAnonymous)
                {
                    NativeRecordType type = graph.Types[field.Type];
                    NativeRecordType canonical = graph.Types[type.Canonical];
                    if (canonical.Kind != "record" || canonical.Declaration is not int nested)
                    {
                        throw new FormatException("An anonymous native member must contain a record declaration.");
                    }

                    Include(nested, absolute, qualifiers | type.Qualifiers);
                }
                else
                {
                    if (field.Name.Length != 0 && !names.Add(field.Name))
                    {
                        throw new FormatException("Promoted native member names are ambiguous in their enclosing record.");
                    }

                    members.Add(new(field with { OffsetBits = absolute }, qualifiers));
                }
            }

            pending.Remove(declaration);
        }
    }

    /// <summary>
    /// Keeps every named, aliased, rooted or otherwise addressable native type outside the internal-container set.
    /// </summary>
    private static HashSet<int> FindInternalContainers(NativeRecordGraph graph)
    {
        var containers = new HashSet<int>();
        foreach (NativeRecordField field in graph.Declarations.SelectMany(static declaration => declaration.Fields))
        {
            if (!(field.IsAnonymous && graph.Types[graph.Types[field.Type].Canonical].Declaration is int declaration &&
                graph.Declarations[declaration].Name.Length == 0))
            {
                continue;
            }

            containers.Add(declaration);
        }

        foreach (int root in graph.Roots.Values)
        {
            Expose(root);
        }

        foreach (NativeRecordType type in graph.Types)
        {
            if (type.Kind == "alias")
            {
                Expose(type.Canonical);
            }
            else if (type.Kind is "pointer" or "array" or "vector" or "atomic" or "complex")
            {
                Expose(type.Element!.Value);
            }

            if (type.Function is NativeRecordFunction function)
            {
                Expose(function.Result);
                foreach (int parameter in function.Parameters)
                {
                    Expose(parameter);
                }
            }
        }

        foreach (NativeRecordField field in graph.Declarations.SelectMany(static declaration => declaration.Fields).Where(static field => !field.IsAnonymous))
        {
            Expose(field.Type);
        }

        return containers;

        void Expose(int type)
        {
            if (graph.Types[graph.Types[type].Canonical].Declaration is int declaration)
            {
                containers.Remove(declaration);
            }
        }
    }
}

/// <summary>
/// Retains an addressable native member and the qualifiers introduced by its anonymous containing records.
/// </summary>
/// <param name="Field">The original field with its offset measured from the exposed native parent.</param>
/// <param name="ParentQualifiers">The qualification inherited through anonymous container fields.</param>
internal sealed record NativeBindingRecordMember(NativeRecordField Field, NativeHeaderQualifiers ParentQualifiers);
