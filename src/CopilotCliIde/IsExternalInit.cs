// Polyfill for init-only properties on .NET Framework
#pragma warning disable IDE0130 // Namespace does not match folder structure
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
#pragma warning restore IDE0130
