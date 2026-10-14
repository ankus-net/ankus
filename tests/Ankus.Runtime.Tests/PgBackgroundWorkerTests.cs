using System.Runtime.InteropServices;
using System.Text;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Verifies worker transport, ownership and managed callback boundaries without simulating PostgreSQL processes.
/// </summary>
[TestClass]
public sealed unsafe class PgBackgroundWorkerTests
{
    /// <summary>
    /// Defaults follow database access, while explicit phases and exact declaration bytes survive registration.
    /// </summary>
    [TestMethod]
    public void WorkerOptionsPreserveValues()
    {
        var defaults = new PgBackgroundWorkerOptions("worker", "library", "Run");
        Assert.AreEqual("worker", defaults.Type);
        Assert.AreEqual(PgBackgroundWorkerStartTime.PostmasterStart, defaults.StartTime);
        Assert.IsFalse(defaults.DatabaseAccess);
        Assert.IsNull(defaults.RestartDelay);
        Assert.AreEqual((nuint)0, defaults.Argument);
        Assert.AreEqual(string.Empty, defaults.Extra);
        Assert.AreEqual(0, defaults.NotifyProcessId);
        var database = new PgBackgroundWorkerOptions("worker", "library", "Run") { DatabaseAccess = true };
        Assert.AreEqual(PgBackgroundWorkerStartTime.RecoveryFinished, database.StartTime);
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        fixture.Handler = request =>
        {
            Assert.AreEqual(NativeMemoryOperation.BackgroundWorker, request._operation);
            Assert.AreEqual((nuint)sizeof(NativeBackgroundWorkerDefinition), request._length);
            NativeBackgroundWorkerDefinition definition = *(NativeBackgroundWorkerDefinition*)request._pointer;
            Assert.AreEqual("worker café 🐘", Marshal.PtrToStringUTF8(definition._name));
            Assert.AreEqual("reporting", Marshal.PtrToStringUTF8(definition._type));
            Assert.AreEqual("library", Marshal.PtrToStringUTF8(definition._library));
            Assert.AreEqual("Run", Marshal.PtrToStringUTF8(definition._entryPoint));
            Assert.AreEqual("payload π", Marshal.PtrToStringUTF8(definition._extra));
            Assert.AreEqual(nuint.MaxValue, definition._argument);
            Assert.AreEqual(1, definition._startTime);
            Assert.AreEqual(5, definition._restartSeconds);
            Assert.AreEqual(1, definition._databaseAccess);
            Assert.AreEqual(0, definition._notifyProcessId);
            return default;
        };
        PgBackgroundWorker.Register(new("worker café 🐘", "library", "Run")
        {
            Type = "reporting",
            Extra = "payload π",
            Argument = nuint.MaxValue,
            DatabaseAccess = true,
            StartTime = PgBackgroundWorkerStartTime.ConsistentState,
            RestartDelay = TimeSpan.FromSeconds(5),
        });
        Assert.AreEqual(0, Assert.ContainsSingle(fixture.Requests)._flags);
    }

    /// <summary>
    /// Dynamic registration preserves default empty payloads, notification identity and exact restart boundaries.
    /// </summary>
    /// <param name="databaseAccess">Whether startup must wait for recovery.</param>
    /// <param name="seconds">Whole restart seconds, or minus one for no restart.</param>
    [TestMethod]
    [DataRow(false, -1)]
    [DataRow(true, 0)]
    [DataRow(true, 86400000)]
    public void WorkerRegistrationPreservesDefaultsAndRestartBoundaries(bool databaseAccess, int seconds)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        fixture.Handler = request =>
        {
            if (request._flags != 1)
            {
                return default;
            }

            NativeBackgroundWorkerDefinition definition = *(NativeBackgroundWorkerDefinition*)request._pointer;
            Assert.AreEqual("worker", Marshal.PtrToStringUTF8(definition._type));
            Assert.AreEqual(string.Empty, Marshal.PtrToStringUTF8(definition._extra));
            Assert.AreEqual((nuint)0, definition._argument);
            Assert.AreEqual(databaseAccess ? 2 : 0, definition._startTime);
            Assert.AreEqual(databaseAccess ? 1 : 0, definition._databaseAccess);
            Assert.AreEqual(seconds, definition._restartSeconds);
            Assert.AreEqual(int.MaxValue, definition._notifyProcessId);
            return new() { _value = 1, _context = 71 };
        };
        Assert.IsTrue(PgBackgroundWorker.TryStart(new("worker", "library", "Run")
        {
            DatabaseAccess = databaseAccess,
            RestartDelay = seconds == -1 ? null : TimeSpan.FromSeconds(seconds),
            NotifyProcessId = int.MaxValue,
        }, out PgBackgroundWorkerHandle? handle));
        handle.Dispose();
        Assert.AreSequenceEqual([1, 6], fixture.Requests.Select(static request => request._flags));
    }

    /// <summary>
    /// Dynamic metadata rejects bytes PostgreSQL would replace, while the independent extra payload remains Unicode.
    /// </summary>
    /// <param name="field">The dynamic metadata field.</param>
    /// <param name="parameter">The rejected option name.</param>
    [TestMethod]
    [DataRow(0, "Name")]
    [DataRow(1, "Type")]
    [DataRow(2, "Library")]
    [DataRow(3, "EntryPoint")]
    public void WorkerDynamicMetadataRejectsLossyConversion(int field, string parameter)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        var options = new PgBackgroundWorkerOptions(field == 0 ? "worker π" : "worker",
            field == 2 ? "library ü" : "library", field == 3 ? "entry σ" : "Run")
        {
            Type = field == 1 ? "type\u0001" : "type",
        };
        ArgumentException failure = Assert.ThrowsExactly<ArgumentException>(() => PgBackgroundWorker.TryStart(options, out _));
        Assert.AreEqual(parameter, failure.ParamName);
        Assert.Contains("unchanged", failure.Message);
        Assert.IsEmpty(fixture.Requests);
        fixture.Handler = request =>
        {
            if (request._flags != 1)
            {
                return default;
            }

            NativeBackgroundWorkerDefinition definition = *(NativeBackgroundWorkerDefinition*)request._pointer;
            Assert.AreEqual("worker\t\r\n\x7F", Marshal.PtrToStringUTF8(definition._name));
            Assert.AreEqual("payload café 🐘", Marshal.PtrToStringUTF8(definition._extra));
            return new() { _value = 1, _context = 71 };
        };
        Assert.IsTrue(PgBackgroundWorker.TryStart(new("worker\t\r\n\x7F", "library", "Run")
        { Extra = "payload café 🐘" }, out PgBackgroundWorkerHandle? handle));
        handle.Dispose();
        Assert.AreSequenceEqual([1, 6], fixture.Requests.Select(static request => request._flags));
    }

    /// <summary>
    /// Invalid options fail before native registration, without trimming or rounding their values.
    /// </summary>
    /// <param name="scenario">The invalid declaration partition.</param>
    /// <param name="parameter">The rejected argument or option name.</param>
    [TestMethod]
    [DataRow(0, "options")]
    [DataRow(1, "Name")]
    [DataRow(2, "Name")]
    [DataRow(3, "Type")]
    [DataRow(4, "Library")]
    [DataRow(5, "EntryPoint")]
    [DataRow(6, "Extra")]
    [DataRow(7, "StartTime")]
    [DataRow(8, "StartTime")]
    [DataRow(9, "NotifyProcessId")]
    [DataRow(10, "RestartDelay")]
    [DataRow(11, "RestartDelay")]
    [DataRow(12, "RestartDelay")]
    [DataRow(13, "options")]
    [DataRow(14, "options")]
    public void WorkerOptionsRejectInvalidValues(int scenario, string parameter)
    {
        using var fixture = new MemoryContextTestFixture();
        PgBackgroundWorkerOptions? options = scenario switch
        {
            0 => null,
            1 => new(null!, "library", "Run"),
            2 => new(string.Empty, "library", "Run"),
            3 => new("worker", "library", "Run") { Type = "a\0b" },
            4 => new("worker", string.Empty, "Run"),
            5 => new("worker", "library", "a\0b"),
            6 => new("worker", "library", "Run") { Extra = null! },
            7 => new("worker", "library", "Run") { StartTime = (PgBackgroundWorkerStartTime)(-1) },
            8 => new("worker", "library", "Run") { StartTime = (PgBackgroundWorkerStartTime)3 },
            9 => new("worker", "library", "Run") { NotifyProcessId = -1 },
            10 => new("worker", "library", "Run") { RestartDelay = TimeSpan.FromTicks(-1) },
            11 => new("worker", "library", "Run") { RestartDelay = TimeSpan.FromTicks(TimeSpan.TicksPerSecond + 1) },
            12 => new("worker", "library", "Run") { RestartDelay = TimeSpan.FromSeconds((long)int.MaxValue + 1) },
            13 => new("worker", "library", "Run") { DatabaseAccess = true, StartTime = PgBackgroundWorkerStartTime.PostmasterStart },
            _ => new("worker", "library", "Run") { NotifyProcessId = 71 },
        };
        ArgumentException error = Assert.Throws<ArgumentException>(() => PgBackgroundWorker.Register(options!));
        Assert.AreEqual(parameter, error.ParamName);
        Assert.IsEmpty(fixture.Requests);
        Assert.ThrowsExactly<EncoderFallbackException>(() => PgBackgroundWorker.Register(new("bad\uD800", "library", "Run")));
    }

    /// <summary>
    /// Registration exhaustion, failure cleanup and malformed outcomes preserve native handle ownership.
    /// </summary>
    /// <param name="outcome">The scripted native registration result.</param>
    /// <param name="identity">The scripted native observation identity.</param>
    [TestMethod]
    [DataRow(0, 0)]
    [DataRow(0, 71)]
    [DataRow(1, 0)]
    [DataRow(1, -1)]
    [DataRow(2, 71)]
    public void WorkerRegistrationValidatesReplies(int outcome, int identity)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        fixture.Handler = request => request._flags == 1 ? new() { _value = outcome, _context = identity } : default;
        var options = new PgBackgroundWorkerOptions("worker", "library", "Run");
        if (outcome == 0 && identity == 0)
        {
            Assert.IsFalse(PgBackgroundWorker.TryStart(options, out PgBackgroundWorkerHandle? handle));
            Assert.IsNull(handle);
        }
        else
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => PgBackgroundWorker.TryStart(options, out _));
        }

        Assert.AreSequenceEqual(identity > 0 ? [1, 6] : [1], fixture.Requests.Select(static request => request._flags));
        if (identity > 0)
        {
            Assert.AreEqual(identity, fixture.Requests[1]._context);
        }
    }

    /// <summary>
    /// Status, PID, startup and shutdown remain distinct from termination and observation disposal.
    /// </summary>
    [TestMethod]
    public void WorkerHandlesPreserveLifecycle()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        fixture.Handler = request => request._flags switch
        {
            1 => new() { _context = 71, _value = 1 },
            2 => new() { _value = 1 },
            3 => new() { _value = 0, _pointer = 313 },
            5 => new() { _value = 2 },
            _ => default,
        };
        Assert.IsTrue(PgBackgroundWorker.TryStart(new("worker", "library", "Run") { NotifyProcessId = 311 }, out PgBackgroundWorkerHandle? handle));
        using (handle)
        {
            Assert.AreEqual(311, handle.NotifyProcessId);
            Assert.AreEqual(new(PgBackgroundWorkerStatus.NotYetStarted, null), handle.GetState());
            Assert.AreEqual(new(PgBackgroundWorkerStatus.Started, 313), handle.WaitForStartup());
            handle.Terminate();
            Assert.AreEqual(PgBackgroundWorkerStatus.Stopped, handle.WaitForShutdown());
        }

        handle.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => handle.GetState());
        Assert.ThrowsExactly<ObjectDisposedException>(handle.Terminate);
        Assert.AreSequenceEqual([1, 2, 3, 4, 5, 6], fixture.Requests.Select(static request => request._flags));
        Assert.IsTrue(fixture.Requests.Skip(1).All(static request => request._context == 71));
    }

    /// <summary>
    /// Handle operations reject impossible native states and process identifiers.
    /// </summary>
    /// <param name="operation">The observation operation.</param>
    /// <param name="status">The returned state discriminator.</param>
    /// <param name="process">The returned process identifier.</param>
    [TestMethod]
    [DataRow(2, 0, 0)]
    [DataRow(2, 0, -1)]
    [DataRow(2, 1, 71)]
    [DataRow(2, 3, 0)]
    [DataRow(2, 4, 0)]
    [DataRow(3, 1, 0)]
    [DataRow(3, 2, 71)]
    [DataRow(5, 0, 71)]
    [DataRow(5, 1, 0)]
    [DataRow(5, 5, 0)]
    public void WorkerHandlesRejectInvalidReplies(int operation, int status, int process)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        fixture.Handler = request => request._flags == 1 ? new() { _context = 71, _value = 1 } :
            new() { _value = status, _pointer = process };
        Assert.IsTrue(PgBackgroundWorker.TryStart(new("worker", "library", "Run"), out PgBackgroundWorkerHandle? handle));
        using (handle)
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => Observe(handle, operation));
        }
    }

    /// <summary>
    /// Untracked waits and postmaster death are owned outcomes rather than invented PIDs or thrown transport failures.
    /// </summary>
    /// <param name="operation">The wait operation.</param>
    /// <param name="status">The valid terminal or untracked state.</param>
    [TestMethod]
    [DataRow(3, PgBackgroundWorkerStatus.Stopped)]
    [DataRow(3, PgBackgroundWorkerStatus.PostmasterDied)]
    [DataRow(3, PgBackgroundWorkerStatus.Untracked)]
    [DataRow(5, PgBackgroundWorkerStatus.PostmasterDied)]
    [DataRow(5, PgBackgroundWorkerStatus.Untracked)]
    public void WorkerWaitsPreserveOutcomes(int operation, PgBackgroundWorkerStatus status)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        fixture.Handler = request => request._flags == 1 ? new() { _context = 71, _value = 1 } : new() { _value = (nint)status };
        Assert.IsTrue(PgBackgroundWorker.TryStart(new("worker", "library", "Run"), out PgBackgroundWorkerHandle? handle));
        using (handle)
        {
            Assert.AreEqual(new(status, null), Observe(handle, operation));
        }
    }

    /// <summary>
    /// Provider/thread checks precede native access, and callback exit releases forgotten handles without termination.
    /// </summary>
    [TestMethod]
    public void WorkerHandlesValidateOwnership()
    {
        using var fixture = new MemoryContextTestFixture();
        fixture.Handler = request => request._flags == 1 ? new() { _context = 71, _value = 1 } : default;
        PgBackgroundWorkerHandle retained;
        using (MemoryContextTestFixture.Enter())
        {
            Assert.IsTrue(PgBackgroundWorker.TryStart(new("worker", "library", "Run"), out PgBackgroundWorkerHandle? handle));
            retained = handle;
            using (MemoryContextTestFixture.Enter(23))
            {
                Assert.ThrowsExactly<InvalidOperationException>(() => handle.GetState());
                Assert.ThrowsExactly<InvalidOperationException>(handle.Dispose);
            }

            Assert.Throws<InvalidOperationException>(() => DedicatedThread.Run(handle.Terminate));
            Assert.AreSequenceEqual([1], fixture.Requests.Select(static request => request._flags));
        }

        Assert.ThrowsExactly<ObjectDisposedException>(() => retained.WaitForStartup());
        retained.Dispose();
        Assert.AreSequenceEqual([1, 6], fixture.Requests.Select(static request => request._flags));
    }

    /// <summary>
    /// UTF-8 identity copies, nullable connection names and maximum OIDs retain their exact meaning.
    /// </summary>
    [TestMethod]
    public void WorkerContextPreservesIdentityAndConnections()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        List<(string? Database, string? User)> names = [];
        fixture.Handler = request =>
        {
            if (request._flags == 7)
            {
                string text = request._value switch { 0 => "name café", 1 => "type π", _ => "extra 🐘" };
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                bytes.CopyTo(new Span<byte>((void*)request._data, checked((int)request._length)));
                return new() { _length = (nuint)bytes.Length };
            }

            if (request._flags == 11)
            {
                names.Add((Marshal.PtrToStringUTF8(request._context), Marshal.PtrToStringUTF8(request._pointer)));
            }

            return default;
        };
        Assert.AreEqual("name café", PgBackgroundWorker.Name);
        Assert.AreEqual("type π", PgBackgroundWorker.Type);
        Assert.AreEqual("extra 🐘", PgBackgroundWorker.Extra);
        PgBackgroundWorker.Connect(null, null);
        PgBackgroundWorker.Connect(string.Empty, "role π");
        PgBackgroundWorker.Connect("database café", string.Empty);
        Assert.AreSequenceEqual([(null, null), (string.Empty, "role π"), ("database café", string.Empty)], names);
        PgBackgroundWorker.Connect(uint.MaxValue, uint.MaxValue - 1);
        NativeMemoryRequest oids = fixture.Requests[^1];
        Assert.AreEqual(12, oids._flags);
        Assert.AreEqual(unchecked((nint)uint.MaxValue), oids._context);
        Assert.AreEqual(unchecked((nint)(uint.MaxValue - 1)), oids._value);
        int before = fixture.Requests.Count;
        Assert.ThrowsExactly<ArgumentException>(() => PgBackgroundWorker.Connect("a\0b"));
        Assert.ThrowsExactly<EncoderFallbackException>(() => PgBackgroundWorker.Connect(null, "bad\uD800"));
        Assert.HasCount(before, fixture.Requests);
    }

    /// <summary>
    /// Signal masks and timing units remain exact, with malformed replies and fractional intervals rejected.
    /// </summary>
    [TestMethod]
    public void WorkerSignalsAndTimeoutsPreserveBoundaries()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        fixture.Handler = request => new() { _value = request._flags == 9 ? request._value : request._flags is 10 or 14 ? 1 : 0 };
        PgBackgroundWorker.AttachSignalHandlers(PgBackgroundWorkerSignals.Interrupt | PgBackgroundWorkerSignals.Child);
        Assert.AreEqual(PgBackgroundWorkerSignals.Reload | PgBackgroundWorkerSignals.Terminate,
            PgBackgroundWorker.ConsumeSignals(PgBackgroundWorkerSignals.Reload | PgBackgroundWorkerSignals.Terminate));
        Assert.IsTrue(PgBackgroundWorker.CanContinue);
        Assert.IsTrue(PgBackgroundWorker.Wait());
        Assert.IsTrue(PgBackgroundWorker.Wait(TimeSpan.Zero));
        Assert.IsTrue(PgBackgroundWorker.Wait(TimeSpan.FromMilliseconds(int.MaxValue)));
        Assert.AreSequenceEqual([-1, 0, int.MaxValue], fixture.Requests.Where(static request => request._flags == 10).Select(static request => (int)request._value));
        int before = fixture.Requests.Count;
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PgBackgroundWorker.Wait(TimeSpan.FromTicks(1)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PgBackgroundWorker.Wait(TimeSpan.FromMilliseconds(-1)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PgBackgroundWorker.Wait(TimeSpan.FromMilliseconds((long)int.MaxValue + 1)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PgBackgroundWorker.ConsumeSignals((PgBackgroundWorkerSignals)16));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PgBackgroundWorker.AttachSignalHandlers((PgBackgroundWorkerSignals)(-1)));
        Assert.HasCount(before, fixture.Requests);
        fixture.Handler = static _ => new() { _value = 2, _length = 4097 };
        Assert.ThrowsExactly<InvalidOperationException>(() => PgBackgroundWorker.Wait(TimeSpan.Zero));
        Assert.ThrowsExactly<InvalidOperationException>(() => PgBackgroundWorker.ConsumeSignals(PgBackgroundWorkerSignals.Reload));
        Assert.ThrowsExactly<InvalidOperationException>(() => PgBackgroundWorker.Name);
        fixture.Handler = static _ => default;
        Assert.IsFalse(PgBackgroundWorker.Wait(TimeSpan.Zero));
        Assert.AreEqual(PgBackgroundWorkerSignals.None, PgBackgroundWorker.ConsumeSignals());
        PgBackgroundWorker.ReloadConfiguration();
        Assert.AreEqual(15, fixture.Requests[^1]._flags);
    }

    /// <summary>
    /// Transaction results and original managed failures return after native completion and restore both capabilities.
    /// </summary>
    [TestMethod]
    public void WorkerTransactionsPreserveBoundaries()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        List<int> outcomes = [];
        fixture.Handler = request =>
        {
            Assert.AreEqual(13, request._flags);
            using MemoryContextTestFixture.Scope inner = MemoryContextTestFixture.Enter(23);
            outcomes.Add(((delegate* unmanaged[Cdecl]<nint, nint, int>)request._pointer)(71, inner.Address));
            return default;
        };
        object expected = new();
        Assert.AreSame(expected, PgBackgroundWorker.RunTransaction(() =>
        {
            NativeBackend.CheckAccess(71);
            Assert.AreEqual(23, NativeMemoryContext.Provider);
            Assert.ThrowsExactly<InvalidOperationException>(() => PgBackgroundWorker.RunTransaction(static () => 13));
            return expected;
        }));
        var failure = new FormatException("owned callback failure");
        bool unwound = false;
        FormatException actual = Assert.ThrowsExactly<FormatException>(() => PgBackgroundWorker.RunTransaction<int>(() =>
        {
            try
            {
                throw failure;
            }
            finally
            {
                unwound = true;
            }
        }));
        Assert.AreSame(failure, actual);
        Assert.IsTrue(unwound);
        Assert.AreSequenceEqual([0, 1], outcomes);
        Assert.AreEqual(17, NativeMemoryContext.Provider);
        Assert.ThrowsExactly<InvalidOperationException>(() => Spi.Execute("SELECT 42"));
        Assert.AreEqual(47, PgBackgroundWorker.RunTransaction(static () => 47));
        bool executed = false;
        PgBackgroundWorker.RunTransaction(() =>
        {
            executed = true;
        });
        Assert.IsTrue(executed);
        Assert.AreSequenceEqual([0, 1, 0, 0], outcomes);
    }

    /// <summary>
    /// Missing callbacks, duplicate native invocation and invalid bindings do not publish a result or retain frame state.
    /// </summary>
    /// <param name="scenario">The malformed native transaction boundary.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public void WorkerTransactionsRejectInvalidCallbacks(int scenario)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        int calls = 0;
        fixture.Handler = request =>
        {
            if (scenario != 0)
            {
                var callback = (delegate* unmanaged[Cdecl]<nint, nint, int>)request._pointer;
                _ = callback(scenario == 1 ? 0 : 71, scenario == 2 ? 0 : scope.Address);
                if (scenario == 3)
                {
                    Assert.AreEqual(1, callback(71, scope.Address));
                }
            }

            return default;
        };
        Assert.ThrowsExactly<InvalidOperationException>(() => PgBackgroundWorker.RunTransaction(() => ++calls));
        Assert.AreEqual(scenario == 3 ? 1 : 0, calls);
        Assert.AreEqual(17, NativeMemoryContext.Provider);
        Assert.ThrowsExactly<ArgumentNullException>(() => PgBackgroundWorker.RunTransaction<int>(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => PgBackgroundWorker.RunTransaction(null!));
    }

    /// <summary>
    /// Owned native errors release every diagnostic buffer and leave registration, observation and transaction APIs usable.
    /// </summary>
    /// <param name="scenario">Registration, observation, transaction startup or transaction commit failure.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public void WorkerOperationsPreserveOwnedErrors(int scenario)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        var native = new PgException("P7853", new string('x', 4096), "detail café", "retry safely");
        bool fail = true;
        int callbacks = 0;
        fixture.Handler = request =>
        {
            if (request._flags == 1)
            {
                return fail && scenario == 0 ? throw native : new() { _value = 1, _context = 71 };
            }

            if (request._flags == 2)
            {
                return fail ? throw native : new() { _value = 0, _pointer = 313 };
            }

            if (request._flags == 13)
            {
                if (fail && scenario == 2)
                {
                    throw native;
                }

                Assert.AreEqual(0, ((delegate* unmanaged[Cdecl]<nint, nint, int>)request._pointer)(71, scope.Address));
                if (fail)
                {
                    throw native;
                }
            }

            return default;
        };
        // codeql[cs/missed-using-statement] Each scenario starts the worker at a different point, so the handle is disposed when the test ends.
        PgBackgroundWorkerHandle? handle = null;
        try
        {
            if (scenario == 1)
            {
                Assert.IsTrue(PgBackgroundWorker.TryStart(new("worker", "library", "Run"), out handle));
            }

            PgException error = Assert.ThrowsExactly<PgException>(() =>
            {
                if (scenario == 0)
                {
                    PgBackgroundWorker.TryStart(new("worker", "library", "Run"), out _);
                }
                else if (scenario == 1)
                {
                    handle!.GetState();
                }
                else
                {
                    PgBackgroundWorker.RunTransaction(() => ++callbacks);
                }
            });
            Assert.AreEqual("P7853", error.SqlState);
            Assert.AreEqual(new string('x', 4096), error.Message);
            Assert.AreEqual("detail café", error.Detail);
            Assert.AreEqual("retry safely", error.Hint);
            Assert.AreEqual(3, fixture.ErrorReleases);
            Assert.AreEqual(scenario == 3 ? 1 : 0, callbacks);
            Assert.AreEqual(17, NativeMemoryContext.Provider);
            fail = false;
            if (scenario == 0)
            {
                Assert.IsTrue(PgBackgroundWorker.TryStart(new("worker", "library", "Run"), out handle));
            }

            if (handle is not null)
            {
                Assert.AreEqual(new(PgBackgroundWorkerStatus.Started, 313), handle.GetState());
            }
            else
            {
                Assert.AreEqual(42, PgBackgroundWorker.RunTransaction(static () => 42));
            }
        }
        finally
        {
            handle?.Dispose();
        }
    }

    /// <summary>
    /// Identity text rejects malformed UTF-8 instead of silently replacing bytes, and later reads still work.
    /// </summary>
    [TestMethod]
    public void WorkerIdentityRejectsMalformedNativeText()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        fixture.Handler = request =>
        {
            ((byte*)request._data)[0] = 0xFF;
            return new() { _length = 1 };
        };
        Assert.ThrowsExactly<DecoderFallbackException>(() => PgBackgroundWorker.Name);
        fixture.Handler = request =>
        {
            ((byte*)request._data)[0] = (byte)'a';
            return new() { _length = 1 };
        };
        Assert.AreEqual("a", PgBackgroundWorker.Name);
        Assert.AreSequenceEqual([7, 7], fixture.Requests.Select(static request => request._flags));
    }

    /// <summary>
    /// Selects the public observation API without inspecting the lease implementation.
    /// </summary>
    private static PgBackgroundWorkerState Observe(PgBackgroundWorkerHandle handle, int operation) => operation switch
    {
        2 => handle.GetState(),
        3 => handle.WaitForStartup(),
        _ => new(handle.WaitForShutdown(), null),
    };
}
