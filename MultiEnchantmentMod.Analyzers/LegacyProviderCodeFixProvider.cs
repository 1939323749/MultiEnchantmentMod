// This file previously contained a placeholder CodeFixProvider with no fixable diagnostics.
// Actual code-fix implementations now live in:
//   - MissingCompatibilityAttributeCodeFixProvider.cs  (MEM007)
//   - ModifyDynamicVarSignatureCodeFixProvider.cs       (MEM009)
//
// This stub is retained for backward compatibility — existing installations that reference the
// type name will not encounter a missing-type error. The provider simply reports nothing fixable.

using System.Collections.Immutable;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;

namespace MultiEnchantmentMod.Analyzers;

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(LegacyProviderCodeFixProvider))]
public sealed class LegacyProviderCodeFixProvider : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray<string>.Empty;

    public override FixAllProvider? GetFixAllProvider() => null;

    public override Task RegisterCodeFixesAsync(CodeFixContext context) => Task.CompletedTask;
}
