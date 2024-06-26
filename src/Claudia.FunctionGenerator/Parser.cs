﻿using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Immutable;
using System.Xml.Linq;

namespace Claudia.FunctionGenerator;

public class Parser
{
    private SourceProductionContext context;
    private ImmutableArray<GeneratorAttributeSyntaxContext> sources;

    public Parser(SourceProductionContext context, ImmutableArray<GeneratorAttributeSyntaxContext> sources)
    {
        this.context = context;
        this.sources = sources;
    }

    internal ParseResult[] Parse()
    {
        var list = new List<ParseResult>();
        var methods = new List<Method>();

        // grouping by type(TypeDeclarationSyntax)
        foreach (var item in sources.GroupBy(x => x.TargetNode.Parent))
        {
            methods.Clear();

            if (item.Key == null) continue;
            var targetType = (TypeDeclarationSyntax)item.Key;
            var symbol = item.First().SemanticModel.GetDeclaredSymbol(targetType);
            if (symbol == null) continue;

            // verify is partial
            if (!IsPartial(targetType))
            {
                context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.MustBePartial, targetType.Identifier.GetLocation(), symbol.Name));
                continue;
            }

            // nested is not allowed
            if (IsNested(targetType))
            {
                context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.NestedNotAllow, targetType.Identifier.GetLocation(), symbol.Name));
                continue;
            }

            // verify is generis type
            if (symbol.TypeParameters.Length > 0)
            {
                context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.GenericTypeNotSupported, targetType.Identifier.GetLocation(), symbol.Name));
                continue;
            }

            if (!symbol.IsStatic)
            {
                context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.MustBeStatic, targetType.Identifier.GetLocation(), symbol.Name));
                continue;
            }

            var hasError = false;
            foreach (var source in item)
            {
                // source.TargetNode
                var method = (IMethodSymbol)source.TargetSymbol;

                var docComment = source.TargetNode.GetDocumentationCommentTriviaSyntax();
                if (docComment == null)
                {
                    context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.MethodNeedsDocumentationCommentXml, method.Locations[0], method.Name));
                    hasError = true;
                    continue;
                }
                else
                {
                    var description = docComment.GetSummary().Replace("\"", "'");
                    if (string.IsNullOrWhiteSpace(description))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.MethodNeedsSummary, method.Locations[0], method.Name));
                        hasError = true;
                        continue;
                    }

                    var parameterNames = new HashSet<string>(method.Parameters.Select(x => x.Name));
                    if (parameterNames.Count != 0)
                    {
                        foreach (var p in docComment.GetParams())
                        {
                            var desc = p.Description;
                            if (string.IsNullOrWhiteSpace(desc))
                            {
                                context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.ParameterNeedsDescription, method.Locations[0], method.Name, p.Name));
                                hasError = true;
                                continue;
                            }

                            var name = p.Name;
                            parameterNames.Remove(name);
                        }

                        if (parameterNames.Count != 0)
                        {
                            context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.AllParameterNeedsDescription, method.Locations[0], method.Name));
                            hasError = true;
                            continue;
                        }
                    }
                }

                foreach (var p in method.Parameters)
                {
                    // castable types
                    // https://learn.microsoft.com/en-us/dotnet/api/system.xml.linq.xelement?view=net-8.0
                    switch (p.Type.SpecialType)
                    {
                        case SpecialType.System_Boolean:
                        case SpecialType.System_SByte:
                        case SpecialType.System_Byte:
                        case SpecialType.System_Int16:
                        case SpecialType.System_UInt16:
                        case SpecialType.System_Int32:
                        case SpecialType.System_UInt32:
                        case SpecialType.System_Int64:
                        case SpecialType.System_UInt64:
                        case SpecialType.System_Decimal:
                        case SpecialType.System_Single:
                        case SpecialType.System_Double:
                        case SpecialType.System_String:
                        case SpecialType.System_DateTime:
                            break;
                        default:
                            if (p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) is "global::System.DateTimeOffset" or "global::System.Guid" or "global::System.TimeSpan")
                            {
                                break;
                            }
                            if (p.Type.TypeKind == TypeKind.Enum)
                            {
                                break;
                            }

                            hasError = true;
                            context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.ParameterTypeIsNotSupported, method.Locations[0], method.Name, p.Name, p.Type.Name));
                            continue;
                    }
                }

                if (method.ReturnsVoid || (method.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) is "global::System.Threading.Tasks.Task" or "global::System.Threading.Tasks.ValueTask"))
                {
                    context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.VoidReturnIsNotSupported, method.Locations[0], method.Name));
                    hasError = true;
                    continue;
                }

                methods.Add(new Method { Symbol = method, Syntax = (MethodDeclarationSyntax)source.TargetNode });
            }

            if (hasError)
            {
                continue;
            }

            list.Add(new ParseResult
            {
                TypeSyntax = targetType,
                TypeSymbol = symbol,
                Methods = methods.ToArray()
            });
        }

        return list.ToArray();
    }

    static bool IsPartial(TypeDeclarationSyntax typeDeclaration)
    {
        return typeDeclaration.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword));
    }

    static bool IsNested(TypeDeclarationSyntax typeDeclaration)
    {
        return typeDeclaration.Parent is TypeDeclarationSyntax;
    }
}
