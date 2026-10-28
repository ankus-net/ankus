using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Ankus.Generators;

public sealed partial class SpiInterpolationAnalyzer
{
    private sealed partial class SqlFlowAnalysis
    {
        private readonly HashSet<ILocalSymbol> _counters = new(SymbolEqualityComparer.Default);

        /// <summary>
        /// Finds the locals that tell a loop's first pass from later ones: <see langword="int"/> and <see langword="long"/>
        /// locals that are only set to constants and incremented by positive constants, and <see langword="bool"/>
        /// locals that are only set to constants.
        /// </summary>
        /// <param name="root">The complete operation tree, including nested callbacks.</param>
        private void FindCounters(IOperation root)
        {
            HashSet<ILocalSymbol> rejected = new(SymbolEqualityComparer.Default);
            Dictionary<ILocalSymbol, IOperation?> owners = new(SymbolEqualityComparer.Default);
            List<ILocalReferenceOperation> references = [];
            foreach (IOperation operation in Descendants(root))
            {
                if (operation is IVariableDeclaratorOperation declarator && IsCounterType(declarator.Symbol.Type))
                {
                    owners[declarator.Symbol] = Owner(declarator);
                    if (declarator.Symbol.RefKind != RefKind.None ||
                        declarator.GetVariableInitializer() is { } initializer && CounterConstant(initializer.Value) is null)
                    {
                        rejected.Add(declarator.Symbol);
                    }
                }
                else if (operation is ILocalReferenceOperation reference && IsCounterType(reference.Local.Type))
                {
                    references.Add(reference);
                }
            }

            foreach (ILocalReferenceOperation reference in references)
            {
                bool integer = reference.Local.Type.SpecialType != SpecialType.System_Boolean;
                bool allowed = reference.Parent switch
                {
                    ISimpleAssignmentOperation assignment when ReferenceEquals(assignment.Target, reference) => CounterConstant(assignment.Value) is not null,
                    IIncrementOrDecrementOperation step => integer && step.Kind == OperationKind.Increment,
                    ICompoundAssignmentOperation compound when ReferenceEquals(compound.Target, reference) => integer &&
                        compound.OperatorKind == BinaryOperatorKind.Add && CounterConstant(compound.Value) is long step && step > 0,
                    IDeconstructionAssignmentOperation or ITupleOperation or IDeclarationExpressionOperation => false,
                    _ => true,
                };

                // A callback could change a captured counter between a test and the code it guards.
                if (!allowed || !owners.TryGetValue(reference.Local, out IOperation? owner) || !ReferenceEquals(owner, Owner(reference)))
                {
                    rejected.Add(reference.Local);
                }
            }

            _counters.UnionWith(owners.Keys.Where(local => !rejected.Contains(local) && !_exposed.Contains(local)));

            static IOperation? Owner(IOperation operation)
            {
                for (IOperation? current = operation.Parent; current is not null; current = current.Parent)
                {
                    if (current is IAnonymousFunctionOperation or ILocalFunctionOperation)
                    {
                        return current;
                    }
                }

                return null;
            }
        }

        private static bool IsCounterType(ITypeSymbol type)
            => type.SpecialType is SpecialType.System_Int32 or SpecialType.System_Int64 or SpecialType.System_Boolean;

        /// <summary>
        /// Reads an integer or Boolean compiler constant.
        /// </summary>
        /// <param name="operation">The value.</param>
        /// <returns>The constant as a <see langword="long"/> or <see langword="bool"/>, or null for any other value.</returns>
        private static object? CounterConstant(IOperation operation)
            => operation.ConstantValue is { HasValue: true, Value: { } value } ? value switch
            {
                bool flag => flag,
                int or long or short or sbyte or byte or ushort or uint => Convert.ToInt64(value, CultureInfo.InvariantCulture),
                _ => null,
            } : null;

        /// <summary>
        /// Applies an operation's counter writes to the path split after the operation's values are evaluated.
        /// </summary>
        /// <param name="paths">The paths before the operation.</param>
        /// <param name="operation">A control-flow block operation.</param>
        /// <returns>The paths after the operation.</returns>
        private PathState Advance(PathState paths, IOperation operation)
        {
            // A constant is a reset, a step is null, and any other write stops the split.
            List<(ILocalSymbol Counter, object? Value, bool Known)> writes = [];
            foreach (IOperation descendant in Descendants(operation))
            {
                switch (descendant)
                {
                    case ISimpleAssignmentOperation { Target: ILocalReferenceOperation { Local: { } local } } assignment when _counters.Contains(local):
                        object? value = CounterConstant(assignment.Value);
                        writes.Add((local, value, value is not null));
                        break;
                    case IIncrementOrDecrementOperation { Target: ILocalReferenceOperation { Local: { } local } } step when _counters.Contains(local):
                        writes.Add((local, null, step.Kind == OperationKind.Increment));
                        break;
                    case ICompoundAssignmentOperation { Target: ILocalReferenceOperation { Local: { } local } } compound when _counters.Contains(local):
                        writes.Add((local, null, compound.OperatorKind == BinaryOperatorKind.Add && CounterConstant(compound.Value) is long amount && amount > 0));
                        break;
                }
            }

            if (writes.Count == 1 && writes[0].Known)
            {
                return writes[0].Value is { } reset ? paths.Reset(writes[0].Counter, reset) : paths.Step(writes[0].Counter);
            }

            foreach ((ILocalSymbol written, _, _) in writes)
            {
                paths = paths.Remove(written);
            }

            return paths;
        }

        /// <summary>
        /// Keeps only the paths on which a counter test takes the branch, so code that runs only on a loop's first pass
        /// sees only that pass's values.
        /// </summary>
        /// <param name="paths">The paths at the end of the block.</param>
        /// <param name="block">The block whose branch value decides the successor.</param>
        /// <param name="branch">The successor being followed.</param>
        /// <returns>The paths that can take the branch, or null when none can.</returns>
        private static PathState? Refine(PathState paths, BasicBlock block, ControlFlowBranch branch)
        {
            if (block.BranchValue is not { } condition || block.ConditionKind == ControlFlowConditionKind.None ||
                !CounterTest(condition, out ILocalSymbol counter, out Func<object, (bool? First, bool? Later)> truth))
            {
                return paths;
            }

            bool taken = ReferenceEquals(branch, block.ConditionalSuccessor);
            return paths.Refine(counter, truth, taken == (block.ConditionKind == ControlFlowConditionKind.WhenTrue));
        }

        /// <summary>
        /// Recognizes a test of a counter against a constant and describes its result on the paths where the counter
        /// still has its first value and on the paths after it advanced.
        /// </summary>
        /// <param name="condition">The branch value.</param>
        /// <param name="counter">The tested counter.</param>
        /// <param name="truth">The result for a first value: on the first paths, and on later paths when it is the same
        /// for every later value.</param>
        /// <returns>Whether the condition is such a test.</returns>
        private static bool CounterTest(IOperation condition, out ILocalSymbol counter, out Func<object, (bool? First, bool? Later)> truth)
        {
            counter = null!;
            truth = null!;
            switch (Unwrap(condition))
            {
                case ILocalReferenceOperation { Local.Type.SpecialType: SpecialType.System_Boolean } flag:
                    counter = flag.Local;
                    truth = static first => ((bool)first, !(bool)first);
                    return true;
                case IUnaryOperation { OperatorKind: UnaryOperatorKind.Not } not when Unwrap(not.Operand) is ILocalReferenceOperation { Local.Type.SpecialType: SpecialType.System_Boolean } flag:
                    counter = flag.Local;
                    truth = static first => (!(bool)first, (bool)first);
                    return true;
                case IBinaryOperation binary:
                    bool reversed = Unwrap(binary.LeftOperand) is not ILocalReferenceOperation;
                    if ((reversed ? Unwrap(binary.RightOperand) : Unwrap(binary.LeftOperand)) is not ILocalReferenceOperation { Local: { } local } ||
                        CounterConstant(reversed ? binary.LeftOperand : binary.RightOperand) is not { } constant)
                    {
                        return false;
                    }

                    BinaryOperatorKind kind = reversed ? binary.OperatorKind switch
                    {
                        BinaryOperatorKind.LessThan => BinaryOperatorKind.GreaterThan,
                        BinaryOperatorKind.LessThanOrEqual => BinaryOperatorKind.GreaterThanOrEqual,
                        BinaryOperatorKind.GreaterThan => BinaryOperatorKind.LessThan,
                        BinaryOperatorKind.GreaterThanOrEqual => BinaryOperatorKind.LessThanOrEqual,
                        _ => binary.OperatorKind,
                    } : binary.OperatorKind;
                    if (local.Type.SpecialType == SpecialType.System_Boolean)
                    {
                        if (constant is not bool expected || kind is not (BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals))
                        {
                            return false;
                        }

                        bool equals = kind == BinaryOperatorKind.Equals;
                        counter = local;
                        truth = first => (((bool)first == expected) == equals, ((bool)first != expected) == equals);
                        return true;
                    }

                    if (constant is not long k || kind is not (BinaryOperatorKind.GreaterThan or BinaryOperatorKind.GreaterThanOrEqual or
                        BinaryOperatorKind.LessThan or BinaryOperatorKind.LessThanOrEqual or BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals))
                    {
                        return false;
                    }

                    counter = local;
                    truth = first => Compare((long)first, kind, k);
                    return true;
                default:
                    return false;
            }

            static IOperation Unwrap(IOperation operation)
            {
                while (operation is IConversionOperation { IsImplicit: true } conversion)
                {
                    operation = conversion.Operand;
                }

                return operation;
            }

            // Later values are greater than the first, since the counter is only reset to a constant and incremented. An
            // int would need 2^31 increments to wrap.
            static (bool? First, bool? Later) Compare(long first, BinaryOperatorKind kind, long k)
            {
                bool atMostFirst = k <= first;
                bool atMostNext = atMostFirst || k - 1 == first;
                return kind switch
                {
                    BinaryOperatorKind.GreaterThan => (first > k, atMostFirst ? true : null),
                    BinaryOperatorKind.GreaterThanOrEqual => (first >= k, atMostNext ? true : null),
                    BinaryOperatorKind.LessThan => (first < k, atMostNext ? false : null),
                    BinaryOperatorKind.LessThanOrEqual => (first <= k, atMostFirst ? false : null),
                    BinaryOperatorKind.Equals => (first == k, atMostFirst ? false : null),
                    _ => (first != k, atMostFirst ? true : null),
                };
            }
        }

        /// <summary>
        /// Separates the paths on which a counter still has the value it was last set to from the paths on which it has
        /// advanced, so a test such as <c>i &gt; 0</c> keeps a loop's first pass apart from the passes after it.
        /// </summary>
        /// <remarks>
        /// A split for one counter can hold splits for others. Paths that reach a split without its counter join both
        /// sides, so a later test never discards them.
        /// </remarks>
        private sealed class PathState
        {
            private const int MaxDepth = 3;

            private PathState(FlowState? leaf, ILocalSymbol? counter, object? initial, PathState? later, PathState? first)
            {
                Leaf = leaf;
                Counter = counter;
                Initial = initial;
                Later = later;
                First = first;
            }

            /// <summary>
            /// Gets the state of an unsplit set of paths.
            /// </summary>
            internal FlowState? Leaf { get; }

            /// <summary>
            /// Gets the counter that separates <see cref="First"/> from <see cref="Later"/>.
            /// </summary>
            internal ILocalSymbol? Counter { get; }

            /// <summary>
            /// Gets the counter's first value, a <see langword="long"/> or <see langword="bool"/>.
            /// </summary>
            internal object? Initial { get; }

            /// <summary>
            /// Gets the paths on which the counter has advanced past, or for a flag differs from, its first value.
            /// </summary>
            internal PathState? Later { get; }

            /// <summary>
            /// Gets the paths on which the counter has its first value.
            /// </summary>
            internal PathState? First { get; }

            private int Depth => Leaf is not null ? 0 : 1 + Math.Max(Later?.Depth ?? 0, First?.Depth ?? 0);

            /// <summary>
            /// Wraps one state as an unsplit set of paths.
            /// </summary>
            internal static PathState Of(FlowState state) => new(state, null, null, null, null);

            /// <summary>
            /// Enumerates the states of every separate set of paths.
            /// </summary>
            internal IEnumerable<FlowState> Leaves()
                => Leaf is not null ? [Leaf] : (Later?.Leaves() ?? []).Concat(First?.Leaves() ?? []);

            /// <summary>
            /// Merges every set of paths into one state.
            /// </summary>
            internal FlowState Collapse()
            {
                FlowState? merged = null;
                foreach (FlowState state in Leaves())
                {
                    merged = merged is null ? state.Clone() : FlowState.Merge(merged, state);
                }

                return merged!;
            }

            /// <summary>
            /// Copies every state so the copy can be evaluated without changing this value.
            /// </summary>
            internal PathState Clone()
                => Leaf is not null ? Of(Leaf.Clone()) : new(null, Counter, Initial, Later?.Clone(), First?.Clone());

            /// <summary>
            /// Records that a counter was set to a constant.
            /// </summary>
            internal PathState Reset(ILocalSymbol counter, object value)
            {
                if (Leaf is not null)
                {
                    return new(null, counter, value, null, this);
                }

                if (!SymbolEqualityComparer.Default.Equals(Counter, counter))
                {
                    return Trim(Split(Counter!, Initial!, Later?.Reset(counter, value), First?.Reset(counter, value))!);
                }

                PathState all = Merge(Later, First)!;
                return (Initial, value) switch
                {
                    (bool initial, bool flag) => initial == flag ? new(null, counter, initial, null, all) : new(null, counter, initial, all, null),
                    (long initial, long number) when number == initial => new(null, counter, initial, null, all),
                    (long initial, long number) when number > initial => new(null, counter, initial, all, null),
                    _ => new(null, counter, value, null, all),
                };
            }

            /// <summary>
            /// Records that a counter was incremented, so no path keeps its first value.
            /// </summary>
            internal PathState Step(ILocalSymbol counter)
            {
                if (Leaf is not null)
                {
                    return this;
                }

                return SymbolEqualityComparer.Default.Equals(Counter, counter)
                    ? new(null, Counter, Initial, Merge(Later, First), null)
                    : Split(Counter!, Initial!, Later?.Step(counter), First?.Step(counter))!;
            }

            /// <summary>
            /// Stops separating paths by a counter.
            /// </summary>
            internal PathState Remove(ILocalSymbol counter)
            {
                if (Leaf is not null)
                {
                    return this;
                }

                return SymbolEqualityComparer.Default.Equals(Counter, counter)
                    ? Merge(Later?.Remove(counter), First?.Remove(counter))!
                    : Split(Counter!, Initial!, Later?.Remove(counter), First?.Remove(counter))!;
            }

            /// <summary>
            /// Keeps the paths on which a counter test has the given result.
            /// </summary>
            /// <param name="counter">The tested counter.</param>
            /// <param name="truth">The test's result for a first value, on first and later paths.</param>
            /// <param name="result">The result that takes the branch.</param>
            /// <returns>The remaining paths, or null when none remain.</returns>
            internal PathState? Refine(ILocalSymbol counter, Func<object, (bool? First, bool? Later)> truth, bool result)
            {
                if (Leaf is not null)
                {
                    return this;
                }

                if (!SymbolEqualityComparer.Default.Equals(Counter, counter))
                {
                    return Split(Counter!, Initial!, Later?.Refine(counter, truth, result), First?.Refine(counter, truth, result));
                }

                (bool? first, bool? later) = truth(Initial!);
                return Split(Counter!, Initial!, later == !result ? null : Later, first == !result ? null : First);
            }

            /// <summary>
            /// Combines two sets of paths, keeping a split that only one side has by joining the other side to both of its
            /// parts.
            /// </summary>
            internal static PathState? Merge(PathState? left, PathState? right)
            {
                if (left is null || right is null)
                {
                    return left ?? right;
                }

                if (left.Leaf is not null && right.Leaf is not null)
                {
                    return Of(FlowState.Merge(left.Leaf, right.Leaf));
                }

                if (left.Leaf is null && right.Leaf is null && SymbolEqualityComparer.Default.Equals(left.Counter, right.Counter) &&
                    Equals(left.Initial, right.Initial))
                {
                    return Split(left.Counter!, left.Initial!, Merge(left.Later, right.Later), Merge(left.First, right.First));
                }

                (PathState split, PathState other) = left.Leaf is null ? (left, right) : (right, left);
                PathState joined = other.Remove(split.Counter!);
                return Trim(Split(split.Counter!, split.Initial!, Merge(split.Later, joined), Merge(split.First, joined))!);
            }

            /// <summary>
            /// Converges a back edge's paths with the paths that reached the loop before, part by part where both are
            /// split the same way.
            /// </summary>
            internal static PathState Widen(PathState current, PathState previous)
            {
                if (current.Leaf is not null && previous.Leaf is not null)
                {
                    FlowState widened = current.Leaf.Clone();
                    widened.Widen(previous.Leaf);
                    return Of(widened);
                }

                if (current.Leaf is null && previous.Leaf is null && SymbolEqualityComparer.Default.Equals(current.Counter, previous.Counter) &&
                    Equals(current.Initial, previous.Initial))
                {
                    return Split(current.Counter!, current.Initial!, Part(current.Later, previous.Later), Part(current.First, previous.First))!;
                }

                FlowState collapsed = current.Collapse();
                collapsed.Widen(previous.Collapse());
                return Of(collapsed);

                static PathState? Part(PathState? current, PathState? previous)
                    => current is null || previous is null ? current : Widen(current, previous);
            }

            /// <summary>
            /// Tests whether two sets of paths are split the same way with equivalent states.
            /// </summary>
            internal bool Equivalent(PathState other)
                => Leaf is not null
                    ? other.Leaf is not null && Leaf.Equivalent(other.Leaf)
                    : other.Leaf is null && SymbolEqualityComparer.Default.Equals(Counter, other.Counter) && Equals(Initial, other.Initial) &&
                        Same(Later, other.Later) && Same(First, other.First);

            private static bool Same(PathState? left, PathState? right)
                => left is null ? right is null : right is not null && left.Equivalent(right);

            private static PathState? Split(ILocalSymbol counter, object initial, PathState? later, PathState? first)
                => later is null && first is null ? null : new(null, counter, initial, later, first);

            // Separating paths by too many counters at once costs more than it gains, so the outermost split goes first.
            private static PathState Trim(PathState paths)
            {
                while (paths.Depth > MaxDepth)
                {
                    paths = paths.Remove(paths.Counter!);
                }

                return paths;
            }
        }
    }
}
