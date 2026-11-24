using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Holepunch.BareKit;

/// <summary>
/// A loaded Bare Kit library, which stays loaded for as long as the process
/// lives.
/// </summary>
public sealed unsafe class Library
{
    // Polls are found by their handle, as their callback cannot tell which
    // library to read their data through, and destroying a poll waits for its
    // callback.
    internal static readonly ConcurrentDictionary<nint, Worklet> Polls = new();

    private readonly delegate* unmanaged<byte, int> _optimizeForMemory;
    private readonly delegate* unmanaged<int> _teardown;

    internal readonly delegate* unmanaged<Options*, nint*, int> WorkletCreate;
    internal readonly delegate* unmanaged<nint, void> WorkletDestroy;
    internal readonly delegate* unmanaged<nint, delegate* unmanaged<int, nint, void>, nint, int> WorkletOnExit;
    internal readonly delegate* unmanaged<nint, byte*, byte*, nuint, int, byte**, int> WorkletStart;
    internal readonly delegate* unmanaged<nint, int, int> WorkletSuspend;
    internal readonly delegate* unmanaged<nint, int> WorkletResume;
    internal readonly delegate* unmanaged<nint, int, int> WorkletWakeup;
    internal readonly delegate* unmanaged<nint, int> WorkletTerminate;

    internal readonly delegate* unmanaged<nint, nint*, int> IpcCreate;
    internal readonly delegate* unmanaged<nint, void> IpcClose;
    internal readonly delegate* unmanaged<nint, void> IpcEnd;
    internal readonly delegate* unmanaged<nint, void> IpcDestroy;
    internal readonly delegate* unmanaged<nint, byte*, nuint, int> IpcRead;
    internal readonly delegate* unmanaged<nint, byte*, nuint, int> IpcWrite;

    internal readonly delegate* unmanaged<nint, nint*, int> PollCreate;
    internal readonly delegate* unmanaged<nint, void> PollDestroy;
    internal readonly delegate* unmanaged<nint, int, delegate* unmanaged<nint, int, void>, int> PollStart;
    internal readonly delegate* unmanaged<nint, int> PollStop;

    private Library(nint handle)
    {
        nint Export(string name) => NativeLibrary.GetExport(handle, name);

        _optimizeForMemory = (delegate* unmanaged<byte, int>)Export("bare_worklet_optimize_for_memory");
        _teardown = (delegate* unmanaged<int>)Export("bare_worklet_teardown");

        WorkletCreate = (delegate* unmanaged<Options*, nint*, int>)Export("bare_worklet_create");
        WorkletDestroy = (delegate* unmanaged<nint, void>)Export("bare_worklet_destroy");
        WorkletOnExit = (delegate* unmanaged<nint, delegate* unmanaged<int, nint, void>, nint, int>)Export("bare_worklet_on_exit");
        WorkletStart = (delegate* unmanaged<nint, byte*, byte*, nuint, int, byte**, int>)Export("bare_worklet_start");
        WorkletSuspend = (delegate* unmanaged<nint, int, int>)Export("bare_worklet_suspend");
        WorkletResume = (delegate* unmanaged<nint, int>)Export("bare_worklet_resume");
        WorkletWakeup = (delegate* unmanaged<nint, int, int>)Export("bare_worklet_wakeup");
        WorkletTerminate = (delegate* unmanaged<nint, int>)Export("bare_worklet_terminate");

        IpcCreate = (delegate* unmanaged<nint, nint*, int>)Export("bare_ipc_create");
        IpcClose = (delegate* unmanaged<nint, void>)Export("bare_ipc_close");
        IpcEnd = (delegate* unmanaged<nint, void>)Export("bare_ipc_end");
        IpcDestroy = (delegate* unmanaged<nint, void>)Export("bare_ipc_destroy");
        IpcRead = (delegate* unmanaged<nint, byte*, nuint, int>)Export("bare_ipc_read");
        IpcWrite = (delegate* unmanaged<nint, byte*, nuint, int>)Export("bare_ipc_write");

        PollCreate = (delegate* unmanaged<nint, nint*, int>)Export("bare_ipc_poll_create");
        PollDestroy = (delegate* unmanaged<nint, void>)Export("bare_ipc_poll_destroy");
        PollStart = (delegate* unmanaged<nint, int, delegate* unmanaged<nint, int, void>, int>)Export("bare_ipc_poll_start");
        PollStop = (delegate* unmanaged<nint, int>)Export("bare_ipc_poll_stop");
    }

    /// <summary>
    /// Loads the Bare Kit library that this package provides for the running
    /// platform.
    /// </summary>
    public static Library Open()
    {
        return new Library(NativeLibrary.Load("bare-kit", typeof(Library).Assembly, null));
    }

    /// <summary>
    /// Loads the Bare Kit library at <paramref name="path"/>, such as a build
    /// of its own that the application bundles.
    /// </summary>
    public static Library Open(string path)
    {
        return new Library(NativeLibrary.Load(path));
    }

    /// <summary>
    /// Trades performance for memory. Must be configured before starting the
    /// first worklet.
    /// </summary>
    public void OptimizeForMemory(bool enabled)
    {
        Check(_optimizeForMemory(enabled ? (byte)1 : (byte)0));
    }

    /// <summary>
    /// Tears down the state shared by the worklets of the process.
    /// </summary>
    /// <remarks>
    /// Every worklet must have been disposed first. Must be called at most
    /// once, after which starting a worklet fails. A process that runs
    /// worklets for as long as it lives need not call it.
    /// </remarks>
    public void Teardown()
    {
        Check(_teardown());
    }

    /// <summary>
    /// Starts <paramref name="filename"/> in a worklet with an IPC connection
    /// to it.
    /// </summary>
    public Worklet Start(string filename, WorkletOptions? options = null)
    {
        return new Worklet(this, filename, options ?? new WorkletOptions());
    }

    internal static void Check(int status)
    {
        if (status != 0) throw new BareKitException(status);
    }

    [UnmanagedCallersOnly]
    internal static void HandleExit(int code, nint data)
    {
        var handle = GCHandle<Worklet>.FromIntPtr(data);

        handle.Target.OnExit(code);
        handle.Dispose();
    }

    [UnmanagedCallersOnly]
    internal static void HandlePoll(nint poll, int events)
    {
        if (Polls.TryGetValue(poll, out var worklet)) worklet.OnPoll(events);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Options
    {
        public int Version;
        public nuint MemoryLimit;
        public Platform Platform;
    }

    [InlineArray(8)]
    internal struct Platform
    {
        private nint _element;
    }
}
