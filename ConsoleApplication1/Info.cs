using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynTraits {
  class Info {
    public readonly SymbolId id;
    public readonly ClassDeclarationSyntax decl;
    public List<Info> parents;
    bool isLinearized;
    bool marked;

    public Info(SymbolId id, ClassDeclarationSyntax decl) {
      this.id = id;
      this.decl = decl;
    }

    public Info(SymbolId id, ClassDeclarationSyntax decl, List<Info> parents) {
      this.id = id;
      this.decl = decl;
      this.parents = parents;
    }

    public List<Info> getLinearization() {
      if (!isLinearized) {
        if (marked) throw new Exception("Cyclic reference traits: " + id);
        marked = true;
        var newParents = new List<Info> { this };
        foreach (var parent in parents) {
          var right = parent.getLinearization();
          for (var i = newParents.Count - 1; i >= 1; i--) {
            if (right.IndexOf(newParents[i]) != -1) newParents.RemoveAt(i);
          }
          newParents.AddRange(right);
        }
        marked = false;
        isLinearized = true;
        parents = newParents;
      }
      return parents;
    }

    public override string ToString() {
      return $"{id}";
    }
  }
}
