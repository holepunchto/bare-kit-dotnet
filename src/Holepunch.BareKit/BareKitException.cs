namespace Holepunch.BareKit;

/// <summary>
/// A call into Bare Kit that failed with <see cref="Status"/>.
/// </summary>
public sealed class BareKitException(int status) : Exception($"Bare Kit failed with status {status}")
{
    /// <summary>
    /// The status that the call failed with.
    /// </summary>
    public int Status { get; } = status;
}
