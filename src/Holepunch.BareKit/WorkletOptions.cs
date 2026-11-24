namespace Holepunch.BareKit;

/// <summary>
/// The options of <see cref="Library.Start"/>.
/// </summary>
public sealed class WorkletOptions
{
    /// <summary>
    /// The arguments passed to the worklet as <c>Bare.argv</c>.
    /// </summary>
    public IReadOnlyList<string> Args { get; init; } = [];

    /// <summary>
    /// The source of the module, which is read from the filename if
    /// <see langword="null"/>.
    /// </summary>
    public byte[]? Source { get; init; }

    /// <summary>
    /// The memory limit of each JavaScript heap in bytes, which is inferred
    /// from the amount of physical memory of the device if 0.
    /// </summary>
    public nuint MemoryLimit { get; init; }

    /// <summary>
    /// The options that apply on Android.
    /// </summary>
    public AndroidOptions Android { get; init; } = new();
}

/// <summary>
/// The options of a worklet that apply on Android.
/// </summary>
public sealed class AndroidOptions
{
    /// <summary>
    /// The Java virtual machine, as a <c>JavaVM *</c>, which defaults to the
    /// one that loaded Bare Kit through <c>JNI_OnLoad()</c>.
    /// </summary>
    public nint JavaVM { get; init; }

    /// <summary>
    /// The application context, as a JNI reference to an
    /// <c>android.content.Context</c>, which need only be valid for the call.
    /// </summary>
    public nint Context { get; init; }
}
