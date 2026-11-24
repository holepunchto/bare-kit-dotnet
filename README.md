# bare-kit-dotnet

.NET bindings for [Bare Kit](https://github.com/holepunchto/bare-kit), which runs Bare in a thread of its own within the application, known as a worklet, with an IPC to it exposed as `Bare.IPC` inside the worklet.

```
dotnet add package Holepunch.BareKit
```

## Usage

```csharp
using System.Text;
using Holepunch.BareKit;

var library = Library.Open();

using var worklet = library.Start(Path.Combine(AppContext.BaseDirectory, "app.bundle"));

await worklet.WriteAsync("Hello world"u8.ToArray());

using var reader = new StreamReader(worklet, Encoding.UTF8);

Console.WriteLine(await reader.ReadToEndAsync());
```

Where `app.bundle` is a Bare bundle using `Bare.IPC`:

```js
Bare.IPC.on('data', (data) => Bare.IPC.end(data))
```

A worklet is a `Stream`, and reading it yields the data that the worklet writes to `Bare.IPC` until it ends its side. As `Bare.IPC` keeps the worklet alive, `CompleteWrites()` ends the data sent to it so that it may exit on its own, and `WaitForExitAsync()` returns its exit code. Writes throw an `IOException` once the data sent has ended or the worklet has closed its side. Disposing a worklet terminates it, so keep it for as long as it should run. Once disposed, `Terminate()` does nothing and its other methods throw `ObjectDisposedException`.

These bindings are a thin layer over [Bare Kit](https://github.com/holepunchto/bare-kit), whose README covers the [lifecycle](https://github.com/holepunchto/bare-kit#lifecycle), [IPC](https://github.com/holepunchto/bare-kit#ipc), [suspension](https://github.com/holepunchto/bare-kit#suspension), [addons](https://github.com/holepunchto/bare-kit#addons), and [error handling](https://github.com/holepunchto/bare-kit#error-handling) of a worklet. In .NET terms:

- If `WorkletOptions.Source` is `null`, `Start()` reads `filename` at runtime, so the worklet may be a module next to its `node_modules` rather than a bundle.
- `Library.Open()` loads Bare Kit as a shared library, so native addons resolve the symbols of Bare from it as is.
- The asynchronous reads and writes wait without blocking a thread, and one reader and one writer may use the worklet at the same time.
- An uncaught error in the worklet aborts the process, the .NET runtime included, so report errors over `Bare.IPC` as described in [error handling](https://github.com/holepunchto/bare-kit#error-handling).

## Platforms

The package includes prebuilt Bare Kit libraries for Linux, macOS, and Windows on arm64 and x64, which `Library.Open()` loads for the running platform. To use a build of its own instead, such as on Android and iOS where the application bundles the library itself, an application passes its path to `Library.Open(path)`. On Android, the Java virtual machine and application context are passed with `WorkletOptions.Android`.

## License

Apache-2.0
