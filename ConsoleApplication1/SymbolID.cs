using Microsoft.CodeAnalysis;

namespace RoslynTraits {
  struct SymbolId {
    public readonly string nspace;
    public readonly string identifier;

    public SymbolId(string nspace, string identifier) {
      this.nspace = nspace;
      this.identifier = identifier;
    }

    public override string ToString() {
      return $"{nspace}.{identifier}";
    }

    public SymbolId(ISymbol symbol)
      : this(symbol.ContainingNamespace.ToString(), symbol.Name) { }

    public bool Equals(SymbolId other) {
      return string.Equals(nspace, other.nspace) && string.Equals(identifier, other.identifier);
    }
  }
}
