#if NETFRAMEWORK
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
namespace System.Runtime.Versioning
{
    [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
    internal sealed class SupportedOSPlatformAttribute(string platformName) : Attribute { public string PlatformName { get; } = platformName; }
    [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
    internal sealed class SupportedOSPlatformGuardAttribute(string platformName) : Attribute { public string PlatformName { get; } = platformName; }
}
#endif
