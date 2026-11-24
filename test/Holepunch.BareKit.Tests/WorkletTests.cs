using System.Text;
using Xunit;

namespace Holepunch.BareKit.Tests;

public sealed class WorkletTests : IDisposable
{
    private static readonly Lazy<Library> s_library = new(Library.Open);

    private readonly List<Worklet> _worklets = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Fixture(string name)
    {
        return Path.Combine(AppContext.BaseDirectory, "fixtures", name);
    }

    private Worklet Start(string filename, WorkletOptions? options = null)
    {
        var worklet = s_library.Value.Start(filename, options);

        _worklets.Add(worklet);

        return worklet;
    }

    private static async Task<string> ReadAllAsync(Worklet worklet)
    {
        using var reader = new StreamReader(worklet, leaveOpen: true);

        return await reader.ReadToEndAsync(Token);
    }

    public void Dispose()
    {
        foreach (var worklet in _worklets) worklet.Dispose();
    }

    [Fact]
    public async Task EchoesIpcData()
    {
        var worklet = Start(Fixture("echo.js"));

        await worklet.WriteAsync("Hello world"u8.ToArray(), Token);

        var data = new byte[11];

        await worklet.ReadExactlyAsync(data, Token);

        Assert.Equal("Hello world", Encoding.UTF8.GetString(data));
    }

    [Fact]
    public async Task EndsIpcData()
    {
        var worklet = Start(Fixture("collect.js"));

        worklet.Write("Hello "u8);
        worklet.Write("world"u8);
        worklet.CompleteWrites();

        Assert.Throws<IOException>(() => worklet.Write("!"u8));

        Assert.Equal("Hello world", await ReadAllAsync(worklet));
        Assert.Equal(0, await worklet.WaitForExitAsync(Token));
    }

    [Fact]
    public async Task PassesArguments()
    {
        var worklet = Start(Fixture("argv.js"), new WorkletOptions { Args = ["foo", "bar"] });

        Assert.Equal("""["foo","bar"]""", await ReadAllAsync(worklet));
    }

    [Fact]
    public async Task RunsSource()
    {
        var worklet = Start("/app.js", new WorkletOptions { Source = "Bare.IPC.end('Hello world')"u8.ToArray() });

        Assert.Equal("Hello world", await ReadAllAsync(worklet));
    }

    [Fact]
    public async Task ReportsTheExitCode()
    {
        var worklet = Start(Fixture("exit.js"));

        Assert.Equal(3, await worklet.WaitForExitAsync(Token));
        Assert.Equal("", await ReadAllAsync(worklet));
    }

    [Fact]
    public async Task Terminates()
    {
        var worklet = Start(Fixture("sleep.js"));

        worklet.Terminate();

        await worklet.WaitForExitAsync(Token);

        Assert.Equal("", await ReadAllAsync(worklet));
    }

    [Fact]
    public void IgnoresTerminateOnceDisposedAndRejectsOtherCalls()
    {
        var worklet = Start(Fixture("sleep.js"));

        worklet.Dispose();
        worklet.Terminate();

        Assert.Throws<ObjectDisposedException>(() => worklet.Suspend());
    }

    [Fact]
    public async Task SuspendsAndResumes()
    {
        var worklet = Start(Fixture("lifecycle.js"));

        var events = new List<string>();
        var data = new byte[64];

        while (events.Count < 3)
        {
            var read = await worklet.ReadAsync(data, Token);
            var name = Encoding.UTF8.GetString(data, 0, read);

            events.Add(name);

            if (name == "ready") worklet.Suspend(TimeSpan.Zero);
            else if (name == "suspend") worklet.Resume();
        }

        Assert.Equal(["ready", "suspend", "resume"], events);
    }

    [Fact]
    public async Task DisposesWhileReading()
    {
        var worklet = Start(Fixture("echo.js"));

        var read = worklet.ReadAsync(new byte[1], Token).AsTask();

        worklet.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => read);

        Assert.Throws<ObjectDisposedException>(() => worklet.Write("Hello world"u8));

        await worklet.WaitForExitAsync(Token);
    }

    [Fact]
    public async Task StreamsLargeData()
    {
        var data = new byte[256 * 4096];

        for (var i = 0; i < data.Length; i++) data[i] = (byte)i;

        var worklet = Start(Fixture("collect.js"));

        var written = Task.Run(async () =>
        {
            await worklet.WriteAsync(data, Token);

            worklet.CompleteWrites();
        }, Token);

        using var collected = new MemoryStream();

        await worklet.CopyToAsync(collected, Token);
        await written;

        Assert.Equal(data, collected.ToArray());
    }
}
