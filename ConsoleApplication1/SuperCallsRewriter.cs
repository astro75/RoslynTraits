using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Simplification;

namespace RoslynTraits {
  class SuperCallsRewriter : CSharpSyntaxRewriter {
    Dictionary<string, int> replaces;

    public SuperCallsRewriter(Dictionary<string, int> replaces) : base(false) {
      this.replaces = replaces;
    }

    public override SyntaxNode VisitMemberAccessExpression(MemberAccessExpressionSyntax node) {
      if (node.Expression is BaseExpressionSyntax) {
        // base.xxxx
        var name = node.Name.Identifier.Text;
        var parentLevel = 0;
        if (replaces.ContainsKey(name)) {
          parentLevel = replaces[name];
        }
        if (parentLevel != 0)
          return SyntaxFactory.ParseName(seperCallOverrideText(parentLevel, name));
      }
      return base.VisitMemberAccessExpression(node);
    }

    public static string seperCallOverrideText(int parentLevel, string name) {
      return $"__super_{parentLevel}_{name}";
    }
  }
}
