using Microsoft.CodeAnalysis;

namespace RoslynTraits {
  struct SymbolId {
    public readonly string nspace;
    public readonly string identifier;
    public readonly INamedTypeSymbol symbol;

    public SymbolId(string nspace, string identifier, INamedTypeSymbol symbol = null) {
      this.nspace = nspace;
      this.identifier = identifier;
      this.symbol = symbol;
    }

    public SymbolId(INamedTypeSymbol symbol)
      : this(symbol.ContainingNamespace.ToString(), symbol.Name, symbol) { }

    public SymbolId withSymbol(INamedTypeSymbol symbol) {
      return new SymbolId(nspace, identifier, symbol);
    }

    public override string ToString() {
      return $"{nspace}.{identifier}";
    }

    public bool Equals(SymbolId other) {
      return string.Equals(nspace, other.nspace) && string.Equals(identifier, other.identifier);
    }
  }
}
