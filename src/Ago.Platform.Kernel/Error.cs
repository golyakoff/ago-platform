namespace Ago.Platform.Kernel;

public readonly record struct Error(string Code, string Message)
{
    public override string ToString() => $"{Code}: {Message}";
}
