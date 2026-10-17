using System.Globalization;
using System.Text.Json;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Verifies benchmark authoring, execution, batching and explicit result contracts.
/// </summary>
[TestClass]
public sealed class PgBenchmarkTests
{
    /// <summary>
    /// Keeps Ankus defaults aligned with the pgrx benchmark defaults.
    /// </summary>
    [TestMethod]
    public void AttributeUsesPgrxDefaults()
    {
        var attribute = new PgBenchmarkAttribute();

        Assert.AreEqual(PgBenchmarkTransactionMode.Shared, attribute.Transaction);
        Assert.AreEqual(100, attribute.SampleSize);
        Assert.AreEqual(5_000, attribute.MeasurementTimeMilliseconds);
        Assert.AreEqual(3_000, attribute.WarmupTimeMilliseconds);
        Assert.AreEqual(100_000, attribute.ResampleCount);
        Assert.AreEqual(0.01, attribute.NoiseThreshold);
        Assert.AreEqual(0.05, attribute.SignificanceLevel);
        Assert.IsNull(attribute.Setup);
    }

    /// <summary>
    /// Resolves every public batch strategy exactly.
    /// </summary>
    [TestMethod]
    public void BatchSizesPreserveStrategiesAndValues()
    {
        Assert.AreEqual(10L, PgBenchmarkBatchSize.SmallInput.GetIterationsPerBatch(100));
        Assert.AreEqual(1L, PgBenchmarkBatchSize.LargeInput.GetIterationsPerBatch(100));
        Assert.AreEqual(1L, PgBenchmarkBatchSize.PerIteration.GetIterationsPerBatch(100));
        Assert.AreEqual(25L, PgBenchmarkBatchSize.NumBatches(4).GetIterationsPerBatch(100));
        Assert.AreEqual(1L, PgBenchmarkBatchSize.NumBatches(long.MaxValue).GetIterationsPerBatch(100));
        Assert.AreEqual(7L, PgBenchmarkBatchSize.NumIterations(7).GetIterationsPerBatch(100));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PgBenchmarkBatchSize.NumBatches(0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PgBenchmarkBatchSize.NumIterations(0));
    }

    /// <summary>
    /// Rejects missing and duplicate timing loops before measurement.
    /// </summary>
    [TestMethod]
    public void BencherRequiresExactlyOneRoutine()
    {
        var missing = new PgBencher();
        Assert.ThrowsExactly<InvalidOperationException>(() => missing.TakeRoutine());

        var duplicate = new PgBencher();
        duplicate.Iterate(static () => 1);
        Assert.ThrowsExactly<InvalidOperationException>(() => duplicate.Iterate(static () => 2));
    }

    /// <summary>
    /// Prepares each input once and invokes the measured body with that input.
    /// </summary>
    [TestMethod]
    public void BatchedSetupRunsImmediatelyBeforeMeasuredRoutine()
    {
        int setup = 0;
        int invoked = 0;
        List<string> calls = [];
        var bencher = new PgBencher();
        bencher.IterateBatched(() =>
        {
            calls.Add("setup");
            return ++setup;
        }, value =>
        {
            calls.Add("routine");
            invoked += value;
        }, PgBenchmarkBatchSize.NumIterations(2));

        PgBenchmarkRoutine routine = bencher.TakeRoutine();
        routine.Run(5, PgBenchmarkTransactionMode.Shared, static action => action());

        Assert.AreEqual(5, setup);
        Assert.AreEqual(15, invoked);
        Assert.AreSequenceEqual(
            ["setup", "routine", "setup", "routine", "setup", "routine", "setup", "routine", "setup", "routine"],
            calls);
    }

    /// <summary>
    /// Includes batched input preparation in the elapsed boundary used by pgrx's backend bridge.
    /// </summary>
    /// <param name="mode">The transaction mode under test.</param>
    [TestMethod]
    [DataRow(PgBenchmarkTransactionMode.Shared)]
    [DataRow(PgBenchmarkTransactionMode.SubtransactionPerBatch)]
    [DataRow(PgBenchmarkTransactionMode.SubtransactionPerIteration)]
    [DataRow(PgBenchmarkTransactionMode.RollbackPerBatch)]
    [DataRow(PgBenchmarkTransactionMode.RollbackPerIteration)]
    public void BatchedMeasurementIncludesInputPreparation(PgBenchmarkTransactionMode mode)
    {
        long setupElapsed = 0;
        var bencher = new PgBencher();
        bencher.IterateBatched(() =>
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            long minimum = Math.Max(1, System.Diagnostics.Stopwatch.Frequency / 1_000);
            while (System.Diagnostics.Stopwatch.GetTimestamp() - started < minimum)
            {
                // Spin until the work has taken a measurable millisecond.
            }

            setupElapsed = System.Diagnostics.Stopwatch.GetTimestamp() - started;
            return 1;
        }, static _ => { }, PgBenchmarkBatchSize.PerIteration);

        long elapsed = bencher.TakeRoutine().Measure(
            1, mode, static action => action());

        Assert.IsGreaterThanOrEqualTo(setupElapsed, elapsed);
    }

    /// <summary>
    /// Includes the selected subtransaction boundary in the elapsed sample.
    /// </summary>
    /// <param name="mode">The subtransaction mode under test.</param>
    [TestMethod]
    [DataRow(PgBenchmarkTransactionMode.SubtransactionPerBatch)]
    [DataRow(PgBenchmarkTransactionMode.SubtransactionPerIteration)]
    [DataRow(PgBenchmarkTransactionMode.RollbackPerBatch)]
    [DataRow(PgBenchmarkTransactionMode.RollbackPerIteration)]
    public void BatchedMeasurementIncludesSubtransactionBoundary(PgBenchmarkTransactionMode mode)
    {
        long boundaryElapsed = 0;
        var bencher = new PgBencher();
        bencher.IterateBatched(static () => 1, static _ => { }, PgBenchmarkBatchSize.PerIteration);

        long elapsed = bencher.TakeRoutine().Measure(1, mode, action =>
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            action();
            long minimum = Math.Max(1, System.Diagnostics.Stopwatch.Frequency / 1_000);
            while (System.Diagnostics.Stopwatch.GetTimestamp() - started < minimum)
            {
                // Spin until the work has taken a measurable millisecond.
            }

            boundaryElapsed = System.Diagnostics.Stopwatch.GetTimestamp() - started;
        });

        Assert.IsGreaterThanOrEqualTo(boundaryElapsed, elapsed);
    }

    /// <summary>
    /// Preserves pgrx's 64-bit iteration domain without an array-size conversion failure.
    /// </summary>
    [TestMethod]
    public void BatchedMeasurementAcceptsIterationCountsBeyondArrayLimits()
    {
        var expected = new InvalidOperationException("setup reached");
        var bencher = new PgBencher();
        bencher.IterateBatched<int>(() => throw expected, static _ => { },
            PgBenchmarkBatchSize.NumIterations(long.MaxValue));

        InvalidOperationException actual = Assert.ThrowsExactly<InvalidOperationException>(() =>
            bencher.TakeRoutine().Measure((long)int.MaxValue + 1,
                PgBenchmarkTransactionMode.Shared, static action => action()));

        Assert.AreSame(expected, actual);
    }

    /// <summary>
    /// Applies shared, per-batch and per-iteration transaction boundaries exactly, whether they release or roll back.
    /// </summary>
    [TestMethod]
    public void TransactionModesUseTheirExactBoundaries()
    {
        Assert.AreEqual(0, CountSubtransactions(PgBenchmarkTransactionMode.Shared));
        Assert.AreEqual(3, CountSubtransactions(PgBenchmarkTransactionMode.SubtransactionPerBatch));
        Assert.AreEqual(5, CountSubtransactions(PgBenchmarkTransactionMode.SubtransactionPerIteration));
        Assert.AreEqual(3, CountSubtransactions(PgBenchmarkTransactionMode.RollbackPerBatch));
        Assert.AreEqual(5, CountSubtransactions(PgBenchmarkTransactionMode.RollbackPerIteration));
    }

    /// <summary>
    /// Keeps prepared inputs inside the same subtransaction as their measured use.
    /// </summary>
    /// <param name="mode">The transaction mode under test.</param>
    [TestMethod]
    [DataRow(PgBenchmarkTransactionMode.SubtransactionPerBatch)]
    [DataRow(PgBenchmarkTransactionMode.SubtransactionPerIteration)]
    [DataRow(PgBenchmarkTransactionMode.RollbackPerBatch)]
    [DataRow(PgBenchmarkTransactionMode.RollbackPerIteration)]
    public void BatchedSetupUsesSelectedTransactionBoundary(PgBenchmarkTransactionMode mode)
    {
        bool inside = false;
        var bencher = new PgBencher();
        bencher.IterateBatched(() =>
        {
            Assert.IsTrue(inside);
            return 1;
        }, value =>
        {
            Assert.AreEqual(1, value);
            Assert.IsTrue(inside);
        }, PgBenchmarkBatchSize.NumIterations(2));

        bencher.TakeRoutine().Run(5, mode, action =>
        {
            Assert.IsFalse(inside);
            inside = true;
            try
            {
                action();
            }
            finally
            {
                inside = false;
            }
        });

        Assert.IsFalse(inside);
    }

    /// <summary>
    /// Writes descriptors and successful measurement payloads without reflection.
    /// </summary>
    [TestMethod]
    public void RunnerReturnsNativeAotSafeDescriptorAndSamples()
    {
        var definition = new PgBenchmarkDefinition("benches", "Extension.Benchmarks.Add()", "ankus_bench_test", null,
            PgBenchmarkTransactionMode.Shared, "Benchmarks.cs", 17,
            new(10, 1, 1, 100, 2, 0.05));

        PgJsonb descriptor = PgBenchmarkRunner.Describe(definition);
        using (JsonDocument document = descriptor.Parse())
        {
            Assert.AreEqual("Extension.Benchmarks.Add()", document.RootElement.GetProperty("bench_name").GetString());
            Assert.AreEqual("shared", document.RootElement.GetProperty("transaction_mode").GetString());
            Assert.AreEqual(10, document.RootElement.GetProperty("config").GetProperty("sample_size").GetInt32());
            Assert.AreEqual(2d, document.RootElement.GetProperty("config").GetProperty("noise_threshold").GetDouble());
            Assert.AreEqual(JsonValueKind.Null, document.RootElement.GetProperty("throughput").ValueKind);
        }

        var throughputDefinition = new PgBenchmarkDefinition("benches", "Extension.Benchmarks.Copy()", "ankus_bench_copy", null,
            PgBenchmarkTransactionMode.Shared, "Benchmarks.cs", 23, new(10, 1, 1, 100, 0.01, 0.05, PgBenchmarkThroughput.BytesDecimal, 4096));
        using (JsonDocument document = PgBenchmarkRunner.Describe(throughputDefinition).Parse())
        {
            JsonElement throughput = document.RootElement.GetProperty("throughput");
            Assert.AreEqual("bytesdecimal", throughput.GetProperty("kind").GetString());
            Assert.AreEqual(4096, throughput.GetProperty("value").GetInt64());
        }

        using (JsonDocument invalid = PgBenchmarkRunner.Run(new PgBenchmarkDefinition("benches", "Extension.Benchmarks.Invalid()",
            "ankus_bench_invalid", null, PgBenchmarkTransactionMode.Shared, "Benchmarks.cs", 29,
            new(10, 1, 1, 100, 0.01, 0.05, PgBenchmarkThroughput.Elements, 0)), null, static bencher => bencher.Iterate(MeasuredWork), null,
            static action => action()).Parse())
        {
            Assert.AreEqual("failed", invalid.RootElement.GetProperty("status").GetString());
            Assert.AreEqual("The generated benchmark configuration is invalid.", invalid.RootElement.GetProperty("error_text").GetString());
        }

        PgJsonb result = PgBenchmarkRunner.Run(definition, null, static bencher => bencher.Iterate(MeasuredWork), null,
            static action => action());
        using JsonDocument resultDocument = result.Parse();
        Assert.AreEqual("ok", resultDocument.RootElement.GetProperty("status").GetString(),
            resultDocument.RootElement.TryGetProperty("error_text", out JsonElement error) ? error.GetString() : null);
        Assert.AreEqual(10, resultDocument.RootElement.GetProperty("samples").GetArrayLength());
        string? samplingMode = resultDocument.RootElement.GetProperty("sampling_mode").GetString();
        Assert.IsTrue(samplingMode is "linear" or "flat");
        string?[] estimateKinds = [.. resultDocument.RootElement.GetProperty("estimates").EnumerateArray()
            .Select(static estimate => estimate.GetProperty("estimate_kind").GetString())];
        string[] expectedKinds = samplingMode == "linear"
            ? ["mean", "median", "median_abs_dev", "slope", "std_dev"]
            : ["mean", "median", "median_abs_dev", "std_dev"];
        Assert.AreSequenceEqual(expectedKinds, estimateKinds);
    }

    /// <summary>
    /// Matches Criterion's absolute statistics and paired through-origin slope bootstrap on known samples.
    /// </summary>
    [TestMethod]
    public void EstimatorsMatchCriterionArithmetic()
    {
        PgBenchmarkRunner.Sample[] samples = [.. Enumerable.Range(1, 10)
            .Select(static value => new PgBenchmarkRunner.Sample(value - 1, value, (double)value * value))];
        var configuration = new PgBenchmarkConfiguration(10, 1, 1, 100, 0.01, 0.05);

        PgBenchmarkRunner.Estimate[] linear = PgBenchmarkRunner.EstimateSamples(
            samples, configuration, PgBenchmarkSamplingMode.Linear);
        Assert.AreSequenceEqual(["mean", "median", "median_abs_dev", "slope", "std_dev"],
            linear.Select(static estimate => estimate.Kind));
        Assert.AreEqual(5.5, linear[0].Point, 0.000_000_001);
        Assert.AreEqual(5.5, linear[1].Point, 0.000_000_001);
        Assert.AreEqual(3.7065, linear[2].Point, 0.000_000_001);
        Assert.AreEqual(55d / 7d, linear[3].Point, 0.000_000_001);
        Assert.AreEqual(Math.Sqrt(55d / 6d), linear[4].Point, 0.000_000_001);
        AssertSlopeBootstrap(linear[3], samples, configuration.ResampleCount);

        PgBenchmarkRunner.Estimate[] flat = PgBenchmarkRunner.EstimateSamples(
            samples, configuration, PgBenchmarkSamplingMode.Flat);
        Assert.AreSequenceEqual(["mean", "median", "median_abs_dev", "std_dev"],
            flat.Select(static estimate => estimate.Kind));
    }

    /// <summary>
    /// Each of Criterion's four summaries follows from the p-value and the mean change's confidence bounds, using
    /// deterministic samples: identical distributions, a doubled time, a halved time and a significant change inside the
    /// noise threshold.
    /// </summary>
    /// <param name="factor">The current time per iteration relative to the baseline.</param>
    /// <param name="noiseThreshold">The relative change treated as noise.</param>
    /// <param name="summary">The expected summary.</param>
    /// <param name="significant">Whether the p-value must fall below the significance level.</param>
    [TestMethod]
    [DataRow(1.0, 0.05, "No change in performance detected.", false)]
    [DataRow(2.0, 0.05, "Performance has regressed.", true)]
    [DataRow(0.5, 0.05, "Performance has improved.", true)]
    [DataRow(1.02, 0.05, "Change within noise threshold.", true)]
    public void ComparisonSummariesFollowSignificanceAndNoise(double factor, double noiseThreshold, string summary, bool significant)
    {
        var configuration = new PgBenchmarkConfiguration(100, 1, 1, 1_000, noiseThreshold, 0.05);
        double[] noise = [.. Enumerable.Range(0, 100).Select(static index => (index % 10) - 4.5)];
        PgBenchmarkRunner.Sample[] current = [.. noise.Select((offset, index) =>
            new PgBenchmarkRunner.Sample(index, 10, ((1_000 * factor) + offset) * 10))];
        string baseline = "{\"status\":\"ok\",\"samples\":[" + string.Join(',', noise.Select(static offset =>
            "{\"iteration_count\":10,\"elapsed_ns\":" + ((1_000 + offset) * 10).ToString(CultureInfo.InvariantCulture) + "}")) + "]}";
        PgBenchmarkRunner.Comparison comparison = PgBenchmarkRunner.Compare(current, new PgJsonb(baseline), configuration);
        Assert.AreEqual(summary, comparison.Summary);
        Assert.AreEqual(significant, comparison.PValue < 0.05, comparison.PValue.ToString(CultureInfo.InvariantCulture));
        Assert.AreEqual(factor - 1, comparison.Mean.Point, 0.001);
        Assert.IsLessThanOrEqualTo(comparison.Mean.Point, comparison.Mean.LowerBound);
        Assert.IsGreaterThanOrEqualTo(comparison.Mean.Point, comparison.Mean.UpperBound);
    }

    /// <summary>
    /// Uses persisted raw samples for bootstrap confidence bounds, significance, and the Criterion-style summary.
    /// </summary>
    [TestMethod]
    public void RunnerComparesPersistedSamplesWithConfiguredResampling()
    {
        var definition = new PgBenchmarkDefinition("benches", "Extension.Benchmarks.Compare()", "ankus_bench_compare", null,
            PgBenchmarkTransactionMode.Shared, "Benchmarks.cs", 23,
            new(10, 1, 1, 100, 0.01, 0.10));
        PgJsonb baseline = PgBenchmarkRunner.Run(definition, null,
            static bencher => bencher.Iterate(MeasuredWork), null, static action => action());
        using (JsonDocument baselineDocument = baseline.Parse())
        {
            Assert.AreEqual("ok", baselineDocument.RootElement.GetProperty("status").GetString(), baseline.Text);
        }

        PgJsonb result = PgBenchmarkRunner.Run(definition, null,
            static bencher => bencher.Iterate(MeasuredWork), baseline, static action => action());

        using JsonDocument document = result.Parse();
        Assert.AreEqual("ok", document.RootElement.GetProperty("status").GetString(), result.Text);
        JsonElement comparison = document.RootElement.GetProperty("comparison");
        Assert.IsTrue(double.IsFinite(comparison.GetProperty("p_value").GetDouble()));
        Assert.AreEqual(0.10, comparison.GetProperty("significance_level").GetDouble(), 0.000_001);
        Assert.AreEqual(0.95, comparison.GetProperty("mean").GetProperty("confidence_level").GetDouble(), 0.000_001);
        Assert.IsTrue(double.IsFinite(comparison.GetProperty("median").GetProperty("point_estimate").GetDouble()));
        Assert.IsFalse(string.IsNullOrWhiteSpace(comparison.GetProperty("summary").GetString()));
    }

    /// <summary>
    /// Matches Criterion's exponential warmup and requires the cumulative duration to exceed the target.
    /// </summary>
    [TestMethod]
    public void WarmupProducesCriterionCalibration()
    {
        long[] durations = [40, 60, 1];
        int next = 0;
        List<long> measured = [];
        long Measure(long iterations)
        {
            measured.Add(iterations);
            return durations[next++];
        }

        (long elapsed, long iterations) = PgBenchmarkRunner.WarmUp(Measure, 100);
        Assert.AreEqual(101L, elapsed);
        Assert.AreEqual(7L, iterations);
        Assert.AreSequenceEqual([1, 2, 4], measured);
        Assert.AreEqual(durations.Length, next);
    }

    /// <summary>
    /// Rejects invalid warmup results and detects duration or iteration overflow.
    /// </summary>
    [TestMethod]
    public void WarmupRejectsInvalidMeasurementsAndOverflow()
    {
        InvalidOperationException negative = Assert.ThrowsExactly<InvalidOperationException>(() =>
            PgBenchmarkRunner.WarmUp(static _ => -1, 1));
        Assert.AreEqual("The benchmark warmup returned a negative duration.", negative.Message);

        long lastIteration = 0;
        InvalidOperationException overflow = Assert.ThrowsExactly<InvalidOperationException>(() =>
            PgBenchmarkRunner.WarmUp(iterations =>
            {
                lastIteration = iterations;
                return 0;
            }, 1));
        Assert.AreEqual("The benchmark warmup exceeded the supported iteration range.", overflow.Message);
        Assert.AreEqual(1L << 62, lastIteration);

        int durationCalls = 0;
        InvalidOperationException durationOverflow = Assert.ThrowsExactly<InvalidOperationException>(() =>
            PgBenchmarkRunner.WarmUp(_ => ++durationCalls == 1 ? long.MaxValue : 1, long.MaxValue));
        Assert.AreEqual("The benchmark warmup exceeded the supported duration range.", durationOverflow.Message);
        Assert.AreEqual(2, durationCalls);
    }

    /// <summary>
    /// Produces Criterion's linearly increasing automatic sample plan for ordinary routines.
    /// </summary>
    [TestMethod]
    public void AutomaticSamplingUsesCriterionLinearPlan()
    {
        (PgBenchmarkSamplingMode mode, long[] counts) =
            PgBenchmarkRunner.CreateSamplingPlan(70, 7, 10, 5_500);
        Assert.AreEqual(PgBenchmarkSamplingMode.Linear, mode);
        Assert.AreSequenceEqual([10, 20, 30, 40, 50, 60, 70, 80, 90, 100], counts);
    }

    /// <summary>
    /// Keeps Criterion's linear mode when its estimated duration is exactly twice the target.
    /// </summary>
    [TestMethod]
    public void AutomaticSamplingKeepsCriterionBoundaryMode()
    {
        (PgBenchmarkSamplingMode mode, long[] counts) =
            PgBenchmarkRunner.CreateSamplingPlan(140, 7, 10, 550);
        Assert.AreEqual(PgBenchmarkSamplingMode.Linear, mode);
        Assert.AreSequenceEqual([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], counts);
    }

    /// <summary>
    /// Uses Criterion's flat plan when linear sampling would exceed twice the target duration.
    /// </summary>
    [TestMethod]
    public void AutomaticSamplingUsesCriterionFlatPlanForSlowRoutines()
    {
        (PgBenchmarkSamplingMode mode, long[] counts) =
            PgBenchmarkRunner.CreateSamplingPlan(21, 7, 100, 1_000);
        Assert.AreEqual(PgBenchmarkSamplingMode.Flat, mode);
        Assert.HasCount(100, counts);
        Assert.IsTrue(counts.All(static count => count == 4));
    }

    /// <summary>
    /// Runs every planned sample once and retains below-target durations without selection bias.
    /// </summary>
    [TestMethod]
    public void MeasurementRetainsEveryPlannedSample()
    {
        long[] counts = [2, 4, 6];
        long[] durations = [1, 40, 12];
        int next = 0;
        List<long> measured = [];
        (long Iterations, long ElapsedTicks)[] samples = PgBenchmarkRunner.MeasureSamples(iterations =>
        {
            measured.Add(iterations);
            return durations[next++];
        }, counts);

        Assert.AreSequenceEqual(counts, measured);
        Assert.AreEqual(durations.Length, next);
        for (int index = 0; index < samples.Length; index++)
        {
            Assert.AreEqual(counts[index], samples[index].Iterations);
            Assert.AreEqual(durations[index], samples[index].ElapsedTicks);
        }
    }

    /// <summary>
    /// Rejects invalid timing values and preserves routine failures without retrying author code.
    /// </summary>
    [TestMethod]
    public void MeasurementPreservesFailuresWithoutRetrying()
    {
        int zeroCalls = 0;
        (long Iterations, long ElapsedTicks)[] zeroSamples = PgBenchmarkRunner.MeasureSamples(_ =>
        {
            zeroCalls++;
            return 0;
        }, [1, 2]);
        Assert.AreEqual(2, zeroCalls);
        Assert.IsTrue(zeroSamples.All(static sample => sample.ElapsedTicks == 0));
        var configuration = new PgBenchmarkConfiguration(10, 1, 1, 100, 0.01, 0.05);
        PgBenchmarkRunner.Sample[] analysisSamples = [.. Enumerable.Range(0, 10)
            .Select(static index => new PgBenchmarkRunner.Sample(index, 1, 0))];
        InvalidOperationException zero = Assert.ThrowsExactly<InvalidOperationException>(() =>
            PgBenchmarkRunner.EstimateSamples(analysisSamples, configuration, PgBenchmarkSamplingMode.Flat));
        Assert.Contains("took zero time per iteration", zero.Message);
        InvalidOperationException negative = Assert.ThrowsExactly<InvalidOperationException>(() =>
            PgBenchmarkRunner.MeasureSamples(static _ => -1, [1]));
        Assert.AreEqual("The benchmark measurement returned a negative duration.", negative.Message);
        var expected = new InvalidOperationException("benchmark routine failed");
        int calls = 0;
        InvalidOperationException actual = Assert.ThrowsExactly<InvalidOperationException>(() =>
            PgBenchmarkRunner.MeasureSamples(_ =>
            {
                calls++;
                throw expected;
            }, [1, 2]));
        Assert.AreSame(expected, actual);
        Assert.AreEqual(1, calls);
    }

    /// <summary>
    /// Rejects the non-finite comparison statistic that pgrx reports as a failed benchmark.
    /// </summary>
    [TestMethod]
    public void ComparisonRejectsNonFiniteTStatistic()
    {
        double[] current = [1, 1, 1];
        double[] baseline = [1, 1, 1];

        InvalidOperationException error = Assert.ThrowsExactly<InvalidOperationException>(() =>
            PgBenchmarkRunner.PValue(current, baseline, 100, new(42)));

        Assert.Contains("finite T statistic", error.Message);
    }

    /// <summary>
    /// Prevents value-type benchmark results from allocating through boxing.
    /// </summary>
    [TestMethod]
    public void BlackBoxDoesNotBoxValueTypes()
    {
        _ = PgBenchmark.BlackBox(0);
        long before = GC.GetAllocatedBytesForCurrentThread();
        int total = 0;
        for (int value = 0; value < 1_000; value++)
        {
            total += PgBenchmark.BlackBox(value);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.AreEqual(499_500, total);
        Assert.AreEqual(0L, allocated);
        Span<int> input = [42];
        Span<int> output = PgBenchmark.BlackBox(input);
        Assert.AreEqual(42, output[0]);
    }

    /// <summary>
    /// Returns author failures as explicit result payloads.
    /// </summary>
    [TestMethod]
    public void RunnerReturnsExplicitFailurePayload()
    {
        var definition = new PgBenchmarkDefinition("benches", "Extension.Benchmarks.Fails()", "ankus_bench_failure", null,
            PgBenchmarkTransactionMode.Shared, "Benchmarks.cs", 29,
            new(10, 1, 1, 100, 0.01, 0.05));

        PgJsonb result = PgBenchmarkRunner.Run(definition, null,
            static _ => throw new InvalidOperationException("expected benchmark failure"), null, static action => action());

        using JsonDocument document = result.Parse();
        Assert.AreEqual("failed", document.RootElement.GetProperty("status").GetString());
        Assert.AreEqual("expected benchmark failure", document.RootElement.GetProperty("error_text").GetString());
        Assert.AreEqual(0, document.RootElement.GetProperty("samples").GetArrayLength());
    }

    /// <summary>
    /// Rejects invalid generated benchmark settings before invoking benchmark code.
    /// </summary>
    [TestMethod]
    public void RunnerRejectsInvalidConfiguration()
    {
        PgBenchmarkConfiguration[] invalidConfigurations =
        [
            new(9, 1, 1, 100, 0.01, 0.05),
            new(10, 1, 0, 100, 0.01, 0.05),
        ];
        foreach (PgBenchmarkDefinition definition in invalidConfigurations.Select(static configuration => new PgBenchmarkDefinition("benches",
            "Extension.Benchmarks.Invalid()", "ankus_bench_invalid", null, PgBenchmarkTransactionMode.Shared, "Benchmarks.cs", 31, configuration)))
        {
            bool invoked = false;

            PgJsonb result = PgBenchmarkRunner.Run(definition, null, bencher =>
            {
                invoked = true;
                bencher.Iterate(static () => { });
            }, null, static action => action());

            using JsonDocument document = result.Parse();
            Assert.AreEqual("failed", document.RootElement.GetProperty("status").GetString());
            string? error = document.RootElement.GetProperty("error_text").GetString();
            Assert.IsNotNull(error);
            Assert.Contains("configuration is invalid", error);
            Assert.IsFalse(invoked);
        }
    }

    private static int CountSubtransactions(PgBenchmarkTransactionMode mode)
    {
        int boundaries = 0;
        var bencher = new PgBencher();
        bencher.IterateBatched(static () => 1, static _ => { }, PgBenchmarkBatchSize.NumIterations(2));
        bencher.TakeRoutine().Run(5, mode, action =>
        {
            boundaries++;
            action();
        });

        return boundaries;
    }

    private static void AssertSlopeBootstrap(
        PgBenchmarkRunner.Estimate actual,
        PgBenchmarkRunner.Sample[] samples,
        int resampleCount)
    {
        Random random = new(1_262_774_131);
        for (int draw = 0; draw < resampleCount * samples.Length; draw++)
        {
            _ = random.Next(samples.Length);
        }

        double[] distribution = new double[resampleCount];
        for (int bootstrap = 0; bootstrap < distribution.Length; bootstrap++)
        {
            double products = 0;
            double squares = 0;
            for (int index = 0; index < samples.Length; index++)
            {
                PgBenchmarkRunner.Sample selected = samples[random.Next(samples.Length)];
                products += selected.Iterations * selected.ElapsedNanoseconds;
                squares += (double)selected.Iterations * selected.Iterations;
            }

            distribution[bootstrap] = products / squares;
        }

        Array.Sort(distribution);
        double mean = distribution.Average();
        double squaredDifferences = distribution.Sum(value => (value - mean) * (value - mean));
        double standardError = Math.Sqrt(squaredDifferences / (distribution.Length - 1));
        Assert.AreEqual(standardError, actual.StandardError!.Value, 0.000_000_001);
        Assert.AreEqual(Percentile(distribution, 0.025), actual.LowerBound!.Value, 0.000_000_001);
        Assert.AreEqual(Percentile(distribution, 0.975), actual.UpperBound!.Value, 0.000_000_001);
    }

    private static double Percentile(double[] ordered, double probability)
    {
        double position = (ordered.Length - 1) * probability;
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        return lower == upper
            ? ordered[lower]
            : ordered[lower] + (ordered[upper] - ordered[lower]) * (position - lower);
    }

    /// <summary>
    /// Takes long enough per iteration that no sample can round to zero ticks, which Criterion rejects, on a coarse
    /// Windows stopwatch.
    /// </summary>
    /// <returns>A value the optimizer must keep.</returns>
    private static int MeasuredWork()
    {
        Thread.SpinWait(1_000);
        return PgBenchmark.BlackBox(42);
    }
}
