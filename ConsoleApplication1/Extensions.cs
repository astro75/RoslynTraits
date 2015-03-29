using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynTraits {
  internal static class Extensions {
    public static IEnumerable<Document> allDocs(this Solution sol) {
      return sol.Projects.SelectMany(s => s.Documents).Where(doc => !doc.Name.EndsWith(".generated.cs"));
    }

    public static bool has(this BasePropertyDeclarationSyntax decl, SyntaxKind kind) {
      return decl.Modifiers.has(kind);
    }

    public static bool has(this BaseMethodDeclarationSyntax decl, SyntaxKind kind) {
      return decl.Modifiers.has(kind);
    }

    public static bool has(this SyntaxTokenList tokens, SyntaxKind kind) {
      return tokens.Any(m => m.IsKind(kind));
    }

    public static bool hasNot(this BasePropertyDeclarationSyntax decl, SyntaxKind kind) {
      return decl.Modifiers.hasNot(kind);
    }

    public static bool hasNot(this BaseMethodDeclarationSyntax decl, SyntaxKind kind) {
      return decl.Modifiers.hasNot(kind);
    }

    public static bool hasNot(this SyntaxTokenList tokens, SyntaxKind kind) {
      return tokens.All(m => !m.IsKind(kind));
    }

    public static PropertyDeclarationSyntax remove(this PropertyDeclarationSyntax decl, SyntaxKind kind) {
      return decl.WithModifiers(decl.Modifiers.remove(kind));
    }

    public static MethodDeclarationSyntax remove(this MethodDeclarationSyntax decl, SyntaxKind kind) {
      return decl.WithModifiers(decl.Modifiers.remove(kind));
    }

    public static SyntaxTokenList remove(this SyntaxTokenList tokens, SyntaxKind kind) {
      return SyntaxFactory.TokenList(tokens.Where(m => !m.IsKind(kind)));
    }
  }
}
