namespace Ago.Platform.Kernel;

/// <summary>
/// Base for entity ids: <c>readonly record struct ConversationId(Guid Value) : StronglyTypedId</c>
/// is not how this is used - each product declares its own <c>readonly record struct</c> wrapping a
/// <see cref="Guid"/> directly (docs/conventions/coding-style.md). What this type buys is a single
/// place that documents *why* every id is its own struct instead of a bare <see cref="Guid"/>:
/// two parameters of the same underlying type that are not interchangeable is exactly the bug a
/// strongly-typed id makes uncompilable.
/// </summary>
public interface IStronglyTypedId
{
    Guid Value { get; }
}
