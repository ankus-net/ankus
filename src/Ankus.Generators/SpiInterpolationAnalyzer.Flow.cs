using System.Collections.Immutable;
using System.Globalization;
using Ankus.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace Ankus.Generators;

public sealed partial class SpiInterpolationAnalyzer
{
    /// <summary>
    /// Follows the command values and builder objects that actually reach a use; overwritten writes are ignored.
    /// </summary>
    private sealed class SqlFlowAnalysis(INamedTypeSymbol spi, INamedTypeSymbol session, INamedTypeSymbol? enumerable,
        IAssemblySymbol core, CancellationToken cancellationToken)
    {
        private const int MaxAnalysisSteps = 10_000;
        private readonly Dictionary<(SyntaxTree Tree, TextSpan Span), bool> _results = [];
        private readonly Dictionary<ControlFlowGraph, FlowState?[]> _observed = [];
        private readonly Dictionary<int, ControlFlowGraph> _functions = [];
        private readonly Dictionary<int, IMethodSymbol> _functionSymbols = [];
        private readonly Dictionary<IMethodSymbol, ControlFlowGraph> _localFunctions = new(SymbolEqualityComparer.Default);
        private readonly Dictionary<(SyntaxNode Syntax, OperationKind Kind), int> _sites = [];
        private readonly Dictionary<int, FlowState> _creations = [];
        private readonly Dictionary<int, Dictionary<ISymbol, Value>> _laterLocals = [];
        private readonly Dictionary<int, Dictionary<int, Value>> _laterObjects = [];
        private readonly HashSet<int> _deferred = [];
        private readonly HashSet<ISymbol> _exposed = new(SymbolEqualityComparer.Default);
        private readonly HashSet<CaptureId> _referenceCaptures = [];
        private readonly HashSet<ControlFlowGraph> _active = [];
        private ControlFlowGraph _graph = null!;
        private int _analysisSteps;
        private bool _budgetExceeded;
        private bool _report;

        /// <summary>
        /// Resolves reaching command values to diagnostic decisions after the control-flow entries converge.
        /// </summary>
        /// <param name="graph">The compiler-owned graph for the current operation block.</param>
        /// <param name="root">The complete operation tree used to fail closed if the analysis budget is exhausted.</param>
        /// <param name="owner">The member whose parameters enter the block as unbound external values.</param>
        /// <returns>Exact source identities and whether their raw command construction is unsafe.</returns>
        internal Dictionary<(SyntaxTree Tree, TextSpan Span), bool> Analyze(ControlFlowGraph graph, IOperation root, ISymbol owner)
        {
            FindExposedStorage(root);
            FlowState initial = new();
            if (owner is IMethodSymbol method)
            {
                foreach (IParameterSymbol parameter in method.Parameters)
                {
                    initial.Locals[parameter] = Value.Raw();
                }
            }

            _ = Solve(graph, 0, graph.Blocks.Length - 1, initial);
            SolveDeferredCallbacks();
            if (_budgetExceeded)
            {
                MarkCommandsUnsafe(root);
                return _results;
            }

            _report = true;
            foreach (KeyValuePair<ControlFlowGraph, FlowState?[]> observed in _observed.ToArray())
            {
                _graph = observed.Key;
                foreach (BasicBlock block in observed.Key.Blocks)
                {
                    if (observed.Value[block.Ordinal] is not { } state)
                    {
                        continue;
                    }

                    EvaluateBlock(block, state.Clone());
                }
            }

            if (_budgetExceeded)
            {
                MarkCommandsUnsafe(root);
            }

            return _results;
        }

        /// <summary>
        /// Records locals and parameters whose storage can be written through an untracked managed reference.
        /// </summary>
        /// <param name="root">The complete operation tree, including nested callbacks.</param>
        private void FindExposedStorage(IOperation root)
        {
            foreach (IOperation operation in Descendants(root))
            {
                ISymbol? symbol = operation switch
                {
                    ILocalReferenceOperation local => local.Local,
                    IParameterReferenceOperation parameter => parameter.Parameter,
                    _ => null,
                };
                if (symbol is not null && operation.Parent switch
                {
                    IArgumentOperation { Parameter.RefKind: not RefKind.None } => true,
                    ISimpleAssignmentOperation { IsRef: true } binding => ReferenceEquals(binding.Value, operation),
                    IVariableInitializerOperation { Parent: IVariableDeclaratorOperation { Symbol.RefKind: not RefKind.None } } => true,
                    IConditionalOperation { IsRef: true } => true,
                    IAddressOfOperation => true,
                    _ => false,
                })
                {
                    _exposed.Add(symbol);
                }
            }
        }

        /// <summary>
        /// Analyzes callbacks that never ran locally or that lazy framework code can run after later writes.
        /// </summary>
        private void SolveDeferredCallbacks()
        {
            Dictionary<int, FlowState> solved = [];
            bool changed = true;
            while (changed && !_budgetExceeded)
            {
                changed = false;
                foreach (int site in _functions.Keys.ToArray())
                {
                    if (!_deferred.Contains(site) && _observed.ContainsKey(_functions[site]) && !solved.ContainsKey(site))
                    {
                        continue;
                    }

                    FlowState state = DeferredState(site);
                    if (solved.TryGetValue(site, out FlowState? previous) && state.Equivalent(previous))
                    {
                        continue;
                    }

                    solved[site] = state;
                    _ = Execute(_functions[site], state.Clone(), _functionSymbols[site]);
                    changed = true;
                    if (_budgetExceeded)
                    {
                        return;
                    }
                }
            }
        }

        /// <summary>
        /// Combines a callback's capture-time state with every value written after its creation.
        /// </summary>
        /// <param name="site">The callback identity.</param>
        /// <returns>The conservative state for an invocation at an unknown later time.</returns>
        private FlowState DeferredState(int site)
        {
            FlowState state = _creations.TryGetValue(site, out FlowState? created) ? created.Clone() : new();
            state.Returned = null;
            if (_laterLocals.TryGetValue(site, out Dictionary<ISymbol, Value>? locals))
            {
                foreach (KeyValuePair<ISymbol, Value> pair in locals)
                {
                    state.Locals[pair.Key] = Value.Merge(state.Locals.TryGetValue(pair.Key, out Value? current) ? current : Value.Raw(), pair.Value);
                }
            }

            if (_laterObjects.TryGetValue(site, out Dictionary<int, Value>? objects))
            {
                foreach (KeyValuePair<int, Value> pair in objects)
                {
                    state.Builders[pair.Key] = state.Builders.TryGetValue(pair.Key, out Value? current) ? Value.Merge(current, pair.Value) : pair.Value;
                }
            }

            return state;
        }

        /// <summary>
        /// Assigns a stable heap identity to an allocation operation, leaving odd identities for allocation summaries.
        /// </summary>
        /// <param name="operation">The allocating operation.</param>
        /// <returns>The even identity of the operation's most recent object.</returns>
        private int Site(IOperation operation)
        {
            (SyntaxNode Syntax, OperationKind Kind) key = (operation.Syntax, operation.Kind);
            if (!_sites.TryGetValue(key, out int site))
            {
                site = (_sites.Count + 1) * 2;
                _sites.Add(key, site);
            }

            return site;
        }

        /// <summary>
        /// Allocates a recent heap object and records its contents for callbacks that can observe it later.
        /// </summary>
        /// <param name="operation">The allocating operation.</param>
        /// <param name="content">The object's initial text or elements.</param>
        /// <param name="state">The current flow state.</param>
        /// <returns>The reference to the recent object.</returns>
        private Value Allocate(IOperation operation, Value content, FlowState state)
        {
            int site = Site(operation);
            Value allocated = state.Allocate(site, content);
            RecordObject(state, site, content);
            if (state.Builders.TryGetValue(site + 1, out Value? summary))
            {
                RecordObject(state, site + 1, summary);
            }

            return allocated;
        }

        /// <summary>
        /// Records a callback's capture identity and the state from which it can later run.
        /// </summary>
        /// <param name="site">The callback identity.</param>
        /// <param name="state">The state at the delegate creation.</param>
        /// <returns>The callback value.</returns>
        private Value Create(int site, FlowState state)
        {
            _creations[site] = _creations.TryGetValue(site, out FlowState? created) ? FlowState.Merge(created, state) : state.Clone();
            state.Created.Add(site);
            return Value.Function(site);
        }

        /// <summary>
        /// Updates one local or parameter and records the value for callbacks created earlier on the path.
        /// </summary>
        /// <param name="state">The current flow state.</param>
        /// <param name="symbol">The written storage.</param>
        /// <param name="value">The written value.</param>
        /// <param name="strong">Whether the write certainly replaces the previous value.</param>
        private void Write(FlowState state, ISymbol symbol, Value value, bool strong)
        {
            state.Locals[symbol] = strong ? value
                : Value.Merge(state.Locals.TryGetValue(symbol, out Value? previous) ? previous : Value.Raw(), value);
            foreach (int site in state.Created)
            {
                if (!_laterLocals.TryGetValue(site, out Dictionary<ISymbol, Value>? writes))
                {
                    writes = new(SymbolEqualityComparer.Default);
                    _laterLocals.Add(site, writes);
                }

                writes[symbol] = writes.TryGetValue(symbol, out Value? recorded) ? Value.Merge(recorded, value) : value;
            }
        }

        /// <summary>
        /// Writes a local through its tracked reference targets, or weakly through every exposed local when unresolved.
        /// </summary>
        /// <param name="state">The current flow state.</param>
        /// <param name="symbol">The local, parameter or reference alias being written.</param>
        /// <param name="value">The written value.</param>
        /// <param name="strong">Whether the write certainly replaces the previous value.</param>
        private void WriteLocal(FlowState state, ISymbol symbol, Value value, bool strong)
        {
            bool unresolved = state.Unresolved.Contains(symbol);
            Write(state, symbol, value, strong);
            if (state.Aliases.TryGetValue(symbol, out ImmutableArray<ISymbol> targets))
            {
                foreach (ISymbol target in targets)
                {
                    Write(state, target, value, strong && targets.Length == 1 && !unresolved);
                }
            }

            if (unresolved)
            {
                WriteExposed(state, value);
            }
        }

        /// <summary>
        /// Applies a write through an untracked managed reference to every local whose storage has escaped.
        /// </summary>
        /// <param name="state">The current flow state.</param>
        /// <param name="value">The written value.</param>
        private void WriteExposed(FlowState state, Value value)
        {
            foreach (ISymbol symbol in _exposed)
            {
                Write(state, symbol, value, strong: false);
            }
        }

        /// <summary>
        /// Updates a mutable heap object and records its contents for callbacks created earlier on the path.
        /// </summary>
        /// <param name="state">The current flow state.</param>
        /// <param name="site">The object identity.</param>
        /// <param name="value">The object's new contents.</param>
        private void WriteObject(FlowState state, int site, Value value)
        {
            state.Builders[site] = value;
            RecordObject(state, site, value);
        }

        /// <summary>
        /// Records object contents, including summarized older objects under their original allocation identity.
        /// </summary>
        /// <param name="state">The current flow state.</param>
        /// <param name="site">The object identity.</param>
        /// <param name="value">The recorded contents.</param>
        private void RecordObject(FlowState state, int site, Value value)
        {
            foreach (int created in state.Created)
            {
                if (!_laterObjects.TryGetValue(created, out Dictionary<int, Value>? writes))
                {
                    writes = [];
                    _laterObjects.Add(created, writes);
                }

                writes[site] = writes.TryGetValue(site, out Value? recorded) ? Value.Merge(recorded, value) : value;
                if (site % 2 == 1)
                {
                    writes[site - 1] = writes.TryGetValue(site - 1, out Value? original) ? Value.Merge(original, value) : value;
                }
            }
        }

        /// <summary>
        /// Reads a local directly, or through every possible referent of a reference alias.
        /// </summary>
        /// <param name="symbol">The local or parameter.</param>
        /// <param name="state">The current flow state.</param>
        /// <returns>The possible reaching values.</returns>
        private static Value Read(ISymbol symbol, FlowState state)
        {
            Value own = state.Locals.TryGetValue(symbol, out Value? value) ? value : Value.Raw();
            if (!state.Aliases.TryGetValue(symbol, out ImmutableArray<ISymbol> targets) || targets.IsEmpty)
            {
                return own;
            }

            Value? read = state.Unresolved.Contains(symbol) ? own : null;
            foreach (ISymbol target in targets)
            {
                Value current = state.Locals.TryGetValue(target, out Value? stored) ? stored : Value.Raw();
                read = read is null ? current : Value.Merge(read, current);
            }

            return read ?? own;
        }

        /// <summary>
        /// Binds a reference local or parameter to the storage selected by a ref assignment.
        /// </summary>
        /// <param name="alias">The reference being bound.</param>
        /// <param name="referent">The bound storage expression.</param>
        /// <param name="value">The current value of the bound storage.</param>
        /// <param name="state">The current flow state.</param>
        private void Bind(ISymbol alias, IOperation referent, Value value, FlowState state)
        {
            List<ISymbol> targets = [];
            bool unresolved = false;
            switch (referent)
            {
                case ILocalReferenceOperation local:
                    Resolve(local.Local);
                    break;
                case IParameterReferenceOperation parameter:
                    Resolve(parameter.Parameter);
                    break;
                case IFlowCaptureReferenceOperation capture when !_referenceCaptures.Contains(capture.Id) &&
                    state.CaptureTargets.TryGetValue(capture.Id, out ImmutableArray<ISymbol> captured):
                    foreach (ISymbol symbol in captured)
                    {
                        Resolve(symbol);
                    }

                    break;
                default:
                    unresolved = true;
                    break;
            }

            state.Locals[alias] = value;
            if (targets.Count == 0)
            {
                state.Aliases.Remove(alias);
            }
            else
            {
                state.Aliases[alias] = [.. targets.Distinct(SymbolEqualityComparer.Default)];
            }

            if (unresolved)
            {
                state.Unresolved.Add(alias);
            }
            else
            {
                state.Unresolved.Remove(alias);
            }

            void Resolve(ISymbol symbol)
            {
                if (state.Aliases.TryGetValue(symbol, out ImmutableArray<ISymbol> existing))
                {
                    targets.AddRange(existing);
                    unresolved |= state.Unresolved.Contains(symbol);
                }
                else if (state.Unresolved.Contains(symbol))
                {
                    unresolved = true;
                }
                else
                {
                    targets.Add(symbol);
                }
            }
        }

        /// <summary>
        /// Identifies writable managed references whose referent is not a tracked local.
        /// </summary>
        /// <param name="operation">The referenced storage expression.</param>
        /// <returns>Whether a write can target an escaped local without a tracked alias.</returns>
        private static bool IsUntrackedReference(IOperation operation)
            => operation is IInvocationOperation { TargetMethod.ReturnsByRef: true } or
                IPropertyReferenceOperation { Property.ReturnsByRef: true } or
                IFieldReferenceOperation { Field.RefKind: not RefKind.None };

        private FlowState Solve(ControlFlowGraph graph, int first, int last, FlowState initial)
        {
            ControlFlowGraph previousGraph = _graph;
            _graph = graph;
            foreach (IMethodSymbol function in graph.LocalFunctions)
            {
                _localFunctions[function] = graph.GetLocalFunctionControlFlowGraph(function, cancellationToken);
            }

            FlowState?[] incoming = new FlowState?[graph.Blocks.Length];
            Queue<int> work = new();
            incoming[first] = initial.Clone();
            work.Enqueue(first);
            FlowState? output = null;
            while (work.Count != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++_analysisSteps > MaxAnalysisSteps)
                {
                    _budgetExceeded = true;
                    _graph = previousGraph;
                    return Conservative(initial);
                }

                int ordinal = work.Dequeue();
                BasicBlock block = graph.Blocks[ordinal];
                FlowState state = incoming[ordinal]!.Clone();
                foreach (IOperation operation in block.Operations)
                {
                    if (CanThrow(operation))
                    {
                        SeedHandlers(block.EnclosingRegion, state);
                    }

                    _ = Evaluate(operation, state);
                    if (CanThrow(operation))
                    {
                        SeedHandlers(block.EnclosingRegion, state);
                    }
                }

                if (block.BranchValue is { } branchValue)
                {
                    if (CanThrow(branchValue) || block.FallThroughSuccessor?.Semantics is ControlFlowBranchSemantics.Throw or ControlFlowBranchSemantics.Rethrow)
                    {
                        SeedHandlers(block.EnclosingRegion, state);
                    }

                    Value branchResult = Evaluate(branchValue, state);
                    if (block.FallThroughSuccessor?.Semantics == ControlFlowBranchSemantics.Return)
                    {
                        state.Returned = branchResult;
                    }
                }

                if (block.Kind == BasicBlockKind.Exit)
                {
                    output = output is null ? state : FlowState.Merge(output, state);
                }

                foreach (ControlFlowBranch branch in Successors(block))
                {
                    FlowState propagated = state.Clone();
                    foreach (ControlFlowRegion finalizer in branch.FinallyRegions)
                    {
                        propagated = Solve(graph, finalizer.FirstBlockOrdinal, finalizer.LastBlockOrdinal, propagated);
                    }

                    if (branch.Destination is { IsReachable: true } destination && destination.Ordinal >= first && destination.Ordinal <= last)
                    {
                        Seed(destination.Ordinal, propagated, destination.Ordinal <= ordinal);
                    }
                    else if (branch.Semantics == ControlFlowBranchSemantics.StructuredExceptionHandling)
                    {
                        output = output is null ? propagated : FlowState.Merge(output, propagated);
                    }
                }
            }

            if (!_observed.TryGetValue(graph, out FlowState?[]? seen))
            {
                seen = new FlowState?[graph.Blocks.Length];
                _observed.Add(graph, seen);
            }

            for (int index = first; index <= last; index++)
            {
                if (incoming[index] is { } current)
                {
                    seen[index] = seen[index] is { } old ? FlowState.Merge(old, current) : current;
                }
            }

            _graph = previousGraph;
            return output ?? initial.Clone();

            void Seed(int ordinal, FlowState state, bool backEdge = false)
            {
                FlowState propagated = state.Clone();
                if (backEdge && incoming[ordinal] is { } prior)
                {
                    propagated.Widen(prior);
                }

                FlowState merged = incoming[ordinal] is { } previous ? FlowState.Merge(previous, propagated) : propagated;
                if (incoming[ordinal] is null || !merged.Equivalent(incoming[ordinal]!))
                {
                    incoming[ordinal] = merged;
                    work.Enqueue(ordinal);
                }
            }

            void SeedHandlers(ControlFlowRegion? region, FlowState state)
            {
                for (; region is not null; region = region.EnclosingRegion)
                {
                    if (region.Kind == ControlFlowRegionKind.Try && region.EnclosingRegion is { Kind: ControlFlowRegionKind.TryAndCatch } parent)
                    {
                        foreach (ControlFlowRegion handler in parent.NestedRegions.Where(handler => handler.Kind is ControlFlowRegionKind.Catch or ControlFlowRegionKind.FilterAndHandler &&
                                handler.FirstBlockOrdinal >= first && handler.LastBlockOrdinal <= last))
                        {
                            Seed(handler.FirstBlockOrdinal, state);
                        }
                    }
                }
            }
        }

        private static FlowState Conservative(FlowState state)
        {
            FlowState conservative = state.Clone();
            Mark(conservative.Locals);
            Mark(conservative.Captures);
            Mark(conservative.Builders);
            conservative.Returned = Value.Raw().WithUnsafe();
            return conservative;

            static void Mark<TKey>(Dictionary<TKey, Value> values) where TKey : notnull
            {
                foreach (TKey key in values.Keys.ToArray())
                {
                    values[key] = Value.Unspecified(values[key]).WithUnsafe();
                }
            }
        }

        private void MarkCommandsUnsafe(IOperation root)
        {
            foreach (IInvocationOperation invocation in Descendants(root).OfType<IInvocationOperation>())
            {
                if (!SymbolEqualityComparer.Default.Equals(invocation.TargetMethod.ContainingType, spi) &&
                    !SymbolEqualityComparer.Default.Equals(invocation.TargetMethod.ContainingType, session))
                {
                    continue;
                }

                foreach (IArgumentOperation argument in invocation.Arguments.Where(static argument => argument.Parameter is { Name: "commandText", Type.SpecialType: SpecialType.System_String }))
                {
                    _results[(argument.Value.Syntax.SyntaxTree, argument.Value.Syntax.Span)] = true;
                }
            }
        }

        private static bool CanThrow(IOperation operation)
            => operation is IInvocationOperation or IObjectCreationOperation or IPropertyReferenceOperation or IAwaitOperation or
                IInterpolatedStringOperation or IThrowOperation or IDynamicInvocationOperation ||
                operation.ChildOperations.Any(static child => CanThrow(child));

        private void EvaluateBlock(BasicBlock block, FlowState state)
        {
            foreach (IOperation operation in block.Operations)
            {
                _ = Evaluate(operation, state);
            }

            if (block.BranchValue is { } condition)
            {
                Value value = Evaluate(condition, state);
                if (block.FallThroughSuccessor?.Semantics == ControlFlowBranchSemantics.Return)
                {
                    state.Returned = value;
                }
            }
        }

        private Value Evaluate(IOperation operation, FlowState state)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (operation.ConstantValue.HasValue)
            {
                return operation.Type?.TypeKind == TypeKind.Enum ? Value.UnknownConstant() : Value.Constant(operation.ConstantValue.Value);
            }

            switch (operation)
            {
                case ILocalReferenceOperation reference:
                    return Read(reference.Local, state);
                case IParameterReferenceOperation reference:
                    return Read(reference.Parameter, state);
                case IFlowCaptureReferenceOperation reference:
                    return state.Captures.TryGetValue(reference.Id, out Value? captured) ? captured : Value.Raw();
                case IFlowCaptureOperation capture:
                    Value capturedValue = Evaluate(capture.Value, state);
                    state.Captures[capture.Id] = capturedValue;
                    if (capture.Value is ILocalReferenceOperation capturedLocal)
                    {
                        state.CaptureTargets[capture.Id] = [capturedLocal.Local];
                    }
                    else if (capture.Value is IParameterReferenceOperation capturedParameter)
                    {
                        state.CaptureTargets[capture.Id] = [capturedParameter.Parameter];
                    }
                    else if (IsUntrackedReference(capture.Value))
                    {
                        _referenceCaptures.Add(capture.Id);
                    }

                    return capturedValue;
                case IExpressionStatementOperation statement:
                    return Evaluate(statement.Operation, state);
                case IParenthesizedOperation parentheses:
                    return Evaluate(parentheses.Operand, state);
                case IConversionOperation conversion:
                    Value converted = Evaluate(conversion.Operand, state);
                    return conversion.OperatorMethod is null ? converted : Value.Unspecified(converted);
                case IDelegateCreationOperation creation:
                    return Evaluate(creation.Target, state);
                case IFlowAnonymousFunctionOperation function:
                    int functionSite = function.Syntax.SpanStart;
                    _functions[functionSite] = _graph.GetAnonymousFunctionControlFlowGraph(function, cancellationToken);
                    _functionSymbols[functionSite] = function.Symbol;
                    return Create(functionSite, state);
                case IMethodReferenceOperation reference when _localFunctions.TryGetValue(reference.Method.OriginalDefinition, out ControlFlowGraph? localGraph):
                    int localSite = reference.Method.DeclaringSyntaxReferences[0].Span.Start;
                    _functions[localSite] = localGraph;
                    _functionSymbols[localSite] = reference.Method.OriginalDefinition;
                    return Create(localSite, state);
                case ISimpleAssignmentOperation { IsRef: true } binding:
                    Value referent = Evaluate(binding.Value, state);
                    if (binding.Target is ILocalReferenceOperation { Local: { } alias })
                    {
                        Bind(alias, binding.Value, referent, state);
                    }
                    else if (binding.Target is IParameterReferenceOperation { Parameter: { } parameterAlias })
                    {
                        Bind(parameterAlias, binding.Value, referent, state);
                    }

                    return referent;
                case ISimpleAssignmentOperation assignment:
                    Value assigned = Evaluate(assignment.Value, state);
                    Assign(assignment.Target, assigned, state);
                    return assigned;
                case IDeconstructionAssignmentOperation assignment:
                    Value tupleValue = Evaluate(assignment.Value, state);
                    Assign(assignment.Target, tupleValue, state);
                    return tupleValue;
                case ITupleOperation tuple:
                    return Value.Array([.. tuple.Elements.Select(element => Evaluate(element, state))]);
                case IArrayCreationOperation { Initializer: { } initializer }:
                    return Allocate(operation, Value.Array([.. initializer.ElementValues.Select(element => Evaluate(element, state))]), state);
                case ICollectionExpressionOperation collection:
                    ImmutableArray<Value> items = [.. collection.Elements.Select(element => Evaluate(element, state))];
                    return Allocate(operation, collection.Elements.Any(static element => element is ISpreadOperation)
                        ? Value.Unspecified(Value.Raw()) : Value.Array(items), state);
                case IArrayElementReferenceOperation element:
                    Value elements = ReadBuilders(Evaluate(element.ArrayReference, state), state);
                    if (!elements.Elements.IsDefault && element.Indices.Length == 1 && Evaluate(element.Indices[0], state) is
                        { HasConstant: true, ConstantObject: int elementIndex } && elementIndex >= 0 && elementIndex < elements.Elements.Length)
                    {
                        return elements.Elements[elementIndex];
                    }

                    return elements.Elements.IsDefault ? Value.Unspecified(elements) :
                        elements.Elements.Aggregate((Value?)null, static (previous, value) => previous is null ? value : Value.Merge(previous, value)) ?? Value.Raw();
                case IIncrementOrDecrementOperation increment:
                    Value prior = Evaluate(increment.Target, state);
                    bool increase = increment.Kind == OperationKind.Increment;
                    Value next = prior is { HasConstant: true, ConstantObject: int number } &&
                        (increase ? number < int.MaxValue : number > int.MinValue)
                        ? Value.Constant(number + (increase ? 1 : -1)) : Value.Unspecified(prior);
                    Assign(increment.Target, next, state);
                    return increment.IsPostfix ? prior : next;
                case ICompoundAssignmentOperation assignment:
                    Value combined = Join(AsText(Evaluate(assignment.Target, state), state), AsText(Evaluate(assignment.Value, state), state));
                    Assign(assignment.Target, combined, state);
                    return combined;
                case IInterpolatedStringOperation interpolation:
                    Value text = Value.Constant(string.Empty);
                    foreach (IInterpolatedStringContentOperation part in interpolation.Parts)
                    {
                        if (part is IInterpolatedStringTextOperation literal)
                        {
                            text = Join(text, Evaluate(literal.Text, state));
                        }
                        else if (part is IInterpolationOperation hole)
                        {
                            Value holeValue = AsText(Evaluate(hole.Expression, state), state);
                            text = Join(text, holeValue);
                            if (hole.Alignment is not null || hole.FormatString is not null)
                            {
                                text = text.WithUnsafe();
                            }
                        }
                    }

                    return text;
                case IBinaryOperation { OperatorKind: BinaryOperatorKind.Add, Type.SpecialType: SpecialType.System_String } addition:
                    return Join(AsText(Evaluate(addition.LeftOperand, state), state), AsText(Evaluate(addition.RightOperand, state), state));
                case IInvocationOperation invocation:
                    return Invoke(invocation, state);
                case IObjectCreationOperation { Constructor: { } constructor } creation when IsBuilder(constructor.ContainingType):
                    List<(IArgumentOperation Argument, Value Value)> construction = [.. creation.Arguments.Select(argument =>
                        (Argument: argument, Value: Evaluate(argument.Value, state)))];
                    (IArgumentOperation Argument, Value Value) initial = construction.FirstOrDefault(static pair => pair.Argument.Parameter?.Name == "value");
                    Value initialText = initial.Argument is null ? Value.Constant(string.Empty)
                        : Join(Value.Constant(string.Empty), AsText(initial.Value, state));
                    if (construction.Any(static pair => pair.Argument.Parameter?.Name == "startIndex"))
                    {
                        initialText = Slice(initialText, construction);
                    }

                    return Allocate(creation, initialText, state);
                case IObjectCreationOperation creation:
                    List<(IArgumentOperation Argument, Value Value)> constructorArguments = [.. creation.Arguments.Select(argument =>
                        (Argument: argument, Value: Evaluate(argument.Value, state)))];
                    if (creation.Initializer is { } objectInitializer)
                    {
                        _ = Evaluate(objectInitializer, state);
                    }

                    ApplyUnknownCall(creation.Constructor, constructorArguments, state);
                    return Value.Raw();
                case IFieldReferenceOperation { Field: { Name: "Empty", ContainingType.SpecialType: SpecialType.System_String } }:
                    return Value.Constant(string.Empty);
            }

            foreach (IOperation child in operation.ChildOperations)
            {
                _ = Evaluate(child, state);
            }

            return Value.Raw();
        }

        private Value Invoke(IInvocationOperation invocation, FlowState state)
        {
            Value? receiver = invocation.Instance is { } instance ? Evaluate(instance, state) : null;
            List<(IArgumentOperation Argument, Value Value)> arguments = [];
            foreach (IArgumentOperation argument in invocation.Arguments)
            {
                arguments.Add((argument, Evaluate(argument.Value, state)));
            }

            IMethodSymbol method = invocation.TargetMethod;
            if (_localFunctions.TryGetValue(method.OriginalDefinition, out ControlFlowGraph? localFunction))
            {
                return Execute(localFunction, state, method.OriginalDefinition, arguments);
            }

            if (method.MethodKind == MethodKind.DelegateInvoke && receiver is { } functionValue)
            {
                FlowState? effects = null;
                Value? returned = null;
                foreach (int site in functionValue.Functions)
                {
                    FlowState called = state.Clone();
                    Value result = Execute(_functions[site], called, _functionSymbols[site], arguments);
                    effects = effects is null ? called : FlowState.Merge(effects, called);
                    returned = returned is null ? result : Value.Merge(returned, result);
                }

                if (effects is not null)
                {
                    state.Adopt(effects);
                }

                return returned ?? Value.Raw();
            }

            if (IsQuotedCall(invocation, spi))
            {
                return Value.Quote(method.Name == "QuoteLiteral" ? '\'' : '"');
            }

            if (IsBuilder(method.ContainingType) && receiver is { } builder)
            {
                Value current = ReadBuilders(builder, state);
                if (method.Name == "ToString" && arguments.Count == 0)
                {
                    return current;
                }

                if (method.Name == "ToString" && arguments.Count == 2)
                {
                    return Slice(current, arguments);
                }

                if (method.ReturnType is not INamedTypeSymbol result || !IsBuilder(result))
                {
                    // StringBuilder mutators return the builder; queries such as Equals and EnsureCapacity leave its text unchanged.
                    return Value.Raw();
                }

                Value updated = method.Name switch
                {
                    "Clear" when arguments.Count == 0 => Value.Constant(string.Empty),
                    "Append" when arguments.Count == 1 => Join(current, AsText(arguments[0].Value, state)),
                    "AppendLine" when arguments.Count <= 1 => Join(arguments.Count == 0 ? current : Join(current, AsText(arguments[0].Value, state)),
                        Value.Constant("\n")),
                    "AppendFormat" => Join(current, Format(invocation, arguments, state)),
                    "AppendJoin" => Join(current, JoinValues(arguments, state)),
                    _ => arguments.Any(static pair => InsertsText(pair.Argument) && !pair.Value.IsLiteral)
                        ? Join(current, Value.Raw()) : Value.Unspecified(current),
                };
                foreach (int id in builder.References)
                {
                    WriteObject(state, id, builder.References.Length == 1 && !state.Summaries.Contains(id) ? updated
                        : Value.Merge(state.Builders.TryGetValue(id, out Value? previous) ? previous : Value.Raw(), updated));
                }

                return builder;
            }

            if (method.ContainingType.SpecialType == SpecialType.System_String)
            {
                switch (method.Name)
                {
                    case "Format":
                        return Format(invocation, arguments, state);
                    case "Join":
                        return JoinValues(arguments, state);
                    case "Concat":
                        return Concatenate(arguments, state);
                }

                if (method.ReturnType.SpecialType == SpecialType.System_String)
                {
                    return Transform(receiver, arguments);
                }
            }

            if (enumerable is not null && SymbolEqualityComparer.Default.Equals(method.ContainingType, enumerable))
            {
                // Enumerable callbacks can run during later enumeration, after captured locals change.
                foreach ((_, Value value) in arguments)
                {
                    _deferred.UnionWith(value.Functions);
                }

                if (method.Name == "Select")
                {
                    RunCallbacks(method, arguments, state);
                    (IArgumentOperation Argument, Value Value) selector = arguments.FirstOrDefault(static pair => pair.Argument.Parameter?.Name == "selector");
                    if (selector.Argument is not null && TryQuotedSelector(selector.Argument.Value, out Value quoted))
                    {
                        return Value.Array([quoted, quoted]);
                    }

                    return Value.Array([Value.Raw().WithUnsafe()]);
                }
            }

            if (_report && (SymbolEqualityComparer.Default.Equals(method.ContainingType, spi) ||
                SymbolEqualityComparer.Default.Equals(method.ContainingType, session)))
            {
                foreach ((IArgumentOperation argument, Value value) in arguments)
                {
                    if (argument.Parameter is { Name: "commandText", Type.SpecialType: SpecialType.System_String })
                    {
                        bool error = RequiresDiagnostic(value);
                        (SyntaxTree Tree, TextSpan Span) key = (argument.Value.Syntax.SyntaxTree, argument.Value.Syntax.Span);
                        _results[key] = error || _results.TryGetValue(key, out bool existing) && existing;
                    }
                }
            }

            ApplyUnknownCall(method, arguments, state);
            return Value.Raw();
        }

        /// <summary>
        /// Applies the possible effects of external code on reference arguments, heap arguments and callbacks.
        /// </summary>
        /// <param name="method">The called method or constructor.</param>
        /// <param name="arguments">The evaluated arguments.</param>
        /// <param name="state">The current flow state.</param>
        private void ApplyUnknownCall(IMethodSymbol? method, List<(IArgumentOperation Argument, Value Value)> arguments, FlowState state)
        {
            foreach ((IArgumentOperation argument, _) in arguments)
            {
                if (argument.Parameter?.RefKind is RefKind.Ref or RefKind.Out)
                {
                    Assign(argument.Value, Value.Unspecified(Evaluate(argument.Value, state)), state);
                }
            }

            foreach ((_, Value value) in arguments)
            {
                foreach (int site in value.References)
                {
                    if (!(state.Builders.TryGetValue(site, out Value? previous)))
                    {
                        continue;
                    }

                    WriteObject(state, site, Value.Unspecified(previous));
                }
            }

            RunCallbacks(method, arguments, state);
        }

        /// <summary>
        /// Runs callbacks passed to external code, treating them as possibly skipped unless the SPI connection runs them.
        /// </summary>
        /// <param name="method">The called method or constructor.</param>
        /// <param name="arguments">The evaluated arguments.</param>
        /// <param name="state">The current flow state.</param>
        private void RunCallbacks(IMethodSymbol? method, List<(IArgumentOperation Argument, Value Value)> arguments, FlowState state)
        {
            bool definite = method is not null && SymbolEqualityComparer.Default.Equals(method.ContainingType, spi) && method.Name == "Connect";
            foreach ((_, Value value) in arguments)
            {
                foreach (int site in value.Functions)
                {
                    FlowState called = state.Clone();
                    _ = Execute(_functions[site], called, _functionSymbols[site]);
                    state.Adopt(definite ? called : FlowState.Merge(state, called));
                }
            }
        }

        /// <summary>
        /// Identifies arguments whose characters a string or builder operation can place into the result text.
        /// </summary>
        /// <param name="argument">The bound argument.</param>
        /// <returns>Whether the argument supplies inserted text; an index, count or option returns false.</returns>
        private static bool InsertsText(IArgumentOperation argument)
            => argument.Parameter?.Type is { } type && (type.SpecialType is SpecialType.System_String or SpecialType.System_Object or SpecialType.System_Char ||
                type is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Char } ||
                type is INamedTypeSymbol { Name: "ReadOnlySpan" or "ReadOnlyMemory", TypeArguments.Length: 1 } span &&
                    span.TypeArguments[0].SpecialType == SpecialType.System_Char);

        /// <summary>
        /// Models an unlisted string transformation without treating mixed text or a transformed hazard as external input.
        /// </summary>
        /// <param name="receiver">The transformed instance, or null for a static method.</param>
        /// <param name="arguments">The evaluated arguments.</param>
        /// <returns>The unsafe or external result text.</returns>
        private static Value Transform(Value? receiver, List<(IArgumentOperation Argument, Value Value)> arguments)
        {
            Value[] inserted = [.. arguments.Where(static pair => InsertsText(pair.Argument)).Select(static pair => pair.Value)];
            bool mixed = inserted.Any(static value => !value.IsLiteral) && (receiver is not null || inserted.Length > 1);
            return mixed || receiver is { Unsafe: true } || arguments.Any(static pair => pair.Value.Unsafe)
                ? Value.Raw().WithUnsafe() : Value.Raw();
        }

        /// <summary>
        /// Joins an exactly known sequence with its separator, failing closed for a sequence with unknown elements.
        /// </summary>
        /// <param name="arguments">The evaluated separator and values arguments.</param>
        /// <param name="state">The current flow state.</param>
        /// <returns>The joined text.</returns>
        private static Value JoinValues(List<(IArgumentOperation Argument, Value Value)> arguments, FlowState state)
        {
            (IArgumentOperation Argument, Value Value) separator = arguments.FirstOrDefault(static pair => pair.Argument.Parameter?.Name == "separator");
            (IArgumentOperation Argument, Value Value) values = arguments.FirstOrDefault(static pair => pair.Argument.Parameter?.Name is "values" or "value");
            if (separator.Argument is null || values.Argument is null || arguments.Count != 2)
            {
                return Value.Raw().WithUnsafe();
            }

            Value sequence = values.Value.References.IsEmpty ? values.Value : ReadBuilders(values.Value, state);
            if (sequence.Elements.IsDefault)
            {
                return Value.Raw().WithUnsafe();
            }

            Value delimiter = AsText(separator.Value, state);
            Value joined = Value.Constant(string.Empty);
            for (int index = 0; index < sequence.Elements.Length; index++)
            {
                if (index != 0)
                {
                    joined = Join(joined, delimiter);
                }

                joined = Join(joined, AsText(sequence.Elements[index], state));
            }

            return joined;
        }

        /// <summary>
        /// Concatenates direct and sequence arguments in order, failing closed for a sequence with unknown elements.
        /// </summary>
        /// <param name="arguments">The evaluated concatenation arguments.</param>
        /// <param name="state">The current flow state.</param>
        /// <returns>The concatenated text.</returns>
        private static Value Concatenate(List<(IArgumentOperation Argument, Value Value)> arguments, FlowState state)
        {
            Value text = Value.Constant(string.Empty);
            foreach ((IArgumentOperation argument, Value value) in arguments)
            {
                if (argument.Parameter?.Type.SpecialType is SpecialType.System_String or SpecialType.System_Object)
                {
                    text = Join(text, AsText(value, state));
                    continue;
                }

                Value sequence = value.References.IsEmpty ? value : ReadBuilders(value, state);
                if (sequence.Elements.IsDefault)
                {
                    return Value.Raw().WithUnsafe();
                }

                foreach (Value element in sequence.Elements)
                {
                    text = Join(text, AsText(element, state));
                }
            }

            return text;
        }

        /// <summary>
        /// Converts a formatted object to its reaching text, reading builder contents and rejecting structured values.
        /// </summary>
        /// <param name="value">The formatted value.</param>
        /// <param name="state">The current flow state.</param>
        /// <returns>The text the framework formats for the value.</returns>
        private static Value AsText(Value value, FlowState state)
        {
            Value text = value;
            if (!value.References.IsEmpty)
            {
                Value content = ReadBuilders(value, state);
                Value remainder = value.WithoutReferences();
                text = remainder.Texts.IsEmpty && !remainder.Unknown && !remainder.Unsafe && !remainder.Input && remainder.Elements.IsDefault
                    ? content : Value.Merge(content, remainder);
            }

            return text.Elements.IsDefault ? text : Value.Unspecified(text);
        }

        /// <summary>
        /// Recognizes a framework delegate bound directly to one of the runtime-owned quoting methods.
        /// </summary>
        /// <param name="operation">The selector conversion or method reference.</param>
        /// <param name="quoted">The representative complete quoted atom.</param>
        /// <returns>Whether the selector quotes every input without user code.</returns>
        private bool TryQuotedSelector(IOperation operation, out Value quoted)
        {
            while (true)
            {
                if (operation is IConversionOperation { OperatorMethod: null } conversion)
                {
                    operation = conversion.Operand;
                }
                else if (operation is IParenthesizedOperation parentheses)
                {
                    operation = parentheses.Operand;
                }
                else if (operation is IDelegateCreationOperation creation)
                {
                    operation = creation.Target;
                }
                else
                {
                    break;
                }
            }

            if (operation is IMethodReferenceOperation { Method: { IsStatic: true, ReturnType.SpecialType: SpecialType.System_String } method } &&
                SymbolEqualityComparer.Default.Equals(method.ContainingType, spi) &&
                method.Name is "QuoteIdentifier" or "QuoteLiteral")
            {
                quoted = Value.Quote(method.Name == "QuoteLiteral" ? '\'' : '"');
                return true;
            }

            quoted = Value.Raw();
            return false;
        }

        private Value Execute(ControlFlowGraph function, FlowState state, IMethodSymbol? signature = null,
            List<(IArgumentOperation Argument, Value Value)>? arguments = null)
        {
            if (!_active.Add(function))
            {
                foreach (ISymbol symbol in state.Locals.Keys.ToArray())
                {
                    Write(state, symbol, Value.Unspecified(state.Locals[symbol]), strong: true);
                }

                return Value.Raw().WithUnsafe();
            }

            FlowState initial = state.Clone();
            initial.Returned = null;
            if (signature is not null)
            {
                foreach (IParameterSymbol parameter in signature.Parameters)
                {
                    initial.Locals[parameter] = Value.Raw();
                    initial.Aliases.Remove(parameter);
                    initial.Unresolved.Remove(parameter);
                }

                foreach ((IArgumentOperation argument, Value value) in arguments ?? [])
                {
                    if (argument.Parameter is { } parameter && parameter.Ordinal < signature.Parameters.Length)
                    {
                        initial.Locals[signature.Parameters[parameter.Ordinal]] = value;
                    }
                }
            }

            FlowState result = Solve(function, 0, function.Blocks.Length - 1, initial);
            _active.Remove(function);
            foreach (KeyValuePair<ISymbol, Value> pair in result.Locals)
            {
                state.Locals[pair.Key] = pair.Value;
            }

            foreach (KeyValuePair<int, Value> pair in result.Builders)
            {
                state.Builders[pair.Key] = pair.Value;
            }

            state.Summaries.UnionWith(result.Summaries);
            state.Created.UnionWith(result.Created);
            if (signature is not null && arguments is not null)
            {
                foreach ((IArgumentOperation argument, _) in arguments)
                {
                    if (argument.Parameter is { RefKind: RefKind.Ref or RefKind.Out } parameter &&
                        result.Locals.TryGetValue(signature.Parameters[parameter.Ordinal], out Value? value))
                    {
                        Assign(argument.Value, value, state);
                    }
                }
            }

            return result.Returned ?? Value.Raw();
        }

        private Value Format(IInvocationOperation invocation, List<(IArgumentOperation Argument, Value Value)> arguments, FlowState state)
        {
            (IArgumentOperation Argument, Value Value) template = arguments.FirstOrDefault(static pair => pair.Argument.Parameter?.Name == "format");
            if (template.Argument?.Value.ConstantValue is not { HasValue: true, Value: string format } ||
                arguments.Any(pair => pair.Argument.Parameter?.Name == "provider" && !IsInvariantProvider(pair.Argument.Value, core)))
            {
                return Value.Raw().WithUnsafe();
            }

            try
            {
                // A template using no arguments cannot format their runtime values into SQL.
                return Value.Constant(string.Format(CultureInfo.InvariantCulture, format, []));
            }
            catch (FormatException)
            {
                // Continue with the actual arity and argument-use check.
            }

            List<Value> values = [];
            foreach ((IArgumentOperation argument, Value value) in arguments)
            {
                if (argument.Parameter?.Name is "format" or "provider")
                {
                    continue;
                }

                if (argument.Parameter is { IsParams: true } || argument.Parameter?.Type is IArrayTypeSymbol)
                {
                    Value elements = value.References.IsEmpty ? value : ReadBuilders(value, state);
                    if (elements.Elements.IsDefault)
                    {
                        return Value.Raw().WithUnsafe();
                    }

                    values.AddRange(elements.Elements.Select(element => AsText(element, state)));
                    continue;
                }

                values.Add(AsText(value, state));
            }

            HashSet<int> used = [];
            object?[] probes = [.. Enumerable.Range(0, values.Count).Select(index => new FormatUse(index, used))];
            try
            {
                _ = string.Format(CultureInfo.InvariantCulture, format, probes);
            }
            catch (FormatException)
            {
                return Value.Known([]);
            }

            for (int index = 0; index < values.Count; index++)
            {
                if (!used.Contains(index))
                {
                    values[index] = Value.Constant(string.Empty);
                }
            }

            if (values.Any(static value => value.Unknown || value.Unsafe || !value.References.IsEmpty))
            {
                return Value.Raw().WithUnsafe();
            }

            List<Text> results = [];
            bool failed = false;
            Compose(0, [], []);
            return failed || _budgetExceeded ? Value.Raw().WithUnsafe() : Value.Known(results);

            void Compose(int index, List<object?> replacements, List<string> markers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (failed || results.Count > Value.MaxLayouts)
                {
                    return;
                }

                // Layout enumeration shares the flow budget so adversarial argument choices fail closed.
                if (++_analysisSteps > MaxAnalysisSteps)
                {
                    _budgetExceeded = true;
                    failed = true;
                    return;
                }

                if (index == values.Count)
                {
                    string command;
                    try
                    {
                        object?[] formattedValues = [.. replacements];
                        command = string.Format(CultureInfo.InvariantCulture, format, formattedValues);
                    }
                    catch (FormatException)
                    {
                        return;
                    }

                    List<int> positions = [];
                    foreach (string marker in markers)
                    {
                        int offset = 0;
                        while ((offset = command.IndexOf(marker, offset, StringComparison.Ordinal)) >= 0)
                        {
                            positions.Add(offset - 1);
                            offset += marker.Length;
                        }
                    }

                    positions.Sort();
                    results.Add(new(command, [.. positions]));
                    return;
                }

                if (values[index].Texts.IsEmpty)
                {
                    failed = true;
                    return;
                }

                foreach (Text text in values[index].Texts)
                {
                    if (text.Positions.IsEmpty)
                    {
                        replacements.Add(values[index].HasConstant ? values[index].ConstantObject : text.Sql);
                        Compose(index + 1, replacements, markers);
                        replacements.RemoveAt(replacements.Count - 1);
                    }
                    else if (Mark(index, text) is { } marked)
                    {
                        replacements.Add(marked.Sql);
                        markers.AddRange(marked.Markers);
                        Compose(index + 1, replacements, markers);
                        markers.RemoveRange(markers.Count - marked.Markers.Length, marked.Markers.Length);
                        replacements.RemoveAt(replacements.Count - 1);
                    }
                    else
                    {
                        failed = true;
                        return;
                    }
                }
            }

            // Replaces each complete quoted atom in mixed text with a unique inert marker located after formatting.
            (string Sql, string[] Markers)? Mark(int index, Text text)
            {
                System.Text.StringBuilder marked = new();
                List<string> created = [];
                int cursor = 0;
                for (int ordinal = 0; ordinal < text.Positions.Length; ordinal++)
                {
                    int position = text.Positions[ordinal];
                    if (position < cursor || position >= text.Sql.Length || text.Sql[position] is not ('\'' or '"'))
                    {
                        return null;
                    }

                    char quote = text.Sql[position];
                    int end = text.Sql.IndexOf(quote, position + 1);
                    if (end < 0)
                    {
                        return null;
                    }

                    string prefix = "$999999999999999999" + index.ToString("D10", CultureInfo.InvariantCulture) +
                        ordinal.ToString("D10", CultureInfo.InvariantCulture);
                    string marker = prefix;
                    for (int nines = 1; format.IndexOf(marker, StringComparison.Ordinal) >= 0 ||
                        values.Any(value => value.Texts.Any(candidate => candidate.Sql.IndexOf(marker, StringComparison.Ordinal) >= 0)); nines++)
                    {
                        marker = prefix + new string('9', nines);
                    }

                    _ = marked.Append(text.Sql, cursor, position - cursor).Append(quote).Append(marker).Append(quote);
                    created.Add(marker);
                    cursor = end + 1;
                }

                _ = marked.Append(text.Sql, cursor, text.Sql.Length - cursor);
                return (marked.ToString(), [.. created]);
            }
        }

        private bool IsBuilder(INamedTypeSymbol type)
            => type.ToDisplayString() == "System.Text.StringBuilder" && SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, core);

        private static Value Slice(Value text, List<(IArgumentOperation Argument, Value Value)> arguments)
        {
            Value? start = arguments.FirstOrDefault(static pair => pair.Argument.Parameter?.Name == "startIndex").Value;
            Value? length = arguments.FirstOrDefault(static pair => pair.Argument.Parameter?.Name == "length").Value;
            if (length is { HasConstant: true, ConstantObject: 0 })
            {
                return Value.Constant(string.Empty);
            }

            if (text.Unknown || text.Quoted || text.Unsafe || start is not { HasConstant: true, ConstantObject: int offset } ||
                length is not { HasConstant: true, ConstantObject: int count })
            {
                return Value.Unspecified(text);
            }

            return Value.Known(text.Texts.Where(value => offset >= 0 && count >= 0 && offset <= value.Sql.Length && count <= value.Sql.Length - offset)
                .Select(value => new Text(value.Sql.Substring(offset, count), [])));
        }

        private static bool RequiresDiagnostic(Value value)
            => value.Unsafe || value.Unknown && value.Quoted && !value.SafeSummary || value.Texts.Any(static text =>
                SpiSqlTokenLayout.Check(text.Sql, text.Positions, allowLiteralParameters: true, quotedFragments: true) != SqlInterpolationFailure.None);

        private static Value ReadBuilders(Value references, FlowState state)
            => references.References.Select(id => state.Builders.TryGetValue(id, out Value? value) ? value : Value.Raw())
                .Aggregate<Value, Value?>(null, static (current, value) => current is null ? value : Value.Merge(current, value)) ?? Value.Raw();

        private void Assign(IOperation target, Value value, FlowState state)
        {
            switch (target)
            {
                case ILocalReferenceOperation local:
                    WriteLocal(state, local.Local, value, strong: true);
                    break;
                case IParameterReferenceOperation parameter:
                    WriteLocal(state, parameter.Parameter, value, strong: true);
                    break;
                case IFlowCaptureReferenceOperation capture:
                    state.Captures[capture.Id] = value;
                    bool reference = _referenceCaptures.Contains(capture.Id);
                    if (state.CaptureTargets.TryGetValue(capture.Id, out ImmutableArray<ISymbol> targets))
                    {
                        foreach (ISymbol symbol in targets)
                        {
                            WriteLocal(state, symbol, value, targets.Length == 1 && !reference);
                        }
                    }

                    if (reference)
                    {
                        WriteExposed(state, value);
                    }

                    break;
                case IDeclarationExpressionOperation declaration:
                    Assign(declaration.Expression, value, state);
                    break;
                case ITupleOperation tuple:
                    bool known = !value.Elements.IsDefault && tuple.Elements.Length == value.Elements.Length;
                    for (int index = 0; index < tuple.Elements.Length; index++)
                    {
                        // A user Deconstruct method or opaque tuple can supply any element value.
                        Assign(tuple.Elements[index], known ? value.Elements[index] : Value.Unspecified(value), state);
                    }

                    break;
                case IArrayElementReferenceOperation element:
                    Value array = Evaluate(element.ArrayReference, state);
                    Value position = element.Indices.Length == 1 ? Evaluate(element.Indices[0], state) : Value.Raw();
                    foreach (int site in array.References)
                    {
                        Value previous = state.Builders.TryGetValue(site, out Value? stored) ? stored : Value.Raw();
                        Value updated = !previous.Elements.IsDefault && position is { HasConstant: true, ConstantObject: int offset } &&
                            offset >= 0 && offset < previous.Elements.Length
                            ? Value.Array(previous.Elements.SetItem(offset, value)) : Value.Unspecified(previous);
                        WriteObject(state, site, array.References.Length == 1 && !state.Summaries.Contains(site)
                            ? updated : Value.Merge(previous, updated));
                    }

                    break;
                case IPropertyReferenceOperation { Instance: { } instance } property when IsBuilder(property.Property.ContainingType) &&
                    (property.Property.Name == "Length" || property.Property.IsIndexer):
                    Value builder = Evaluate(instance, state);
                    foreach (int site in builder.References)
                    {
                        Value previous = state.Builders.TryGetValue(site, out Value? stored) ? stored : Value.Raw();
                        Value updated = property.Property.Name == "Length" && value is { HasConstant: true, ConstantObject: 0 }
                            ? Value.Constant(string.Empty)
                            : property.Property.IsIndexer && !value.IsLiteral ? Join(previous, Value.Raw()) : Value.Unspecified(previous);
                        WriteObject(state, site, builder.References.Length == 1 && !state.Summaries.Contains(site)
                            ? updated : Value.Merge(previous, updated));
                    }

                    break;
                case IInvocationOperation { TargetMethod.ReturnsByRef: true } invocation:
                    _ = Evaluate(invocation, state);
                    WriteExposed(state, value);
                    break;
                case IPropertyReferenceOperation { Property.ReturnsByRef: true } property:
                    _ = Evaluate(property, state);
                    WriteExposed(state, value);
                    break;
                case IFieldReferenceOperation { Field.RefKind: not RefKind.None }:
                    WriteExposed(state, value);
                    break;
            }
        }

        private static IEnumerable<ControlFlowBranch> Successors(BasicBlock block)
        {
            if (block.FallThroughSuccessor is { } falling)
            {
                yield return falling;
            }

            if (block.ConditionalSuccessor is { } conditional)
            {
                yield return conditional;
            }
        }

        private static Value Join(Value left, Value right)
        {
            bool unknown = left.Unknown || right.Unknown || !left.References.IsEmpty || !right.References.IsEmpty ||
                left.Texts.IsEmpty && !left.SafeSummary || right.Texts.IsEmpty && !right.SafeSummary;
            bool unsafeText = left.Unsafe || right.Unsafe || left.Input || right.Input;
            if (unknown)
            {
                if (!unsafeText && left.IsCompleteSafe && right.IsCompleteSafe &&
                    (!left.MayBeNonEmpty || !right.MayBeNonEmpty || left.EndsWithBoundary || right.StartsWithBoundary))
                {
                    return Value.SafeConcatenation(left, right);
                }

                return new([], unsafeText, true, left.Quoted || right.Quoted, false, []);
            }

            ImmutableArray<Text> layouts = [.. left.Texts.SelectMany(first => right.Texts.Select(second =>
                new Text(first.Sql + second.Sql, [.. first.Positions, .. second.Positions.Select(position => position + first.Sql.Length)])))
                .Take(Value.MaxLayouts + 1)];
            if (layouts.Length > Value.MaxLayouts && !unsafeText && left.IsCompleteSafe && right.IsCompleteSafe &&
                (!left.MayBeNonEmpty || !right.MayBeNonEmpty || left.EndsWithBoundary || right.StartsWithBoundary))
            {
                return Value.SafeConcatenation(left, right);
            }

            return new(layouts, unsafeText, false, left.Quoted || right.Quoted, false, []);
        }

        /// <summary>
        /// Keeps inert SQL text and semantic positions of complete PostgreSQL-quoted fragments together.
        /// </summary>
        /// <param name="Sql">The literal text with inert complete-fragment surrogates.</param>
        /// <param name="Positions">The opening positions of those complete fragments.</param>
        private sealed record Text(string Sql, ImmutableArray<int> Positions);

        /// <summary>
        /// Observes framework formatting of inert values without executing any consumer formatter.
        /// </summary>
        private sealed class FormatUse(int index, HashSet<int> used) : IFormattable
        {
            /// <summary>
            /// Records the selected argument and returns inert text for syntax validation.
            /// </summary>
            /// <param name="format">The framework's requested format.</param>
            /// <param name="formatProvider">The framework's invariant provider.</param>
            /// <returns>An empty surrogate string.</returns>
            public string ToString(string? format, IFormatProvider? formatProvider)
            {
                used.Add(index);
                return string.Empty;
            }
        }

        /// <summary>
        /// Describes possible text layouts, external input and identities in the bounded abstract heap.
        /// </summary>
        /// <param name="texts">The exact known literal/quoted layouts.</param>
        /// <param name="unsafeText">Whether a path formats unquoted runtime data.</param>
        /// <param name="unknown">Whether complete text or placement is unresolved.</param>
        /// <param name="quoted">Whether complete quoting results must retain their token boundaries.</param>
        /// <param name="input">Whether an external value has not been quoted or bound.</param>
        /// <param name="references">The possible builder or array heap identities.</param>
        /// <param name="elements">The known tuple or array element values.</param>
        /// <param name="functions">The possible local callback identities.</param>
        /// <param name="safeSummary">Whether every summarized layout places complete quoted atoms outside other SQL tokens.</param>
        /// <param name="startsWithBoundary">Whether every summarized nonempty layout starts at a safe concatenation boundary.</param>
        /// <param name="endsWithBoundary">Whether every summarized nonempty layout ends at a safe concatenation boundary.</param>
        /// <param name="nonEmpty">Whether every summarized layout contains at least one character.</param>
        /// <param name="mayBeNonEmpty">Whether at least one summarized layout contains a character.</param>
        private sealed class Value(ImmutableArray<Text> texts, bool unsafeText, bool unknown, bool quoted, bool input, ImmutableArray<int> references,
            ImmutableArray<Value> elements = default, ImmutableArray<int> functions = default, bool safeSummary = false,
            bool startsWithBoundary = false, bool endsWithBoundary = false, bool nonEmpty = false, bool mayBeNonEmpty = false)
        {
            /// <summary>
            /// Bounds exact alternatives before preserving a conservative unknown layout.
            /// </summary>
            internal const int MaxLayouts = 128;
            private readonly ImmutableArray<Text> _layouts = [.. texts.GroupBy(static text => TextKey(text), StringComparer.Ordinal)
                .Select(static group => group.First()).Take(MaxLayouts + 1)];
            private string? _key;
            /// <summary>
            /// Gets the exact alternatives when their bounded representation is retained.
            /// </summary>
            internal ImmutableArray<Text> Texts => _layouts.Length <= MaxLayouts ? _layouts : [];
            /// <summary>
            /// Gets whether a reaching path formats unquoted external data.
            /// </summary>
            internal bool Unsafe { get; } = unsafeText;
            /// <summary>
            /// Gets whether complete text or token placement remains unresolved.
            /// </summary>
            internal bool Unknown => unknown || _layouts.Length > MaxLayouts;
            /// <summary>
            /// Gets whether the value contains complete PostgreSQL-quoted fragments.
            /// </summary>
            internal bool Quoted { get; } = quoted;
            /// <summary>
            /// Gets whether every summarized layout keeps complete quoted atoms outside other SQL tokens.
            /// </summary>
            internal bool SafeSummary { get; } = safeSummary;
            /// <summary>
            /// Gets whether the value is an unbound, unquoted external input.
            /// </summary>
            internal bool Input { get; } = input;
            /// <summary>
            /// Gets the possible identities of mutable builders or arrays.
            /// </summary>
            internal ImmutableArray<int> References { get; } = [.. references.Distinct().OrderBy(static item => item)];
            /// <summary>
            /// Gets the tuple or array contents, or a default array for unresolved contents.
            /// </summary>
            internal ImmutableArray<Value> Elements { get; } = elements;
            /// <summary>
            /// Gets the possible local callback targets.
            /// </summary>
            internal ImmutableArray<int> Functions { get; } = functions.IsDefault ? [] : [.. functions.Distinct().OrderBy(static site => site)];
            /// <summary>
            /// Gets whether every possible text has closed token contexts and safe quoted-atom placement.
            /// </summary>
            internal bool IsCompleteSafe => !Unsafe && !Input && References.IsEmpty && Elements.IsDefault && Functions.IsEmpty &&
                (SafeSummary || !Unknown && Texts.Length != 0 && Texts.All(static text =>
                    SpiSqlTokenLayout.HasCompleteQuotedLayout(text.Sql, text.Positions)));
            /// <summary>
            /// Gets whether every possible text is exactly known literal text without external input or quoted fragments.
            /// </summary>
            internal bool IsLiteral => !Unknown && !Unsafe && !Input && References.IsEmpty && Elements.IsDefault && Functions.IsEmpty &&
                Texts.Length != 0 && Texts.All(static text => text.Positions.IsEmpty);
            /// <summary>
            /// Gets whether every nonempty layout begins with a character that cannot continue a preceding SQL token.
            /// </summary>
            internal bool StartsWithBoundary => SafeSummary ? startsWithBoundary : !Unknown && Texts.Length != 0 &&
                Texts.All(static text => text.Sql.Length == 0 || IsStartBoundary(text.Sql[0]));
            /// <summary>
            /// Gets whether every nonempty layout ends with a character that cannot begin a later compound SQL token.
            /// </summary>
            internal bool EndsWithBoundary => SafeSummary ? endsWithBoundary : !Unknown && Texts.Length != 0 &&
                Texts.All(static text => text.Sql.Length == 0 || IsEndBoundary(text.Sql[text.Sql.Length - 1]));
            /// <summary>
            /// Gets whether every possible layout contains at least one character.
            /// </summary>
            internal bool NonEmpty => SafeSummary ? nonEmpty : !Unknown && Texts.Length != 0 &&
                Texts.All(static text => text.Sql.Length != 0);
            /// <summary>
            /// Gets whether at least one possible layout contains a character.
            /// </summary>
            internal bool MayBeNonEmpty => SafeSummary ? mayBeNonEmpty : !Unknown && Texts.Any(static text => text.Sql.Length != 0);
            /// <summary>
            /// Gets whether the exact compiler constant is retained for inert framework formatting.
            /// </summary>
            internal bool HasConstant
            {
                get;
                private init;
            }

            /// <summary>
            /// Gets the inert compiler constant without invoking any consumer conversion.
            /// </summary>
            internal object? ConstantObject
            {
                get;
                private init;
            }

            /// <summary>
            /// Gets the deterministic comparison representation used by the flow worklist.
            /// </summary>
            internal string Key => _key ??= string.Join("|", Texts.Select(static text => TextKey(text)).OrderBy(static item => item, StringComparer.Ordinal)) +
                $"\0{Unsafe},{Unknown},{Quoted},{SafeSummary},{StartsWithBoundary},{EndsWithBoundary},{NonEmpty},{MayBeNonEmpty},{Input}\0" + string.Join(",", References) +
                (Elements.IsDefault ? "" : string.Join(";", Elements.Select(static value => value.Key.Length + ":" + value.Key))) + "\0" + string.Join(",", Functions);

            private static string TextKey(Text text) => text.Sql.Length + ":" + text.Sql + ":" + string.Join(",", text.Positions);

            private static bool IsStartBoundary(char value)
                => char.IsWhiteSpace(value) || value is ';' or ',' or '(' or ')' or '[' or ']' or '=' or '+' or '<' or '>' or '!' or '~' or '^' or '%' or '?' or ':';

            private static bool IsEndBoundary(char value)
                => IsStartBoundary(value);

            /// <summary>
            /// Preserves an inert compiler constant and its exact formatting type.
            /// </summary>
            internal static Value Constant(object? value) => new([new(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty, [])], false, false, false, false, [])
            {
                HasConstant = true,
                ConstantObject = value,
            };

            /// <summary>
            /// Represents an external value that has not been quoted or bound.
            /// </summary>
            internal static Value Raw() => new([], false, true, false, true, []);
            /// <summary>
            /// Represents a compiler constant whose formatted text is not modeled.
            /// </summary>
            internal static Value UnknownConstant() => new([], false, true, false, false, []);
            /// <summary>
            /// Identifies one recent object in the abstract heap.
            /// </summary>
            internal static Value Builder(int site) => new([], false, false, false, false, [site]);
            /// <summary>
            /// Preserves known tuple or array elements independently of their formatting.
            /// </summary>
            internal static Value Array(ImmutableArray<Value> values) => new([], false, false, false, false, [], values);
            /// <summary>
            /// Identifies a local function or lambda without invoking consumer code.
            /// </summary>
            internal static Value Function(int site) => new([], false, false, false, false, [], functions: [site]);
            /// <summary>
            /// Marks an inert complete SQL atom produced by an actual PostgreSQL quoting method.
            /// </summary>
            internal static Value Quote(char quote) => new([new(quote + "__ankus_fragment__" + quote, [0])], false, false, true, false, []);
            /// <summary>
            /// Keeps bounded exact text alternatives and their complete-fragment requirements.
            /// </summary>
            internal static Value Known(IEnumerable<Text> values)
            {
                ImmutableArray<Text> bounded = [.. values.Take(MaxLayouts + 1)];
                return new(bounded, false, false, bounded.Any(static value => !value.Positions.IsEmpty), false, []);
            }

            /// <summary>
            /// Summarizes alternatives whose complete quoted-fragment layouts were independently verified.
            /// </summary>
            internal static Value SafeChoice(Value left, Value right)
                => new([], false, true, left.Quoted || right.Quoted, false, [], safeSummary: true,
                    startsWithBoundary: left.StartsWithBoundary && right.StartsWithBoundary,
                    endsWithBoundary: left.EndsWithBoundary && right.EndsWithBoundary,
                    nonEmpty: left.NonEmpty && right.NonEmpty,
                    mayBeNonEmpty: left.MayBeNonEmpty || right.MayBeNonEmpty);

            /// <summary>
            /// Summarizes a verified concatenation without enumerating every possible layout.
            /// </summary>
            internal static Value SafeConcatenation(Value left, Value right)
                => new([], false, true, left.Quoted || right.Quoted, false, [], safeSummary: true,
                    startsWithBoundary: left.StartsWithBoundary && (left.NonEmpty || right.StartsWithBoundary),
                    endsWithBoundary: right.EndsWithBoundary && (right.NonEmpty || left.EndsWithBoundary),
                    nonEmpty: left.NonEmpty || right.NonEmpty,
                    mayBeNonEmpty: left.MayBeNonEmpty || right.MayBeNonEmpty);

            /// <summary>
            /// Retains known hazards and identities after an unmodeled transformation.
            /// </summary>
            internal static Value Unspecified(Value previous) => new([], previous.Unsafe, true, previous.Quoted, true, previous.References);
            /// <summary>
            /// Marks a formatted unquoted value without discarding its other flow properties.
            /// </summary>
            internal Value WithUnsafe() => new(Texts, true, Unknown, Quoted, Input, References, Elements, Functions);
            /// <summary>
            /// Retains the non-object alternatives of a value whose object identities are formatted separately.
            /// </summary>
            internal Value WithoutReferences() => new(Texts, Unsafe, Unknown, Quoted, Input, [], Elements, Functions,
                SafeSummary, StartsWithBoundary, EndsWithBoundary, NonEmpty, MayBeNonEmpty);
            /// <summary>
            /// Converges changing loop text while retaining hazards and object identities.
            /// </summary>
            internal Value Widen(Value previous)
                => IsCompleteSafe && previous.IsCompleteSafe ? SafeChoice(this, previous) :
                    new([], Unsafe || previous.Unsafe, true, Quoted || previous.Quoted, Input || previous.Input,
                        [.. References, .. previous.References], functions: [.. Functions, .. previous.Functions]);
            /// <summary>
            /// Redirects aliases when a prior recent object moves into an allocation summary.
            /// </summary>
            internal Value ReplaceReference(int from, int to) => new(Texts, Unsafe, Unknown, Quoted, Input,
                [.. References.Select(reference => reference == from ? to : reference)],
                Elements.IsDefault ? default : [.. Elements.Select(value => value.ReplaceReference(from, to))], Functions,
                SafeSummary, StartsWithBoundary, EndsWithBoundary, NonEmpty, MayBeNonEmpty)
            {
                HasConstant = HasConstant,
                ConstantObject = ConstantObject,
            };

            /// <summary>
            /// Unites alternative reaching values without treating one branch as certain.
            /// </summary>
            internal static Value Merge(Value left, Value right)
            {
                if (left.Key == right.Key)
                {
                    return left;
                }

                ImmutableArray<Text> layouts = left.Unknown || right.Unknown ? [] : [.. left.Texts.Concat(right.Texts).Take(MaxLayouts + 1)];
                if ((left.Unknown || right.Unknown || layouts.Length > MaxLayouts) && left.IsCompleteSafe && right.IsCompleteSafe)
                {
                    return SafeChoice(left, right);
                }

                return new(layouts, left.Unsafe || right.Unsafe,
                    left.Unknown || right.Unknown, left.Quoted || right.Quoted, left.Input || right.Input, [.. left.References, .. right.References],
                    !left.Elements.IsDefault && !right.Elements.IsDefault && left.Elements.Length == right.Elements.Length
                        ? [.. left.Elements.Zip(right.Elements, static (first, second) => Merge(first, second))] : default,
                    [.. left.Functions.Concat(right.Functions).Distinct().OrderBy(static site => site)]);
            }
        }

        /// <summary>
        /// Keeps reaching variables, compiler captures, alias targets and mutable heap contents together.
        /// </summary>
        private sealed class FlowState
        {
            /// <summary>
            /// Gets reaching values of parameters and local variables using semantic symbol identity.
            /// </summary>
            internal Dictionary<ISymbol, Value> Locals { get; } = new(SymbolEqualityComparer.Default);
            /// <summary>
            /// Gets the values of compiler-created control-flow captures.
            /// </summary>
            internal Dictionary<CaptureId, Value> Captures { get; } = [];
            /// <summary>
            /// Gets the current contents of mutable builder and format-argument array objects.
            /// </summary>
            internal Dictionary<int, Value> Builders { get; } = [];
            /// <summary>
            /// Gets possible referents of scalar reference aliases.
            /// </summary>
            internal Dictionary<ISymbol, ImmutableArray<ISymbol>> Aliases { get; } = new(SymbolEqualityComparer.Default);
            /// <summary>
            /// Gets scalar assignment targets retained by lowered lvalue captures.
            /// </summary>
            internal Dictionary<CaptureId, ImmutableArray<ISymbol>> CaptureTargets { get; } = [];
            /// <summary>
            /// Gets allocation identities that can represent several older objects and require weak updates.
            /// </summary>
            internal HashSet<int> Summaries { get; } = [];
            /// <summary>
            /// Gets callbacks created on a reaching path, whose later invocations can observe subsequent writes.
            /// </summary>
            internal HashSet<int> Created { get; } = [];
            /// <summary>
            /// Gets reference locals that may be bound to storage other than tracked locals.
            /// </summary>
            internal HashSet<ISymbol> Unresolved { get; } = new(SymbolEqualityComparer.Default);
            /// <summary>
            /// Gets or sets the value produced by a reachable return branch.
            /// </summary>
            internal Value? Returned
            {
                get;
                set;
            }

            /// <summary>
            /// Applies possible call effects without replacing the caller's pending return value.
            /// </summary>
            internal void Adopt(FlowState other)
            {
                foreach (KeyValuePair<ISymbol, Value> pair in other.Locals)
                {
                    Locals[pair.Key] = pair.Value;
                }

                foreach (KeyValuePair<int, Value> pair in other.Builders)
                {
                    Builders[pair.Key] = pair.Value;
                }

                Summaries.UnionWith(other.Summaries);
                Created.UnionWith(other.Created);
            }

            /// <summary>
            /// Creates a recent heap object while preserving aliases to older objects from the same site.
            /// </summary>
            internal Value Allocate(int site, Value content)
            {
                if (Builders.TryGetValue(site, out Value? previous))
                {
                    int summary = site + 1;
                    Summaries.Add(summary);
                    Builders[summary] = Builders.TryGetValue(summary, out Value? older) ? Value.Merge(older, previous) : previous;
                    foreach (ISymbol symbol in Locals.Keys.ToArray())
                    {
                        Locals[symbol] = Locals[symbol].ReplaceReference(site, summary);
                    }

                    foreach (CaptureId capture in Captures.Keys.ToArray())
                    {
                        Captures[capture] = Captures[capture].ReplaceReference(site, summary);
                    }

                    foreach (int existingSite in Builders.Keys.ToArray())
                    {
                        Builders[existingSite] = Builders[existingSite].ReplaceReference(site, summary);
                    }
                }

                Builders[site] = content;
                return Value.Builder(site);
            }

            /// <summary>
            /// Creates independent reaching maps for one branch or call interpretation.
            /// </summary>
            internal FlowState Clone()
            {
                FlowState copy = new();
                Copy(Locals, copy.Locals);
                Copy(Captures, copy.Captures);
                Copy(Builders, copy.Builders);
                copy.Summaries.UnionWith(Summaries);
                copy.Created.UnionWith(Created);
                copy.Unresolved.UnionWith(Unresolved);
                copy.Returned = Returned;
                foreach (KeyValuePair<ISymbol, ImmutableArray<ISymbol>> pair in Aliases)
                {
                    copy.Aliases.Add(pair.Key, pair.Value);
                }

                foreach (KeyValuePair<CaptureId, ImmutableArray<ISymbol>> pair in CaptureTargets)
                {
                    copy.CaptureTargets.Add(pair.Key, pair.Value);
                }

                return copy;
            }

            /// <summary>
            /// Combines possible branch states, aliases, return values and heap summaries.
            /// </summary>
            internal static FlowState Merge(FlowState left, FlowState right)
            {
                FlowState merged = left.Clone();
                MergeLocals(right.Locals, merged.Locals);
                MergeMap(right.Captures, merged.Captures);
                MergeMap(right.Builders, merged.Builders);
                merged.Summaries.UnionWith(right.Summaries);
                merged.Created.UnionWith(right.Created);
                merged.Unresolved.UnionWith(right.Unresolved);
                merged.Returned = merged.Returned is { } prior && right.Returned is { } returned ? Value.Merge(prior, returned) : merged.Returned ?? right.Returned;
                foreach (KeyValuePair<ISymbol, ImmutableArray<ISymbol>> pair in right.Aliases)
                {
                    merged.Aliases[pair.Key] = merged.Aliases.TryGetValue(pair.Key, out ImmutableArray<ISymbol> old)
                        ? [.. old.Concat(pair.Value).Distinct(SymbolEqualityComparer.Default)] : pair.Value;
                }

                foreach (KeyValuePair<CaptureId, ImmutableArray<ISymbol>> pair in right.CaptureTargets)
                {
                    merged.CaptureTargets[pair.Key] = merged.CaptureTargets.TryGetValue(pair.Key, out ImmutableArray<ISymbol> old)
                        ? [.. old.Concat(pair.Value).Distinct(SymbolEqualityComparer.Default)] : pair.Value;
                }

                return merged;
            }

            /// <summary>
            /// Converges changing values on a back edge without discarding known unsafe paths.
            /// </summary>
            internal void Widen(FlowState previous)
            {
                WidenMap(Locals, previous.Locals);
                WidenMap(Captures, previous.Captures);
                WidenMap(Builders, previous.Builders);
                if (Returned is { } value && previous.Returned is { } old && value.Key != old.Key)
                {
                    Returned = value.Widen(old);
                }
            }

            /// <summary>
            /// Tests equality of every component that can affect subsequent commands.
            /// </summary>
            internal bool Equivalent(FlowState other)
                => Same(Locals, other.Locals) && Same(Captures, other.Captures) && Same(Builders, other.Builders) &&
                    Returned?.Key == other.Returned?.Key &&
                    Summaries.SetEquals(other.Summaries) && Created.SetEquals(other.Created) && Unresolved.SetEquals(other.Unresolved) &&
                    Aliases.Count == other.Aliases.Count && Aliases.All(pair => other.Aliases.TryGetValue(pair.Key, out ImmutableArray<ISymbol> aliases) &&
                        pair.Value.Length == aliases.Length && pair.Value.All(item => aliases.Contains(item, SymbolEqualityComparer.Default))) &&
                    CaptureTargets.Count == other.CaptureTargets.Count && CaptureTargets.All(pair => other.CaptureTargets.TryGetValue(pair.Key, out ImmutableArray<ISymbol> targets) &&
                        pair.Value.Length == targets.Length && pair.Value.All(item => targets.Contains(item, SymbolEqualityComparer.Default)));

            private static void Copy<TKey>(Dictionary<TKey, Value> source, Dictionary<TKey, Value> target) where TKey : notnull
            {
                foreach (KeyValuePair<TKey, Value> pair in source)
                {
                    target.Add(pair.Key, pair.Value);
                }
            }

            /// <summary>
            /// Unites variable values so a path that never wrote a local or parameter keeps its unknown external value.
            /// </summary>
            /// <param name="source">The other path's variables.</param>
            /// <param name="target">The merged variables, initially the first path's variables.</param>
            private static void MergeLocals(Dictionary<ISymbol, Value> source, Dictionary<ISymbol, Value> target)
            {
                foreach (ISymbol symbol in target.Keys.ToArray().Where(symbol => !source.ContainsKey(symbol)))
                {
                    target[symbol] = Value.Merge(target[symbol], Value.Raw());
                }

                foreach (KeyValuePair<ISymbol, Value> pair in source)
                {
                    target[pair.Key] = Value.Merge(target.TryGetValue(pair.Key, out Value? previous) ? previous : Value.Raw(), pair.Value);
                }
            }

            private static void MergeMap<TKey>(Dictionary<TKey, Value> source, Dictionary<TKey, Value> target) where TKey : notnull
            {
                foreach (KeyValuePair<TKey, Value> pair in source)
                {
                    target[pair.Key] = target.TryGetValue(pair.Key, out Value? previous) ? Value.Merge(previous, pair.Value) : pair.Value;
                }
            }

            private static void WidenMap<TKey>(Dictionary<TKey, Value> current, Dictionary<TKey, Value> previous) where TKey : notnull
            {
                foreach (KeyValuePair<TKey, Value> pair in current.ToArray())
                {
                    if (!(previous.TryGetValue(pair.Key, out Value? old) && pair.Value.Key != old.Key))
                    {
                        continue;
                    }

                    current[pair.Key] = pair.Value.Widen(old);
                }
            }

            private static bool Same<TKey>(Dictionary<TKey, Value> left, Dictionary<TKey, Value> right) where TKey : notnull
                => left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out Value? value) && pair.Value.Key == value.Key);
        }
    }
}
