using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Simplification;

namespace RoslynTraits {
  class BaseTypeRewriter : CSharpSyntaxRewriter {
    SyntaxToken rename(SyntaxToken from) {
      return SyntaxFactory.Identifier("T" + from.Text.Substring(1));
    }

    public override SyntaxToken VisitToken(SyntaxToken token) {
      if (!token.IsKind(SyntaxKind.IdentifierName)) return token;
      return rename(token);
    }

    public override SyntaxNode VisitGenericName(GenericNameSyntax node) {
      return node.WithIdentifier(VisitToken(node.Identifier));
    }

    public override SyntaxNode VisitQualifiedName(QualifiedNameSyntax node) {
      return node.WithRight((SimpleNameSyntax) Visit(node.Right));
    }
  }
}
