using System.Collections.Generic;
using System.Text;

namespace Ankus.Generators;

/// <summary>
/// Caches escaped derived catalog grammar while keeping provider qualification at typed identifier boundaries.
/// </summary>
/// <param name="EqualitySignature">The exact equality operator identity.</param>
/// <param name="Functions">The ordered scalar helper catalog declarations.</param>
/// <param name="Operators">The ordered equality and ordering declarations.</param>
/// <param name="Ordering">The optional btree family and class.</param>
/// <param name="Hashing">The optional hash family and class.</param>
internal sealed record DerivedSqlEmission(DerivedSqlEmission.Fragment EqualitySignature,
    EquatableArray<DerivedSqlEmission.Function> Functions, EquatableArray<DerivedSqlEmission.Comparison> Operators,
    DerivedSqlEmission.Family? Ordering, DerivedSqlEmission.Family? Hashing)
{
    /// <summary>
    /// Renders fixed names and grammar without compiler state, mutable graph objects or managed conversions.
    /// </summary>
    /// <param name="model">The minimal derived SQL contract.</param>
    /// <returns>The escaped independently cached declarations and attachment identities.</returns>
    internal static DerivedSqlEmission Create(DerivedSqlModel model)
    {
        var functions = new List<Function>();
        var operators = new List<Comparison>();
        if (model.Equality)
        {
            AddFunction("eq", true, false);
            AddFunction("ne", true, false);
            AddComparison("=", "eq", "=", "<>", "eqsel", "eqjoinsel", true);
            AddComparison("<>", "ne", "<>", "=", "neqsel", "neqjoinsel", false);
        }

        Family? ordering = null;
        if (model.Ordering)
        {
            AddFunction("cmp", false, false);
            foreach ((string token, string role, string commutator, string negator, string restrict, string join) in new[]
            {
                ("<", "lt", ">", ">=", "scalarltsel", "scalarltjoinsel"),
                (">", "gt", "<", "<=", "scalargtsel", "scalargtjoinsel"),
                ("<=", "le", ">=", ">", "scalarlesel", "scalarlejoinsel"),
                (">=", "ge", "<=", "<", "scalargesel", "scalargejoinsel"),
            })
            {
                AddFunction(role, true, false);
                AddComparison(token, role, commutator, negator, restrict, join, false);
            }

            string family = Name("btree_ops");
            ordering = new(Identifier("btree_ops"), family, "btree", Typed(
                "CREATE OPERATOR FAMILY " + family + " USING btree;\nCREATE OPERATOR CLASS " + family + " DEFAULT FOR TYPE ",
                " USING btree FAMILY " + family + " AS\n    OPERATOR 1 " + Operator("<") + " (", ",",
                "),\n    OPERATOR 2 " + Operator("<=") + " (", ",", "),\n    OPERATOR 3 " + Operator("=") + " (", ",",
                "),\n    OPERATOR 4 " + Operator(">=") + " (", ",", "),\n    OPERATOR 5 " + Operator(">") + " (", ",",
                "),\n    FUNCTION 1 " + Name("cmp") + "(", ",", ");\n"));
        }

        Family? hashing = null;
        if (model.Hashing)
        {
            AddFunction("hash", false, true);
            string family = Name("hash_ops");
            hashing = new(Identifier("hash_ops"), family, "hash", Typed(
                "CREATE OPERATOR FAMILY " + family + " USING hash;\nCREATE OPERATOR CLASS " + family + " DEFAULT FOR TYPE ",
                " USING hash FAMILY " + family + " AS\n    OPERATOR 1 " + Operator("=") + " (", ",",
                "),\n    FUNCTION 1 " + Name("hash") + "(", ");\n"));
        }

        return new(Signature(Operator("="), false), new(functions), new(operators), ordering, hashing);

        string Identifier(string role) => Encoding.UTF8.GetByteCount(model.Name) + role.Length + 1 <= 63
            ? model.Name + "_" + role : "ankus_" + model.Symbol + "_" + role;

        string Name(string role) => SqlSchemaTemplate.Prefix(model.Schema) + SqlText.Identifier(Identifier(role));

        string Operator(string token) => SqlSchemaTemplate.Prefix(model.Schema) + token;

        Fragment Typed(params string[] parts) => new(new(parts), model.Type);

        Fragment Signature(string name, bool unary) => unary ? Typed(name + "(", ")") : Typed(name + "(", ",", ")");

        void AddFunction(string role, bool comparison, bool unary)
        {
            string name = Name(role);
            string tail = ") RETURNS " + (comparison ? "boolean" : "integer") + " AS 'MODULE_PATHNAME', 'ankus_fn_operator_" +
                model.Symbol + "_" + role + "' LANGUAGE c IMMUTABLE PARALLEL SAFE STRICT;\n";
            functions.Add(new(role, Identifier(role), name, Signature(name, unary), unary
                ? Typed("CREATE FUNCTION " + name + "(", tail) : Typed("CREATE FUNCTION " + name + "(", ",", tail)));
        }

        void AddComparison(string token, string role, string commutator, string negator, string restrict, string join, bool equal)
            => operators.Add(new(token, role, Signature(Operator(token), false), Typed(
                "CREATE OPERATOR " + Operator(token) + " (FUNCTION = " + Name(role) + ", LEFTARG = ", ", RIGHTARG = ",
                ", COMMUTATOR = OPERATOR(" + Operator(commutator) + "), NEGATOR = OPERATOR(" + Operator(negator) +
                "), RESTRICT = pg_catalog." + restrict + ", JOIN = pg_catalog." + join + (equal ? ", HASHES, MERGES" : string.Empty) + ");\n")));
    }

    /// <summary>
    /// Retains fixed text around repeated occurrences of one typed scalar identifier without textual substitution.
    /// </summary>
    /// <param name="Parts">The escaped text before, between and after typed identifier occurrences.</param>
    /// <param name="Type">The exact scalar SQL identity and provider policy.</param>
    internal sealed record Fragment(EquatableArray<string> Parts, SqlTypeTemplate Type)
    {
        /// <summary>
        /// Composes only current provider qualification between the cached text fragments.
        /// </summary>
        /// <param name="providers">The current validated provider inventory.</param>
        /// <returns>The complete SQL fragment with extension-schema markers.</returns>
        internal string Compose(SqlTypeProviders providers) => string.Join(Type.Emit(providers), Parts);
    }

    /// <summary>
    /// Retains one support function's catalog and selection identities independently of its native boundary.
    /// </summary>
    /// <param name="Role">The operation selecting the native helper.</param>
    /// <param name="Identifier">The unquoted local catalog name.</param>
    /// <param name="Name">The escaped schema-aware function name.</param>
    /// <param name="Signature">The typed catalog identity and attachment.</param>
    /// <param name="Definition">The fixed scalar DDL grammar.</param>
    internal sealed record Function(string Role, string Identifier, string Name, Fragment Signature, Fragment Definition);

    /// <summary>
    /// Retains one comparison operator's catalog identity and PostgreSQL selectivity clauses.
    /// </summary>
    /// <param name="Token">The operator token.</param>
    /// <param name="Role">The implementing support function.</param>
    /// <param name="Signature">The typed operator identity and attachment.</param>
    /// <param name="Definition">The escaped fixed operator declaration.</param>
    internal sealed record Comparison(string Token, string Role, Fragment Signature, Fragment Definition);

    /// <summary>
    /// Retains a generated family and default operator class separately from current graph options.
    /// </summary>
    /// <param name="Identifier">The unquoted local family name.</param>
    /// <param name="Name">The escaped schema-aware family name.</param>
    /// <param name="AccessMethod">The btree or hash access method.</param>
    /// <param name="Definition">The complete typed family and class DDL.</param>
    internal sealed record Family(string Identifier, string Name, string AccessMethod, Fragment Definition);
}
