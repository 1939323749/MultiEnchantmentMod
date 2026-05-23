using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;

namespace MultiEnchantmentMod.Analyzers;

/// <summary>
/// Code-fix for MEM007: adds <c>[assembly: EnchantmentApiCompatibility(2)]</c> to an existing
/// file that already contains assembly-level attributes, or to the first syntax tree otherwise.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(MissingCompatibilityAttributeCodeFixProvider))]
[Shared]
public sealed class MissingCompatibilityAttributeCodeFixProvider : CodeFixProvider
{
    private const string Title = "Add [assembly: EnchantmentApiCompatibility] attribute";

    public override ImmutableArray<string> FixableDiagnosticIds =>
        ImmutableArray.Create(DiagnosticIds.MissingCompatibilityAttribute);

    public override FixAllProvider? GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        Diagnostic diagnostic = context.Diagnostics.First();

        context.RegisterCodeFix(
            CodeAction.Create(
                title: Title,
                createChangedSolution: ct => AddCompatibilityAttributeAsync(context.Document, ct),
                equivalenceKey: Title),
            diagnostic);

        return Task.CompletedTask;
    }

    private static async Task<Solution> AddCompatibilityAttributeAsync(Document document, CancellationToken cancellationToken)
    {
        SyntaxNode? root = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false) is { } tree
            ? await tree.GetRootAsync(cancellationToken).ConfigureAwait(false)
            : null;
        if (root == null)
        {
            return document.Project.Solution;
        }

        // Determine the target document: prefer a file that already has assembly-level attributes
        // (e.g. AssemblyInfo.cs). Fall back to the current document.
        Document targetDocument = document;
        SyntaxNode targetRoot = root;
        foreach (Document doc in document.Project.Documents)
        {
            SyntaxNode? docRoot = await doc.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false) is { } docTree
                ? await docTree.GetRootAsync(cancellationToken).ConfigureAwait(false)
                : null;
            if (docRoot == null)
            {
                continue;
            }

            bool hasAssemblyAttributes = docRoot.DescendantNodes()
                .OfType<AttributeListSyntax>()
                .Any(list => list.Target?.Identifier.IsKind(SyntaxKind.AssemblyKeyword) == true);

            if (hasAssemblyAttributes)
            {
                targetDocument = doc;
                targetRoot = docRoot;
                break;
            }
        }

        // Build: [assembly: EnchantmentApiCompatibility(2)]
        AttributeListSyntax newAttributeList = SyntaxFactory.AttributeList(
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Attribute(
                            SyntaxFactory.ParseName("EnchantmentApiCompatibility"))
                        .WithArgumentList(
                            SyntaxFactory.AttributeArgumentList(
                                SyntaxFactory.SingletonSeparatedList(
                                    SyntaxFactory.AttributeArgument(
                                        SyntaxFactory.LiteralExpression(
                                            SyntaxKind.NumericLiteralExpression,
                                            SyntaxFactory.Literal(2))))))))
            .WithTarget(
                SyntaxFactory.AttributeTargetSpecifier(
                    SyntaxFactory.Token(SyntaxKind.AssemblyKeyword)))
            .NormalizeWhitespace()
            .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);

        // Ensure using directive exists.
        CompilationUnitSyntax compilationUnit = (CompilationUnitSyntax)targetRoot;
        const string requiredNamespace = "MultiEnchantmentMod.Api";

        bool hasUsing = compilationUnit.Usings.Any(u => u.Name?.ToString() == requiredNamespace);
        if (!hasUsing)
        {
            UsingDirectiveSyntax usingDirective = SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(requiredNamespace))
                .NormalizeWhitespace()
                .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);
            compilationUnit = compilationUnit.AddUsings(usingDirective);
        }

        // Insert after existing assembly attributes, or at the top of the compilation unit.
        AttributeListSyntax? lastAssemblyAttribute = compilationUnit.AttributeLists
            .LastOrDefault(list => list.Target?.Identifier.IsKind(SyntaxKind.AssemblyKeyword) == true);

        if (lastAssemblyAttribute != null)
        {
            compilationUnit = compilationUnit.InsertNodesAfter(lastAssemblyAttribute, new[] { newAttributeList });
        }
        else
        {
            compilationUnit = compilationUnit.WithAttributeLists(
                compilationUnit.AttributeLists.Add(newAttributeList));
        }

        return targetDocument.Project.Solution.WithDocumentSyntaxRoot(targetDocument.Id, compilationUnit);
    }
}
