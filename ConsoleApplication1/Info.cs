using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynTraits {
  class Info {
    public SymbolId id;
    public readonly ClassDeclarationSyntax decl;
    public List<Tuple<Info,INamedTypeSymbol>> parents;
    bool isLinearized;
    bool marked;

    public Info(SymbolId id, ClassDeclarationSyntax decl, List<Tuple<Info, INamedTypeSymbol>> parents = null, bool isLinearized = false) {
      this.id = id;
      this.decl = decl;
      this.parents = parents;
      this.isLinearized = isLinearized;
    }

    public Tuple<Info, INamedTypeSymbol> withSymbol(INamedTypeSymbol symbol) {
      return Tuple.Create(this, symbol);
    }

    public List<Tuple<Info, INamedTypeSymbol>> getLinearization() {
      if (!isLinearized) {
        if (marked) throw new Exception("Cyclic reference traits: " + id);
        marked = true;
        var newParents = new List<Tuple<Info, INamedTypeSymbol>> { withSymbol(id.symbol) };
        foreach (var parent in parents) {
          var right = parent.Item1.getLinearization();
          if (right.Count > 0) {
            right[0] = Tuple.Create(right[0].Item1, parent.Item2);
          }
          for (var i = newParents.Count - 1; i >= 1; i--) {
            foreach (var tpl in right) {
              if (tpl.Item1.id.Equals(newParents[i].Item1.id)) {
                newParents.RemoveAt(i);
                break;
              }
            }
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
