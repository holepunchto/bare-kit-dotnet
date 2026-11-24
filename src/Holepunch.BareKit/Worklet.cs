using System.Runtime.InteropServices;

namespace Holepunch.BareKit;

/// <summary>
/// A Bare runtime running on a thread of its own, connected to its
/// <c>Bare.IPC</c>.
/// </summary>
/// <remarks>
/// Reading yields the data that the worklet writes to <c>Bare.IPC</c> until it
/// ends its side of the connection, and writing sends data to it. One reader
/// and one writer may use the worklet at the same time.
/// <see cref="CompleteWrites"/> ends the data sent to the worklet, while
/// disposing closes the connection and terminates the worklet. Use
/// <see cref="Library.Start"/> to start one.
/// </remarks>
public sealed class Worklet : Stream
{
    private const int Readable = 0x1;
    private const int Writable = 0x2;

    private const int WouldBlock = -1;
    private const int Failed = -2;

    private readonly Library _lib;

    private readonly nint _handle;
    private readonly nint _ipc;
    private readonly nint _poll;

    private readonly SemaphoreSlim _reading = new(1, 1);
    private readonly SemaphoreSlim _writing = new(1, 1);

    private readonly Lock _lock = new();
    private int _events;
    private bool _disposed;

    private readonly SemaphoreSlim _readable = new(0, 1);
    private readonly SemaphoreSlim _writable = new(0, 1);

    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal unsafe Worklet(Library lib, string filename, WorkletOptions options)
    {
        _lib = lib;

        var native = new Library.Options { MemoryLimit = options.MemoryLimit };

        native.Platform[0] = options.Android.JavaVM;
        native.Platform[1] = options.Android.Context;

        nint handle, ipc, poll;

        Library.Check(lib.WorkletCreate(&native, &handle));

        var status = lib.IpcCreate(handle, &ipc);

        if (status != 0)
        {
            lib.WorkletDestroy(handle);

            throw new BareKitException(status);
        }

        status = lib.PollCreate(ipc, &poll);

        if (status != 0)
        {
            lib.IpcDestroy(ipc);
            lib.WorkletDestroy(handle);

            throw new BareKitException(status);
        }

        _handle = handle;
        _ipc = ipc;
        _poll = poll;

        Library.Polls[poll] = this;

        var exit = new GCHandle<Worklet>(this);

        lib.WorkletOnExit(handle, &Library.HandleExit, GCHandle<Worklet>.ToIntPtr(exit));

        status = Start(filename, options);

        if (status != 0)
        {
            exit.Dispose();

            Dispose();

            throw new BareKitException(status);
        }
    }

    private unsafe int Start(string filename, WorkletOptions options)
    {
        var args = options.Args;
        var argv = new nint[args.Count + 1];
        var name = Marshal.StringToCoTaskMemUTF8(filename);

        try
        {
            for (var i = 0; i < args.Count; i++) argv[i] = Marshal.StringToCoTaskMemUTF8(args[i]);

            fixed (nint* pointers = argv)
            fixed (byte* source = options.Source)
            {
                return _lib.WorkletStart(_handle, (byte*)name, source, (nuint)(options.Source?.Length ?? 0), args.Count, (byte**)pointers);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(name);

            foreach (var arg in argv) Marshal.FreeCoTaskMem(arg);
        }
    }

    /// <inheritdoc/>
    public override bool CanRead => !_disposed;

    /// <inheritdoc/>
    public override bool CanWrite => !_disposed;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void Flush()
    {
    }

    /// <summary>
    /// Reads data that the worklet has written to <c>Bare.IPC</c>, returning 0
    /// once it has ended its side of the connection.
    /// </summary>
    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty) return 0;

        _reading.Wait();

        try
        {
            int read;

            while ((read = TryRead(buffer)) == WouldBlock) _readable.Wait();

            return read;
        }
        finally
        {
            Disarm(Readable);

            _reading.Release();
        }
    }

    /// <inheritdoc cref="Read(Span{byte})"/>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty) return 0;

        await _reading.WaitAsync(cancellationToken);

        try
        {
            int read;

            while ((read = TryRead(buffer.Span)) == WouldBlock) await _readable.WaitAsync(cancellationToken);

            return read;
        }
        finally
        {
            Disarm(Readable);

            _reading.Release();
        }
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);

        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc/>
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);

        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <summary>
    /// Sends data to <c>Bare.IPC</c> of the worklet, throwing an
    /// <see cref="IOException"/> once the connection has ended or closed.
    /// </summary>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _writing.Wait();

        try
        {
            while (!buffer.IsEmpty)
            {
                var written = TryWrite(buffer);

                if (written == WouldBlock) _writable.Wait();
                else buffer = buffer[written..];
            }
        }
        finally
        {
            Disarm(Writable);

            _writing.Release();
        }
    }

    /// <inheritdoc cref="Write(ReadOnlySpan{byte})"/>
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _writing.WaitAsync(cancellationToken);

        try
        {
            while (!buffer.IsEmpty)
            {
                var written = TryWrite(buffer.Span);

                if (written == WouldBlock) await _writable.WaitAsync(cancellationToken);
                else buffer = buffer[written..];
            }
        }
        finally
        {
            Disarm(Writable);

            _writing.Release();
        }
    }

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);

        Write(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc/>
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);

        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <summary>
    /// Ends the data sent to the worklet, after which <c>Bare.IPC</c> reads
    /// end-of-stream while reading continues.
    /// </summary>
    public unsafe void CompleteWrites()
    {
        _writing.Wait();

        try
        {
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                _lib.IpcEnd(_ipc);
            }
        }
        finally
        {
            _writing.Release();
        }
    }

    /// <summary>
    /// Waits for the worklet to exit and returns its exit code.
    /// </summary>
    public Task<int> WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        return _exit.Task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Suspends the worklet, emitting <c>suspend</c> on <c>Bare</c>. It keeps
    /// running for <paramref name="linger"/> before going idle, or for as long
    /// as the platform allows if <see langword="null"/>.
    /// </summary>
    public unsafe void Suspend(TimeSpan? linger = null)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            Library.Check(_lib.WorkletSuspend(_handle, Milliseconds(linger)));
        }
    }

    /// <summary>
    /// Resumes a suspended worklet, emitting <c>resume</c> on <c>Bare</c>.
    /// </summary>
    public unsafe void Resume()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            Library.Check(_lib.WorkletResume(_handle));
        }
    }

    /// <summary>
    /// Wakes up a suspended worklet, emitting <c>wakeup</c> on <c>Bare</c>. It
    /// runs for <paramref name="deadline"/> before going idle again, or for as
    /// long as the platform allows if <see langword="null"/>.
    /// </summary>
    public unsafe void Wakeup(TimeSpan? deadline = null)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            Library.Check(_lib.WorkletWakeup(_handle, Milliseconds(deadline)));
        }
    }

    /// <summary>
    /// Asks the worklet to exit unless it has already exited or been disposed.
    /// </summary>
    public unsafe void Terminate()
    {
        lock (_lock)
        {
            if (_disposed) return;

            Library.Check(_lib.WorkletTerminate(_handle));
        }
    }

    /// <summary>
    /// Closes the connection, terminating the worklet if it is still running,
    /// and releases its resources. Afterwards, <see cref="Terminate"/> does
    /// nothing and the other methods throw <see cref="ObjectDisposedException"/>,
    /// while <see cref="WaitForExitAsync"/> still completes once the worklet has
    /// exited.
    /// </summary>
    protected override unsafe void Dispose(bool disposing)
    {
        lock (_lock)
        {
            if (_disposed) return;

            _disposed = true;

            _lib.IpcClose(_ipc);
        }

        Notify(_readable);
        Notify(_writable);

        _lib.PollDestroy(_poll);

        Library.Polls.TryRemove(KeyValuePair.Create(_poll, this));

        _lib.IpcDestroy(_ipc);
        _lib.WorkletDestroy(_handle);

        base.Dispose(disposing);
    }

    internal void OnPoll(int events)
    {
        if ((events & Readable) != 0) Notify(_readable);
        if ((events & Writable) != 0) Notify(_writable);
    }

    internal void OnExit(int code)
    {
        _exit.TrySetResult(code);
    }

    private unsafe int TryRead(Span<byte> buffer)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            int read;

            fixed (byte* data = buffer) read = _lib.IpcRead(_ipc, data, (nuint)buffer.Length);

            Update(Readable, read == WouldBlock);

            return read;
        }
    }

    private unsafe int TryWrite(ReadOnlySpan<byte> buffer)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            int written;

            fixed (byte* data = buffer) written = _lib.IpcWrite(_ipc, data, (nuint)buffer.Length);

            if (written == Failed) throw new IOException("The connection to the worklet has ended or closed");

            Update(Writable, written == WouldBlock);

            return written;
        }
    }

    private void Disarm(int events)
    {
        lock (_lock) Update(events, false);
    }

    // Polls are level-triggered, so each event is only polled for while a
    // reader or writer waits for it.
    private unsafe void Update(int events, bool enabled)
    {
        var next = enabled ? _events | events : _events & ~events;

        if (_disposed || next == _events) return;

        _events = next;

        if (next == 0) _lib.PollStop(_poll);
        else _lib.PollStart(_poll, next, &Library.HandlePoll);
    }

    private static void Notify(SemaphoreSlim signal)
    {
        if (signal.CurrentCount != 0) return;

        try
        {
            signal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private static int Milliseconds(TimeSpan? duration)
    {
        if (duration is not { } value) return -1;

        return (int)Math.Clamp(value.TotalMilliseconds, 0, int.MaxValue);
    }
}
