using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Simplification;

namespace RoslynTraits {
  class GenericRewriter : CSharpSyntaxRewriter {
    readonly List<Tuple<string, string>> replaces;

    public GenericRewriter(List<Tuple<string, string>> replaces) : base(false) {
      this.replaces = replaces;
    }

    public override SyntaxNode VisitIdentifierName(IdentifierNameSyntax node) {
      var updated = replaces.FirstOrDefault(r => r.Item1 == node.Identifier.Text)?.Item2;
      return updated != null ? SyntaxFactory.IdentifierName(updated).WithAdditionalAnnotations(Simplifier.Annotation) : node;
    }
  }
}
