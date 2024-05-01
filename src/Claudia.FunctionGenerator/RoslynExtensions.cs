﻿using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.CodeAnalysis.CSharp;

namespace Claudia.FunctionGenerator;

internal static class RoslynExtensions
{
    public static DocumentationCommentTriviaSyntax? GetDocumentationCommentTriviaSyntax(this SyntaxNode node)
    {
        // Hack note:
        // ISymbol.GetDocumentationCommentXml requires<GenerateDocumentationFile>true </>.
        // However, getting the DocumentationCommentTrivia of a SyntaxNode also requires the same condition.
        // It can only be obtained when DocumentationMode is Parse or Diagnostic, but when<GenerateDocumentationFile>false </>,
        // it becomes None, and the necessary Trivia cannot be obtained.
        // Therefore, we will attempt to reparse and retrieve it.

        // About DocumentationMode and Trivia: https://github.com/dotnet/roslyn/issues/58210
        if (node.SyntaxTree.Options.DocumentationMode == DocumentationMode.None)
        {
            var withDocumentationComment = node.SyntaxTree.Options.WithDocumentationMode(DocumentationMode.Parse);
            var code = node.ToFullString();
            var newTree = CSharpSyntaxTree.ParseText(code, (CSharpParseOptions)withDocumentationComment);
            node = newTree.GetRoot();
        }

        foreach (var leadingTrivia in node.GetLeadingTrivia())
        {
            if (leadingTrivia.GetStructure() is DocumentationCommentTriviaSyntax structure)
            {
                return structure;
            }
        }

        return null;
    }

    static IEnumerable<XmlNodeSyntax> GetXmlElements(this SyntaxList<XmlNodeSyntax> content, string elementName)
    {
        foreach (XmlNodeSyntax syntax in content)
        {
            if (syntax is XmlEmptyElementSyntax emptyElement)
            {
                if (string.Equals(elementName, emptyElement.Name.ToString(), StringComparison.Ordinal))
                {
                    yield return emptyElement;
                }

                continue;
            }

            if (syntax is XmlElementSyntax elementSyntax)
            {
                if (string.Equals(elementName, elementSyntax.StartTag?.Name?.ToString(), StringComparison.Ordinal))
                {
                    yield return elementSyntax;
                }

                continue;
            }
        }
    }

    public static string GetSummary(this DocumentationCommentTriviaSyntax docComment)
    {
        var summary = docComment.Content.GetXmlElements("summary").FirstOrDefault() as XmlElementSyntax;
        if (summary == null) return "";

        return summary.Content.ToString().Replace("///", "").Trim();
    }

    public static IEnumerable<(string Name, string Description)> GetParams(this DocumentationCommentTriviaSyntax docComment)
    {
        foreach (var item in docComment.Content.GetXmlElements("param").OfType<XmlElementSyntax>())
        {
            var name = item.StartTag.Attributes.OfType<XmlNameAttributeSyntax>().FirstOrDefault()?.Identifier.Identifier.ValueText.Replace("///", "").Trim() ?? "";
            var desc = item.Content.ToString().Replace("///", "").Trim() ?? "";
            yield return (name, desc);
        }

        yield break;
    }
}
