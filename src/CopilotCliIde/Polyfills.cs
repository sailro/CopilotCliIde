// Polyfills for C# language features that target newer BCLs but only require
// internal sentinel types. .NET Framework 4.7.2 doesn't ship these — they're
// declared here so we can use `record`, `init`, and `required` in this assembly.
namespace System.Runtime.CompilerServices
{
	internal static class IsExternalInit { }

	[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
	internal sealed class RequiredMemberAttribute : Attribute { }

	[AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
	internal sealed class CompilerFeatureRequiredAttribute(string featureName) : Attribute
	{
		public string FeatureName { get; } = featureName;
		public bool IsOptional { get; init; }
	}
}

namespace System.Diagnostics.CodeAnalysis
{
	[AttributeUsage(AttributeTargets.Constructor, AllowMultiple = false, Inherited = false)]
	internal sealed class SetsRequiredMembersAttribute : Attribute { }
}
