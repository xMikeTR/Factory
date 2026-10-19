using Factory.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Factory.S2.Quality;

/// The C# adapter: syntax only, no compilation. Every method, constructor, destructor and operator
/// with a body (block or expression) becomes one `FunctionMetrics` in the neutral format `Quality` judges.
/// Coverage is joined in afterwards (`Cobertura`), so this stays a pure function of the source text.
public static class CSharpMetrics
{
    public static IReadOnlyList<FunctionMetrics> Measure(string file, string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        return root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>()
            .Where(m => m.Body is not null || m.ExpressionBody is not null)
            .Select(m =>
            {
                var span = m.GetLocation().GetLineSpan();
                var first = span.StartLinePosition.Line + 1;
                var last = span.EndLinePosition.Line + 1;
                return new FunctionMetrics(file, QualifiedName(m), first, Cyclomatic(m), last - first + 1, null);
            })
            .ToList();
    }

    /// 1 + decision points in the body: branches, loops, catches, case labels and switch arms (not the
    /// default/discard), short-circuit operators, `??`, `?:`. Nested local functions count in their parent
    /// too: they are part of its reading cost.
    private static int Cyclomatic(SyntaxNode fn) =>
        1 + fn.DescendantNodes().Count(n => n switch
        {
            IfStatementSyntax or WhileStatementSyntax or DoStatementSyntax or ForStatementSyntax
                or ForEachStatementSyntax or CatchClauseSyntax or ConditionalExpressionSyntax or CaseSwitchLabelSyntax
                or CasePatternSwitchLabelSyntax => true,
            SwitchExpressionArmSyntax arm => arm.Pattern is not DiscardPatternSyntax,
            BinaryExpressionSyntax b => b.IsKind(SyntaxKind.LogicalAndExpression) || b.IsKind(SyntaxKind.LogicalOrExpression)
                || b.IsKind(SyntaxKind.CoalesceExpression),
            _ => false,
        });

    private static string QualifiedName(BaseMethodDeclarationSyntax m)
    {
        var name = m switch
        {
            MethodDeclarationSyntax md => md.Identifier.Text,
            ConstructorDeclarationSyntax => ".ctor",
            DestructorDeclarationSyntax => ".dtor",
            OperatorDeclarationSyntax op => "op_" + op.OperatorToken.Text,
            ConversionOperatorDeclarationSyntax => "op_Conversion",
            _ => "?",
        };
        var scopes = m.Ancestors().Where(a => a is BaseTypeDeclarationSyntax or BaseNamespaceDeclarationSyntax)
            .Reverse().Select(a => a switch
            {
                BaseTypeDeclarationSyntax t => t.Identifier.Text,
                BaseNamespaceDeclarationSyntax n => n.Name.ToString(),
                _ => "",
            });
        return string.Join(".", scopes.Append(name));
    }
}
