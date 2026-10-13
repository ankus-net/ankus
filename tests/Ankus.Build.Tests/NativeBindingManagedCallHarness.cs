namespace Ankus.Build.Tests;

/// <summary>
/// Supplies a scoped ABI witness and a counting real allocator for compiled generated-call tests.
/// </summary>
internal static class NativeBindingManagedCallHarness
{
    /// <summary>
    /// Uses only public generated-code contracts; the memory envelope is independently represented at its native ABI.
    /// </summary>
    internal const string Source = """
        using System;
        using System.Collections.Generic;
        using System.Runtime.CompilerServices;
        using System.Runtime.InteropServices;
        using System.Text;
        using Ankus;
        using Ankus.CompilerServices;
        using Ankus.Postgres;

        public static unsafe class NativeCallTestBridge
        {
            // The API pointer every call receives identifies its scope, as it identifies the native context in an
            // extension; this avoids thread statics in the collectible test assembly.
            private static readonly Dictionary<nint, Scope> scopes = [];
            public static int Accessors;
            public sealed class Scope : IDisposable
            {
                private readonly Api* api;
                private readonly nint previous;
                public string Identity = "__EXPECTED_IDENTITY__";
                public bool RejectCall;
                public int Validations;
                public int Invocations;
                public Scope()
                {
                    api = (Api*)NativeMemory.Alloc((nuint)sizeof(Api));
                    *api = new Api { Provider = 17, Current = 19, Invoke = &Invoke, ResultContext = 23 };
                    lock (scopes) scopes.Add((nint)api, this);
                    previous = NativeMemoryContext.Enter((nint)api);
                }
                public void Dispose()
                {
                    NativeMemoryContext.Exit(previous);
                    lock (scopes) scopes.Remove((nint)api);
                    NativeMemory.Free(api);
                }
            }
            [StructLayout(LayoutKind.Sequential)]
            public struct Api
            {
                public nint Provider, Current;
                public delegate* unmanaged[Cdecl]<nint, Request*, Result*, NativeCallError*, int> Invoke;
                public nint ResultContext;
                // The rest of the runtime's layout, left empty: no interrupt flags and no direct reads.
                public int* InterruptPending, FailedFrames, SignalQueue, SignalMask;
                public int DirectReads;
            }
            [StructLayout(LayoutKind.Sequential)]
            public struct Request
            {
                public int Operation, Flags;
                public nint Context, Other, Pointer, Data;
                public nuint Length, Alignment;
                public nint Value;
            }
            [StructLayout(LayoutKind.Sequential)]
            public struct Result
            {
                public nint Context, Pointer, Data;
                public nuint Length;
                public nint Value;
            }
            [StructLayout(LayoutKind.Sequential)]
            public struct Frame
            {
                public NativeCallArgument* Arguments;
                public nuint Count;
                public void* Result;
                public nuint ResultSize;
            }
            [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
            private static int Invoke(nint api, Request* request, Result* result, NativeCallError* error)
            {
                try
                {
                    *result = default;
                    Scope state;
                    lock (scopes) state = scopes[api];
                    if (request->Operation == 32)
                    {
                        state.Validations++;
                        string identity = Encoding.UTF8.GetString((byte*)request->Data, checked((int)request->Length));
                        if (identity != state.Identity || request->Value != 18) throw new PgException("0A000", "native binding rejected");
                        return 0;
                    }
                    if (request->Operation != 34) throw new InvalidOperationException("Unexpected native operation.");
                    state.Invocations++;
                    if (state.RejectCall) throw new PgException("22023", "native call café", "detail naïve", "hint déjà");
                    Frame* frame = (Frame*)request->Data;
                    result->Value = ((delegate* unmanaged[Cdecl]<NativeCallArgument*, nuint, void*, nuint, int>)request->Pointer)
                        (frame->Arguments, frame->Count, frame->Result, frame->ResultSize);
                    return 0;
                }
                catch (Exception failure) { NativeError.Write(failure, error); return 1; }
            }
            public static class Allocator
            {
                private static readonly HashSet<nint> live = [];
                public static int Allocations, Releases;
                public static bool Reject;
                public static int Live => live.Count;
                public static void* Alloc(nuint size)
                {
                    if (Reject) throw new OutOfMemoryException("test allocation rejected");
                    void* value = NativeMemory.Alloc(size);
                    if (!live.Add((nint)value)) throw new InvalidOperationException("Allocation already owned.");
                    Allocations++;
                    return value;
                }
                public static void Free(void* value)
                {
                    if (!live.Remove((nint)value)) throw new InvalidOperationException("Allocation not owned.");
                    Releases++;
                    NativeMemory.Free(value);
                }
            }
        }
        """;
}
