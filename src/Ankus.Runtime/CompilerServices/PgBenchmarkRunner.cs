using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Ankus.CompilerServices;

/// <summary>
/// Carries validated measurement settings from generated benchmark wrappers.
/// </summary>
/// <param name="sampleSize">The number of measurement samples.</param>
/// <param name="measurementTimeMilliseconds">The target total measurement time.</param>
/// <param name="warmupTimeMilliseconds">The warmup time.</param>
/// <param name="resampleCount">The statistical resample count.</param>
/// <param name="noiseThreshold">The relative noise threshold.</param>
/// <param name="significanceLevel">The comparison significance level.</param>
/// <param name="throughput">The unit of work one iteration performs, if reported.</param>
/// <param name="throughputPerIteration">The amount of work one iteration performs, or zero.</param>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class PgBenchmarkConfiguration(
    int sampleSize,
    int measurementTimeMilliseconds,
    int warmupTimeMilliseconds,
    int resampleCount,
    double noiseThreshold,
    double significanceLevel,
    PgBenchmarkThroughput throughput = PgBenchmarkThroughput.None,
    long throughputPerIteration = 0)
{
    /// <summary>
    /// Gets the number of measurement samples, which is at least ten.
    /// </summary>
    public int SampleSize { get; } = sampleSize;

    /// <summary>
    /// Gets the positive target total measurement time in milliseconds.
    /// </summary>
    public int MeasurementTimeMilliseconds { get; } = measurementTimeMilliseconds;

    /// <summary>
    /// Gets the positive warmup time in milliseconds.
    /// </summary>
    public int WarmupTimeMilliseconds { get; } = warmupTimeMilliseconds;

    /// <summary>
    /// Gets the positive statistical resample count.
    /// </summary>
    public int ResampleCount { get; } = resampleCount;

    /// <summary>
    /// Gets the finite, nonnegative relative noise threshold.
    /// </summary>
    public double NoiseThreshold { get; } = noiseThreshold;

    /// <summary>
    /// Gets the comparison significance level, between zero and one.
    /// </summary>
    public double SignificanceLevel { get; } = significanceLevel;

    /// <summary>
    /// Gets the unit of work one iteration performs, or <see cref="PgBenchmarkThroughput.None"/>.
    /// </summary>
    public PgBenchmarkThroughput Throughput { get; } = throughput;

    /// <summary>
    /// Gets the positive amount of work one iteration performs when a throughput is reported, otherwise zero.
    /// </summary>
    public long ThroughputPerIteration { get; } = throughputPerIteration;
}

/// <summary>
/// Carries one generated benchmark identity without runtime reflection.
/// </summary>
/// <param name="schemaName">The benchmark wrapper schema.</param>
/// <param name="benchmarkName">The managed benchmark name.</param>
/// <param name="functionName">The generated SQL function name.</param>
/// <param name="setupFunction">The managed setup name, or null.</param>
/// <param name="transactionMode">The measured transaction boundary.</param>
/// <param name="sourceFile">The project-relative source path.</param>
/// <param name="sourceLine">The one-based declaration line.</param>
/// <param name="configuration">The validated measurement settings.</param>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class PgBenchmarkDefinition(
    string schemaName,
    string benchmarkName,
    string functionName,
    string? setupFunction,
    PgBenchmarkTransactionMode transactionMode,
    string sourceFile,
    int sourceLine,
    PgBenchmarkConfiguration configuration)
{
    /// <summary>
    /// Gets the benchmark wrapper schema.
    /// </summary>
    public string SchemaName { get; } = schemaName;

    /// <summary>
    /// Gets the managed benchmark name.
    /// </summary>
    public string BenchmarkName { get; } = benchmarkName;

    /// <summary>
    /// Gets the generated SQL function name.
    /// </summary>
    public string FunctionName { get; } = functionName;

    /// <summary>
    /// Gets the managed setup name, or null.
    /// </summary>
    public string? SetupFunction { get; } = setupFunction;

    /// <summary>
    /// Gets the measured transaction boundary.
    /// </summary>
    public PgBenchmarkTransactionMode TransactionMode { get; } = transactionMode;

    /// <summary>
    /// Gets the project-relative source path.
    /// </summary>
    public string SourceFile { get; } = sourceFile;

    /// <summary>
    /// Gets the one-based declaration line.
    /// </summary>
    public int SourceLine { get; } = sourceLine;

    /// <summary>
    /// Gets the validated measurement settings.
    /// </summary>
    public PgBenchmarkConfiguration Configuration { get; } = configuration;
}

/// <summary>
/// Executes generated PostgreSQL benchmarks and writes explicit Native AOT-safe JSON payloads.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class PgBenchmarkRunner
{
    private const double ConfidenceLevel = 0.95;

    /// <summary>
    /// Describes one generated benchmark without running author code.
    /// </summary>
    /// <param name="definition">The generated benchmark definition.</param>
    /// <returns>The descriptor consumed by the Ankus tool.</returns>
    public static PgJsonb Describe(PgBenchmarkDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return WriteJson(writer => WriteDefinition(writer, definition));
    }

    /// <summary>
    /// Runs one generated benchmark inside the active PostgreSQL backend.
    /// </summary>
    /// <param name="definition">The generated benchmark definition.</param>
    /// <param name="setup">The optional once-per-run setup callback.</param>
    /// <param name="benchmark">The benchmark method that registers one timing loop.</param>
    /// <param name="baseline">The optional prior result used for comparison.</param>
    /// <returns>The complete measurement or failure payload.</returns>
    public static PgJsonb Run(
        PgBenchmarkDefinition definition,
        Action? setup,
        Action<PgBencher> benchmark,
        PgJsonb? baseline)
        => Run(definition, setup, benchmark, baseline,
            static action => PgTransaction.RunInSubtransaction(action),
            definition?.TransactionMode is PgBenchmarkTransactionMode.RollbackPerBatch or PgBenchmarkTransactionMode.RollbackPerIteration
                ? static action => NativeSubtransaction.RunAndRollBack(action)
                : static action => PgTransaction.RunInSubtransaction(action));

    /// <summary>
    /// Runs one benchmark through an explicit transaction boundary for direct verification.
    /// </summary>
    internal static PgJsonb Run(
        PgBenchmarkDefinition definition,
        Action? setup,
        Action<PgBencher> benchmark,
        PgJsonb? baseline,
        Action<Action> subtransaction)
        => Run(definition, setup, benchmark, baseline, static action => action(), subtransaction);

    private static PgJsonb Run(
        PgBenchmarkDefinition definition,
        Action? setup,
        Action<PgBencher> benchmark,
        PgJsonb? baseline,
        Action<Action> recovery,
        Action<Action> subtransaction)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(benchmark);
        ArgumentNullException.ThrowIfNull(recovery);
        ArgumentNullException.ThrowIfNull(subtransaction);
        try
        {
            Sample[] samples = [];
            Estimate[] estimates = [];
            Comparison? comparison = null;
            PgBenchmarkSamplingMode? samplingMode = null;
            recovery(() =>
            {
                Validate(definition.Configuration);
                setup?.Invoke();
                var bencher = new PgBencher();
                benchmark(bencher);
                PgBenchmarkRoutine routine = bencher.TakeRoutine();
                (long warmupElapsed, long warmupIterations) = WarmUp(routine, definition, subtransaction);
                (PgBenchmarkSamplingMode selectedMode, Sample[] measuredSamples) = Measure(
                    routine, definition, subtransaction, warmupElapsed, warmupIterations);
                samplingMode = selectedMode;
                samples = measuredSamples;
                estimates = EstimateSamples(samples, definition.Configuration, selectedMode);
                comparison = baseline is null ? null : Compare(samples, baseline.Value, definition.Configuration);
            });
            return WriteResult(definition, samples, estimates, comparison, samplingMode, null);
        }
        // codeql[cs/catch-of-all-exceptions] A failed benchmark is reported in its result, so the remaining benchmarks still run.
        catch (Exception exception)
        {
            return WriteResult(definition, [], [], null, null, exception.Message);
        }
    }

    private static void Validate(PgBenchmarkConfiguration configuration)
    {
        if (configuration.SampleSize < 10 || configuration.MeasurementTimeMilliseconds <= 0 ||
            configuration.WarmupTimeMilliseconds <= 0 || configuration.ResampleCount <= 0 ||
            !double.IsFinite(configuration.NoiseThreshold) || configuration.NoiseThreshold < 0 ||
            !double.IsFinite(configuration.SignificanceLevel) || configuration.SignificanceLevel is <= 0 or >= 1 ||
            configuration.Throughput is < PgBenchmarkThroughput.None or > PgBenchmarkThroughput.Elements ||
            (configuration.Throughput == PgBenchmarkThroughput.None ? configuration.ThroughputPerIteration != 0 : configuration.ThroughputPerIteration <= 0))
        {
            throw new InvalidOperationException("The generated benchmark configuration is invalid.");
        }
    }

    private static (long ElapsedTicks, long Iterations) WarmUp(
        PgBenchmarkRoutine routine,
        PgBenchmarkDefinition definition,
        Action<Action> subtransaction)
        => WarmUp(iterations => routine.Measure(iterations, definition.TransactionMode, subtransaction),
            MillisecondsToTicks(definition.Configuration.WarmupTimeMilliseconds));

    /// <summary>
    /// Runs Criterion's exponentially increasing warmup sequence and retains its complete calibration.
    /// </summary>
    /// <param name="measure">The routine that measures the exact requested iteration count.</param>
    /// <param name="targetTicks">The nonnegative warmup target in stopwatch ticks.</param>
    /// <returns>The cumulative elapsed ticks and iteration count.</returns>
    internal static (long ElapsedTicks, long Iterations) WarmUp(Func<long, long> measure, long targetTicks)
    {
        ArgumentNullException.ThrowIfNull(measure);
        ArgumentOutOfRangeException.ThrowIfNegative(targetTicks);
        long elapsed = 0;
        long totalIterations = 0;
        long iterations = 1;
        while (true)
        {
            if (totalIterations > long.MaxValue - iterations)
            {
                throw new InvalidOperationException("The benchmark warmup exceeded the supported iteration range.");
            }

            long measurement = measure(iterations);
            if (measurement < 0)
            {
                throw new InvalidOperationException("The benchmark warmup returned a negative duration.");
            }

            if (elapsed > long.MaxValue - measurement)
            {
                throw new InvalidOperationException("The benchmark warmup exceeded the supported duration range.");
            }

            elapsed += measurement;
            totalIterations += iterations;
            if (elapsed > targetTicks)
            {
                return (elapsed, totalIterations);
            }

            iterations = iterations > long.MaxValue / 2 ? long.MaxValue : iterations * 2;
        }
    }

    private static (PgBenchmarkSamplingMode SamplingMode, Sample[] Samples) Measure(
        PgBenchmarkRoutine routine,
        PgBenchmarkDefinition definition,
        Action<Action> subtransaction,
        long warmupElapsed,
        long warmupIterations)
    {
        PgBenchmarkConfiguration configuration = definition.Configuration;
        (PgBenchmarkSamplingMode samplingMode, long[] iterationCounts) = CreateSamplingPlan(
            warmupElapsed,
            warmupIterations,
            configuration.SampleSize,
            MillisecondsToTicks(configuration.MeasurementTimeMilliseconds));
        (long Iterations, long ElapsedTicks)[] measurements = MeasureSamples(
            iterations => routine.Measure(iterations, definition.TransactionMode, subtransaction),
            iterationCounts);

        var samples = new Sample[configuration.SampleSize];
        for (int index = 0; index < samples.Length; index++)
        {
            (long iterations, long elapsed) = measurements[index];
            samples[index] = new(index, iterations, TicksToNanoseconds(elapsed));
        }

        return (samplingMode, samples);
    }

    /// <summary>
    /// Creates Criterion's automatic linear or flat measurement plan from the complete warmup.
    /// </summary>
    /// <param name="warmupElapsedTicks">The positive cumulative warmup duration.</param>
    /// <param name="warmupIterations">The positive cumulative warmup iteration count.</param>
    /// <param name="sampleSize">The number of samples, which is at least ten.</param>
    /// <param name="measurementTicks">The positive target duration for all samples.</param>
    /// <returns>The selected mode and exact iteration count for each measurement sample.</returns>
    internal static (PgBenchmarkSamplingMode SamplingMode, long[] IterationCounts) CreateSamplingPlan(
        long warmupElapsedTicks,
        long warmupIterations,
        int sampleSize,
        long measurementTicks)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(warmupElapsedTicks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(warmupIterations);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleSize, 10);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(measurementTicks);

        double meanExecutionTicks = (double)warmupElapsedTicks / warmupIterations;
        double totalLinearRuns = (double)sampleSize * (sampleSize + 1L) / 2;
        long linearStep = IterationCount(measurementTicks / meanExecutionTicks / totalLinearRuns);
        double expectedLinearTicks = totalLinearRuns * linearStep * meanExecutionTicks;
        if (expectedLinearTicks > 2d * measurementTicks)
        {
            long flatIterations = IterationCount(measurementTicks / (double)sampleSize / meanExecutionTicks);
            return (PgBenchmarkSamplingMode.Flat, [.. Enumerable.Repeat(flatIterations, sampleSize)]);
        }

        long[] counts = new long[sampleSize];
        for (int index = 0; index < counts.Length; index++)
        {
            long multiplier = index + 1L;
            counts[index] = linearStep > long.MaxValue / multiplier ? long.MaxValue : linearStep * multiplier;
        }

        return (PgBenchmarkSamplingMode.Linear, counts);

        static long IterationCount(double value)
            => value >= long.MaxValue ? long.MaxValue : Math.Max(1, checked((long)Math.Ceiling(value)));
    }

    /// <summary>
    /// Measures every planned sample exactly once and retains its actual duration.
    /// </summary>
    /// <param name="measure">The routine that measures an exact iteration count.</param>
    /// <param name="iterationCounts">The positive planned counts in sample order.</param>
    /// <returns>The retained iteration count and elapsed ticks for every sample.</returns>
    internal static (long Iterations, long ElapsedTicks)[] MeasureSamples(
        Func<long, long> measure,
        IReadOnlyList<long> iterationCounts)
    {
        ArgumentNullException.ThrowIfNull(measure);
        ArgumentNullException.ThrowIfNull(iterationCounts);
        (long Iterations, long ElapsedTicks)[] measurements = new (long Iterations, long ElapsedTicks)[iterationCounts.Count];
        for (int index = 0; index < measurements.Length; index++)
        {
            long iterations = iterationCounts[index];
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(iterations);
            long elapsed = MeasureWithStackOffset(measure, iterations, index);
            if (elapsed < 0)
            {
                throw new InvalidOperationException("The benchmark measurement returned a negative duration.");
            }

            measurements[index] = (iterations, elapsed);
        }

        return measurements;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    private static long MeasureWithStackOffset(Func<long, long> measure, long iterations, int sampleIndex)
    {
        Span<byte> stackSpace = stackalloc byte[sampleIndex % Environment.SystemPageSize];
        if (!stackSpace.IsEmpty)
        {
            stackSpace[0] = unchecked((byte)sampleIndex);
        }

        long elapsed = measure(iterations);
        if (!stackSpace.IsEmpty)
        {
            PgBenchmark.BlackBox(stackSpace[0]);
        }

        return elapsed;
    }

    /// <summary>
    /// Computes Criterion's absolute estimates for retained measurement samples.
    /// </summary>
    /// <param name="samples">The retained samples.</param>
    /// <param name="configuration">The validated benchmark configuration.</param>
    /// <param name="samplingMode">The concrete sampling mode.</param>
    /// <returns>The ordered estimates emitted in the benchmark result.</returns>
    internal static Estimate[] EstimateSamples(
        Sample[] samples,
        PgBenchmarkConfiguration configuration,
        PgBenchmarkSamplingMode samplingMode)
    {
        if (samples.Any(static sample => sample.ElapsedNanoseconds is 0))
        {
            throw new InvalidOperationException(
                "At least one benchmark measurement took zero time per iteration. " +
                "Verify that the routine is measured correctly.");
        }

        double[] values = SampleValues(samples);
        var random = new Random(1_262_774_131);
        (double[] means, double[] standardDeviations, double[] medians, double[] medianAbsoluteDeviations) =
            BootstrapStatistics(values, configuration.ResampleCount, random);
        var estimates = new List<Estimate>
        {
            CreateEstimate("mean", Mean(values), means, ConfidenceLevel),
            CreateEstimate("median", Median(values), medians, ConfidenceLevel),
            CreateEstimate("median_abs_dev", MedianAbsoluteDeviation(values), medianAbsoluteDeviations, ConfidenceLevel),
        };
        if (samplingMode == PgBenchmarkSamplingMode.Linear)
        {
            double[] iterations = [.. samples.Select(static sample => (double)sample.Iterations)];
            double[] elapsed = [.. samples.Select(static sample => sample.ElapsedNanoseconds)];
            double[] slopes = BootstrapSlopes(iterations, elapsed, configuration.ResampleCount, random);
            estimates.Add(CreateEstimate("slope", Slope(iterations, elapsed), slopes, ConfidenceLevel));
        }

        estimates.Add(CreateEstimate(
            "std_dev", StandardDeviation(values), standardDeviations, ConfidenceLevel));
        return [.. estimates];
    }

    internal static Comparison Compare(Sample[] samples, PgJsonb baseline, PgBenchmarkConfiguration configuration)
    {
        double[] current = SampleValues(samples);
        double[] previous = BaselineValues(baseline);
        var random = new Random(1_262_774_131);
        (double[] meanChanges, double[] medianChanges) = BootstrapChanges(
            current, previous, configuration.ResampleCount, random);
        ComparisonEstimate mean = CreateComparisonEstimate("mean", Relative(Mean(current), Mean(previous)),
            meanChanges, ConfidenceLevel);
        ComparisonEstimate median = CreateComparisonEstimate("median", Relative(Median(current), Median(previous)),
            medianChanges, ConfidenceLevel);
        double pValue = PValue(current, previous, configuration.ResampleCount, random);
        string summary;
        if (pValue >= configuration.SignificanceLevel)
        {
            summary = "No change in performance detected.";
        }
        else if (mean.LowerBound < -configuration.NoiseThreshold && mean.UpperBound < -configuration.NoiseThreshold)
        {
            summary = "Performance has improved.";
        }
        else if (mean.LowerBound > configuration.NoiseThreshold && mean.UpperBound > configuration.NoiseThreshold)
        {
            summary = "Performance has regressed.";
        }
        else
        {
            summary = "Change within noise threshold.";
        }

        return new(mean, median, pValue, configuration.SignificanceLevel, configuration.NoiseThreshold, summary);
    }

    private static double[] SampleValues(Sample[] samples)
        => [.. samples.Select(static sample => sample.ElapsedNanoseconds / sample.Iterations)];

    private static double[] BaselineValues(PgJsonb baseline)
    {
        using JsonDocument document = baseline.Parse();
        JsonElement root = document.RootElement;
        if (root.GetProperty("status").GetString() != "ok")
        {
            throw new InvalidOperationException("The benchmark baseline did not complete successfully.");
        }

        double[] values = [.. root.GetProperty("samples").EnumerateArray().Select(static sample =>
        {
            long iterations = sample.GetProperty("iteration_count").GetInt64();
            double elapsed = sample.GetProperty("elapsed_ns").GetDouble();
            if (iterations <= 0 || !double.IsFinite(elapsed) || elapsed <= 0)
            {
                throw new InvalidOperationException("The benchmark baseline contains an invalid sample.");
            }

            return elapsed / iterations;
        })];
        if (values.Length < 2)
        {
            throw new InvalidOperationException("A benchmark comparison requires at least two baseline samples.");
        }

        return values;
    }

    private static (
        double[] Means,
        double[] StandardDeviations,
        double[] Medians,
        double[] MedianAbsoluteDeviations) BootstrapStatistics(
            double[] values,
            int count,
            Random random)
    {
        double[] means = new double[count];
        double[] standardDeviations = new double[count];
        double[] medians = new double[count];
        double[] medianAbsoluteDeviations = new double[count];
        double[] sample = new double[values.Length];
        double[] deviations = new double[values.Length];
        for (int index = 0; index < count; index++)
        {
            Resample(values, sample, random);
            means[index] = Mean(sample);
            standardDeviations[index] = StandardDeviation(sample);
            Array.Sort(sample);
            double median = MedianOrdered(sample);
            medians[index] = median;
            medianAbsoluteDeviations[index] = MedianAbsoluteDeviation(sample, median, deviations);
        }

        return (means, standardDeviations, medians, medianAbsoluteDeviations);
    }

    private static double[] BootstrapSlopes(double[] iterations, double[] elapsed, int count, Random random)
    {
        double[] slopes = new double[count];
        double[] resampledIterations = new double[iterations.Length];
        double[] resampledElapsed = new double[elapsed.Length];
        for (int bootstrap = 0; bootstrap < count; bootstrap++)
        {
            for (int index = 0; index < iterations.Length; index++)
            {
                int selected = random.Next(iterations.Length);
                resampledIterations[index] = iterations[selected];
                resampledElapsed[index] = elapsed[selected];
            }

            slopes[bootstrap] = Slope(resampledIterations, resampledElapsed);
        }

        return slopes;
    }

    private static (double[] Means, double[] Medians) BootstrapChanges(
        double[] current,
        double[] previous,
        int count,
        Random random)
    {
        double[] means = new double[count];
        double[] medians = new double[count];
        double[] currentSample = new double[current.Length];
        double[] previousSample = new double[previous.Length];
        for (int index = 0; index < count; index++)
        {
            Resample(current, currentSample, random);
            Resample(previous, previousSample, random);
            means[index] = Relative(Mean(currentSample), Mean(previousSample));
            Array.Sort(currentSample);
            Array.Sort(previousSample);
            medians[index] = Relative(MedianOrdered(currentSample), MedianOrdered(previousSample));
        }

        return (means, medians);
    }

    private static void Resample(double[] source, double[] destination, Random random)
    {
        for (int index = 0; index < destination.Length; index++)
        {
            destination[index] = source[random.Next(source.Length)];
        }
    }

    private static Estimate CreateEstimate(string kind, double point, double[] distribution, double confidence)
    {
        (double lower, double upper) = ConfidenceInterval(distribution, confidence);
        return new(kind, point, StandardDeviation(distribution), confidence, lower, upper);
    }

    private static ComparisonEstimate CreateComparisonEstimate(
        string kind,
        double point,
        double[] distribution,
        double confidence)
    {
        (double lower, double upper) = ConfidenceInterval(distribution, confidence);
        return new(kind, point, StandardDeviation(distribution), confidence, lower, upper);
    }

    private static (double Lower, double Upper) ConfidenceInterval(double[] distribution, double confidence)
    {
        Array.Sort(distribution);
        double tail = (1 - confidence) / 2;
        return (Quantile(distribution, tail), Quantile(distribution, 1 - tail));
    }

    private static double Quantile(double[] ordered, double probability)
    {
        double position = (ordered.Length - 1) * probability;
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        return lower == upper ? ordered[lower] : ordered[lower] + (ordered[upper] - ordered[lower]) * (position - lower);
    }

    /// <summary>
    /// Computes Criterion's mixed-bootstrap two-sided p-value for two sample distributions.
    /// </summary>
    /// <param name="current">The current per-iteration sample values.</param>
    /// <param name="previous">The baseline per-iteration sample values.</param>
    /// <param name="count">The positive resample count.</param>
    /// <param name="random">The resampling source.</param>
    /// <returns>The two-sided p-value.</returns>
    internal static double PValue(double[] current, double[] previous, int count, Random random)
    {
        double observed = SampleT(current, previous);
        if (!double.IsFinite(observed))
        {
            throw new InvalidOperationException("The benchmark comparison could not compute a finite T statistic.");
        }

        double[] combined = [.. current, .. previous];
        double[] resampled = new double[combined.Length];
        int finite = 0;
        int below = 0;
        for (int index = 0; index < count; index++)
        {
            Resample(combined, resampled, random);
            double statistic = SampleT(resampled.AsSpan(0, current.Length), resampled.AsSpan(current.Length));
            if (!double.IsFinite(statistic))
            {
                continue;
            }

            finite++;
            if (statistic < observed)
            {
                below++;
            }
        }

        if (finite == 0)
        {
            throw new InvalidOperationException("The benchmark comparison produced an empty T distribution.");
        }

        return Math.Min(1, 2d * Math.Min(below, finite - below) / finite);
    }

    private static double SampleT(ReadOnlySpan<double> current, ReadOnlySpan<double> previous)
    {
        double currentMean = Mean(current);
        double previousMean = Mean(previous);
        double denominator = Math.Sqrt(
            Variance(current, currentMean) / current.Length + Variance(previous, previousMean) / previous.Length);
        return denominator is 0 ? double.NaN : (currentMean - previousMean) / denominator;
    }

    private static double Mean(ReadOnlySpan<double> values)
    {
        double sum = 0;
        foreach (double value in values)
        {
            sum += value;
        }

        return sum / values.Length;
    }

    private static double Median(double[] values)
    {
        double[] ordered = (double[])values.Clone();
        Array.Sort(ordered);
        return MedianOrdered(ordered);
    }

    private static double MedianOrdered(double[] ordered)
    {
        int middle = ordered.Length / 2;
        return ordered.Length % 2 == 0 ? (ordered[middle - 1] + ordered[middle]) / 2 : ordered[middle];
    }

    private static double MedianAbsoluteDeviation(double[] values)
        => MedianAbsoluteDeviation(values, Median(values), new double[values.Length]);

    private static double MedianAbsoluteDeviation(double[] values, double median, double[] deviations)
    {
        for (int index = 0; index < values.Length; index++)
        {
            deviations[index] = Math.Abs(values[index] - median);
        }

        Array.Sort(deviations);
        return MedianOrdered(deviations) * 1.4826;
    }

    private static double Slope(ReadOnlySpan<double> iterations, ReadOnlySpan<double> elapsed)
    {
        double products = 0;
        double squares = 0;
        for (int index = 0; index < iterations.Length; index++)
        {
            products += iterations[index] * elapsed[index];
            squares += iterations[index] * iterations[index];
        }

        return products / squares;
    }

    private static double Variance(ReadOnlySpan<double> values, double mean)
    {
        if (values.Length < 2)
        {
            return 0;
        }

        double sum = 0;
        foreach (double value in values)
        {
            double difference = value - mean;
            sum += difference * difference;
        }

        return sum / (values.Length - 1);
    }

    private static double StandardDeviation(double[] values) => Math.Sqrt(Variance(values, Mean(values)));

    private static double Relative(double current, double previous)
    {
        if (previous is 0)
        {
            throw new InvalidOperationException("The benchmark baseline contains a zero-duration sample distribution.");
        }

        return current / previous - 1;
    }

    private static PgJsonb WriteResult(
        PgBenchmarkDefinition definition,
        Sample[] samples,
        Estimate[] estimates,
        Comparison? comparison,
        PgBenchmarkSamplingMode? samplingMode,
        string? error)
        => WriteJson(writer =>
        {
            writer.WriteStartObject();
            WriteDefinitionProperties(writer, definition);
            writer.WriteString("status", error is null ? "ok" : "failed");
            if (error is null)
            {
                writer.WriteNull("error_text");
            }
            else
            {
                writer.WriteString("error_text", error);
            }

            if (samplingMode is null)
            {
                writer.WriteNull("sampling_mode");
            }
            else
            {
                writer.WriteString("sampling_mode", SamplingModeName(samplingMode.Value));
            }

            writer.WriteStartArray("estimates");
            foreach (Estimate estimate in estimates)
            {
                writer.WriteStartObject();
                writer.WriteString("estimate_kind", estimate.Kind);
                writer.WriteNumber("point_estimate_ns", estimate.Point);
                WriteOptionalNumber(writer, "standard_error_ns", estimate.StandardError);
                WriteOptionalNumber(writer, "confidence_level", estimate.ConfidenceLevel);
                WriteOptionalNumber(writer, "ci_lower_bound_ns", estimate.LowerBound);
                WriteOptionalNumber(writer, "ci_upper_bound_ns", estimate.UpperBound);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("samples");
            foreach (Sample sample in samples)
            {
                writer.WriteStartObject();
                writer.WriteNumber("sample_index", sample.Index);
                writer.WriteNumber("iteration_count", sample.Iterations);
                writer.WriteNumber("elapsed_ns", sample.ElapsedNanoseconds);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            if (comparison is null)
            {
                writer.WriteNull("comparison");
            }
            else
            {
                writer.WriteStartObject("comparison");
                WriteComparisonEstimate(writer, "mean", comparison.Mean);
                WriteComparisonEstimate(writer, "median", comparison.Median);
                writer.WriteNumber("p_value", comparison.PValue);
                writer.WriteNumber("significance_level", comparison.SignificanceLevel);
                writer.WriteNumber("noise_threshold", comparison.NoiseThreshold);
                writer.WriteString("summary", comparison.Summary);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        });

    private static void WriteDefinition(Utf8JsonWriter writer, PgBenchmarkDefinition definition)
    {
        writer.WriteStartObject();
        WriteDefinitionProperties(writer, definition);
        writer.WriteEndObject();
    }

    private static void WriteDefinitionProperties(Utf8JsonWriter writer, PgBenchmarkDefinition definition)
    {
        writer.WriteString("schema_name", definition.SchemaName);
        writer.WriteString("bench_name", definition.BenchmarkName);
        writer.WriteString("function_name", definition.FunctionName);
        if (definition.SetupFunction is null)
        {
            writer.WriteNull("setup_function");
        }
        else
        {
            writer.WriteString("setup_function", definition.SetupFunction);
        }

        writer.WriteString("transaction_mode", TransactionName(definition.TransactionMode));
        writer.WriteString("source_file", definition.SourceFile);
        writer.WriteNumber("source_line", definition.SourceLine);
        PgBenchmarkConfiguration configuration = definition.Configuration;
        writer.WriteStartObject("config");
        writer.WriteNumber("sample_size", configuration.SampleSize);
        writer.WriteNumber("measurement_time_ms", configuration.MeasurementTimeMilliseconds);
        writer.WriteNumber("warm_up_time_ms", configuration.WarmupTimeMilliseconds);
        writer.WriteNumber("nresamples", configuration.ResampleCount);
        writer.WriteNumber("noise_threshold", configuration.NoiseThreshold);
        writer.WriteNumber("significance_level", configuration.SignificanceLevel);
        writer.WriteEndObject();
        if (configuration.Throughput == PgBenchmarkThroughput.None)
        {
            writer.WriteNull("throughput");
        }
        else
        {
            // pgrx keeps Criterion's throughput kinds in lowercase with the amount of work per iteration.
            writer.WriteStartObject("throughput");
            writer.WriteString("kind", configuration.Throughput switch
            {
                PgBenchmarkThroughput.Bytes => "bytes",
                PgBenchmarkThroughput.BytesDecimal => "bytesdecimal",
                _ => "elements",
            });
            writer.WriteNumber("value", configuration.ThroughputPerIteration);
            writer.WriteEndObject();
        }
    }

    private static string TransactionName(PgBenchmarkTransactionMode mode)
        => mode switch
        {
            PgBenchmarkTransactionMode.Shared => "shared",
            PgBenchmarkTransactionMode.SubtransactionPerBatch => "subtransaction_per_batch",
            PgBenchmarkTransactionMode.SubtransactionPerIteration => "subtransaction_per_iteration",
            PgBenchmarkTransactionMode.RollbackPerBatch => "rollback_per_batch",
            PgBenchmarkTransactionMode.RollbackPerIteration => "rollback_per_iteration",
            _ => throw new InvalidOperationException("The generated benchmark transaction mode is invalid."),
        };

    private static string SamplingModeName(PgBenchmarkSamplingMode mode)
        => mode switch
        {
            PgBenchmarkSamplingMode.Linear => "linear",
            PgBenchmarkSamplingMode.Flat => "flat",
            _ => throw new InvalidOperationException("The benchmark sampling mode is invalid."),
        };

    private static PgJsonb WriteJson(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
        }

        return new(Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    private static void WriteOptionalNumber(Utf8JsonWriter writer, string name, double? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteNumber(name, value.Value);
        }
    }

    private static void WriteComparisonEstimate(Utf8JsonWriter writer, string name, ComparisonEstimate estimate)
    {
        writer.WriteStartObject(name);
        writer.WriteString("estimate_kind", estimate.Kind);
        writer.WriteNumber("point_estimate", estimate.Point);
        writer.WriteNumber("standard_error", estimate.StandardError);
        writer.WriteNumber("confidence_level", estimate.ConfidenceLevel);
        writer.WriteNumber("ci_lower_bound", estimate.LowerBound);
        writer.WriteNumber("ci_upper_bound", estimate.UpperBound);
        writer.WriteEndObject();
    }

    private static long MillisecondsToTicks(int milliseconds)
        => checked((long)Math.Ceiling(milliseconds * (double)Stopwatch.Frequency / 1_000));

    private static double TicksToNanoseconds(long ticks) => ticks * 1_000_000_000d / Stopwatch.Frequency;

    /// <summary>
    /// Carries one retained benchmark measurement into analysis and reporting.
    /// </summary>
    /// <param name="Index">The zero-based sample index.</param>
    /// <param name="Iterations">The measured iteration count.</param>
    /// <param name="ElapsedNanoseconds">The measured elapsed nanoseconds.</param>
    internal sealed record Sample(int Index, long Iterations, double ElapsedNanoseconds);

    /// <summary>
    /// Carries one absolute benchmark estimate into result serialization.
    /// </summary>
    /// <param name="Kind">The statistic name.</param>
    /// <param name="Point">The point estimate.</param>
    /// <param name="StandardError">The bootstrap standard error.</param>
    /// <param name="ConfidenceLevel">The confidence level.</param>
    /// <param name="LowerBound">The confidence interval lower bound.</param>
    /// <param name="UpperBound">The confidence interval upper bound.</param>
    internal sealed record Estimate(
        string Kind,
        double Point,
        double? StandardError,
        double? ConfidenceLevel,
        double? LowerBound,
        double? UpperBound);

    internal sealed record ComparisonEstimate(
        string Kind,
        double Point,
        double StandardError,
        double ConfidenceLevel,
        double LowerBound,
        double UpperBound);

    internal sealed record Comparison(
        ComparisonEstimate Mean,
        ComparisonEstimate Median,
        double PValue,
        double SignificanceLevel,
        double NoiseThreshold,
        string Summary);
}
