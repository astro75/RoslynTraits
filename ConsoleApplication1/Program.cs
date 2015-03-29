using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.MSBuild;
using SF = Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace ConsoleApplication1 {

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
      : this(symbol.ContainingNamespace.ToString(), symbol.Name) {}

    public bool Equals(SymbolId other) {
      return string.Equals(nspace, other.nspace) && string.Equals(identifier, other.identifier);
    }
  }

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
        var newParents = new List<Info> {this};
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

  internal class Program {
    static void Main(string[] args) {
      var ws = MSBuildWorkspace.Create();
      var sol = ws.OpenSolutionAsync(args[0]).Result;
      sol = run(sol, ws);
      ws.TryApplyChanges(sol);
    }

    static Solution run(Solution sol, MSBuildWorkspace ws) {
      var listInfos = new List<Info>();
      foreach (var doc in sol.allDocs()) {
        var root = doc.GetSyntaxRootAsync().Result;
        var model = doc.GetSemanticModelAsync().Result;
        var abstracts = root.DescendantNodes()
          .OfType<ClassDeclarationSyntax>()
          .Where(c => c.Modifiers.has(SyntaxKind.AbstractKeyword))
          .Where(c => c.Identifier.Text.EndsWith("Trait")).ToList();
        if (abstracts.Count == 0) continue;
        var newName = doc.Name.Replace(".cs", ".trait.interface.generated.cs");
        var cu = SF.CompilationUnit();
        foreach (var abs in abstracts) {
          var interf =
            SF.InterfaceDeclaration(nameToInterface(abs.Identifier.Text))
              .WithModifiers(abs.Modifiers.remove(SyntaxKind.AbstractKeyword))
              .WithTypeParameterList(abs.TypeParameterList)
              .WithConstraintClauses(abs.ConstraintClauses);
          var interf2 = interf.WithIdentifier(SF.Identifier(nameToExtendableInterface(abs.Identifier.Text)));
          interf = interf.WithMembers(SF.List(abs.Members.SelectMany(interfaceMember)));

          var id = handleNamespaces(model, abs, interf, ref cu);
          handleNamespaces(model, abs, interf2, ref cu);
          listInfos.Add(new Info(new SymbolId(id.nspace, interf.Identifier.Text), abs));
        }
        sol = addReplaceDocument(sol.GetDocument(doc.Id).Project, newName, cu, ws).Solution;
      }

      {
        foreach (var info in listInfos) {
          var doc = sol.GetDocument(info.decl.SyntaxTree);
          var model = doc.GetSemanticModelAsync().Result;
          var symbol = model.GetDeclaredSymbol(info.decl);
          var parents = symbol.Interfaces
            .Select(i => extendableToInterface(new SymbolId(i)))
            .Select(id => listInfos.FirstOrDefault(info2 => info2.id.Equals(id)))
            .Where(_ => _ != null)
            .ToList();
          info.parents = parents;
        }
      }
      
      foreach (var doc in sol.allDocs()) {
        var model = doc.GetSemanticModelAsync().Result;
        var root = doc.GetSyntaxRootAsync().Result;
        var classes = root.DescendantNodes()
          .OfType<ClassDeclarationSyntax>()
          .Where(c => c.Modifiers.hasNot(SyntaxKind.AbstractKeyword))
          .Select(c => Tuple.Create(c, model.GetDeclaredSymbol(c)))
          .Where(tpl => tpl.Item2.Interfaces.Any())
          .ToList();
        if (classes.Count == 0) continue;
        var newName = doc.Name.Replace(".cs", ".trait.partial.generated.cs");

        var cu = SF.CompilationUnit();
        var worked = false;
        foreach (var tuple in classes) {
          var parents = tuple.Item2.Interfaces.Select(i => new SymbolId(i))
            .Select(id => listInfos.FirstOrDefault(info => info.id.Equals(id)))
            .Where(_ => _ != null)
            .ToList();
          var classInfo = new Info(new SymbolId(tuple.Item2), tuple.Item1, parents);
          var partial = SF.ClassDeclaration(tuple.Item1.Identifier)
            .WithModifiers(addModifier(tuple.Item1.Modifiers, SyntaxKind.PartialKeyword))
            .WithTypeParameterList(tuple.Item1.TypeParameterList);
          foreach (var info2 in classInfo.getLinearization()) {
            if (info2.decl.Modifiers.hasNot(SyntaxKind.AbstractKeyword)) continue;
            var abs = info2.decl;
            partial = partial.WithMembers(partial.Members.AddRange(partialMembers(abs.Members)));
            worked = true;
          }
          handleNamespaces(model, tuple.Item1, partial, ref cu);
        }
        if (worked) {
          sol = addReplaceDocument(sol.GetDocument(doc.Id).Project, newName, cu, ws).Solution;
        }
      }
      return sol;
    }

    static string nameToInterface(string name) {
      return "T" + name.Replace("Trait", "");
    }

    static string nameToExtendableInterface(string name) {
      return "E" + name.Replace("Trait", "");
    }

    static SymbolId extendableToInterface(SymbolId name) {
      return new SymbolId(name.nspace, "T" + name.identifier.Substring(1));
    }

    static Project addReplaceDocument(Project proj, string newName, CompilationUnitSyntax cu, MSBuildWorkspace ws) {
      try {
        proj = proj.RemoveDocument(proj.Documents.First(d => d.Name == newName).Id);
      }
      catch {
        // ignored
      }
      var newDoc = proj.AddDocument(newName, Formatter.Format(cu, ws));
      proj = newDoc.Project;
      return proj;
    }

    static SymbolId handleNamespaces(
      SemanticModel model, TypeDeclarationSyntax originalType, TypeDeclarationSyntax newMember,
      ref CompilationUnitSyntax cu
    ) {
      var usings = originalType.Ancestors().OfType<CompilationUnitSyntax>().First().Usings;
      var symbol = model.GetDeclaredSymbol(originalType);
      var nsName = symbol.ContainingNamespace.Name == "" ? "" : symbol.ContainingNamespace.ToString();
      if (nsName == "") {
        cu = cu.WithMembers(cu.Members.Add(newMember));
      }
      else {
        var ns = SF.NamespaceDeclaration(SF.ParseName(nsName))
          .WithMembers(SF.SingletonList((MemberDeclarationSyntax) newMember));
        cu = cu.WithMembers(cu.Members.Add(ns));
      }
      cu = cu.WithUsings(usings);
      return new SymbolId(symbol);
    }

    static IEnumerable<MemberDeclarationSyntax> partialMembers(SyntaxList<MemberDeclarationSyntax> members) {
      var list = new List<MemberDeclarationSyntax>();
      foreach (var member in members) {
        if (member is FieldDeclarationSyntax) {
          var field = (FieldDeclarationSyntax) member;
          if (!handlePublicFields(field, list, false)) {
            list.Add(field);
          }
        }
        else if (member is PropertyDeclarationSyntax) {
          var prop = (PropertyDeclarationSyntax) member;
          list.Add(prop);
        }
        else if (member is MethodDeclarationSyntax) {
          var method = (MethodDeclarationSyntax) member;
          if (method.hasNot(SyntaxKind.AbstractKeyword)) {
            method = method.remove(SyntaxKind.OverrideKeyword);
            method = method.remove(SyntaxKind.VirtualKeyword);
            list.Add(method);
          }
        }
      }
      return list;
    }

    static SyntaxTokenList addModifier(SyntaxTokenList modifiers, SyntaxKind kind) {
      if (modifiers.Any(m => m.IsKind(kind))) return modifiers;
      return modifiers.Add(SF.Token(kind));
    }

    static IEnumerable<MemberDeclarationSyntax> interfaceMember(MemberDeclarationSyntax member) {
      var list = new List<MemberDeclarationSyntax>();
      if (member is FieldDeclarationSyntax) {
        var field = (FieldDeclarationSyntax) member;
        handlePublicFields(field, list, true);
      }
      else if (member is PropertyDeclarationSyntax) {
        var prop = (PropertyDeclarationSyntax)member;
        handlePublicFields(prop, list);
      }
      else if (member is MethodDeclarationSyntax) {
        var method = (MethodDeclarationSyntax) member;
        if ((method.has(SyntaxKind.AbstractKeyword) || method.has(SyntaxKind.PublicKeyword)) 
          && method.hasNot(SyntaxKind.OverrideKeyword)
        ) {
          list.Add(
            method.WithModifiers(SF.TokenList())
            .WithBody(null)
            .WithSemicolonToken(SF.Token(SyntaxKind.SemicolonToken))
          );
        }
      }
      return list;
    }

    static bool handlePublicFields(
      FieldDeclarationSyntax field, List<MemberDeclarationSyntax> list,
      bool isInterface
    ) {
      if (field.Modifiers.has(SyntaxKind.PublicKeyword)) {
        foreach (var variable in field.Declaration.Variables) {
          var newProp = SF.PropertyDeclaration(field.Declaration.Type, variable.Identifier)
            .AddAccessorListAccessors(
              SF.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                .WithSemicolonToken(SF.Token(SyntaxKind.SemicolonToken)));
          if (!isInterface && variable.Initializer != null) {
            newProp = newProp
              .WithInitializer(variable.Initializer)
              .WithSemicolon(SF.Token(SyntaxKind.SemicolonToken));
          }
          if (field.Modifiers.hasNot(SyntaxKind.ReadOnlyKeyword)) {
            newProp = newProp.AddAccessorListAccessors(
              SF.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
                .WithSemicolonToken(SF.Token(SyntaxKind.SemicolonToken)));
          }
          if (!isInterface)
            newProp = newProp.WithModifiers(field.Modifiers).remove(SyntaxKind.ReadOnlyKeyword);
          list.Add(newProp);
        }
        return true;
      }
      return false;
    }

    static void handlePublicFields(
      PropertyDeclarationSyntax prop, List<MemberDeclarationSyntax> list
    ) {
      if (prop.Modifiers.has(SyntaxKind.PublicKeyword)) {
        var newProp = SF.PropertyDeclaration(prop.Type, prop.Identifier)
          .AddAccessorListAccessors(
            SF.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
              .WithSemicolonToken(SF.Token(SyntaxKind.SemicolonToken)));
        if (prop.AccessorList?.Accessors.Any(SyntaxKind.SetAccessorDeclaration) == true) {
          newProp = newProp.AddAccessorListAccessors(
            SF.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
              .WithSemicolonToken(SF.Token(SyntaxKind.SemicolonToken)));
        }
        list.Add(newProp);
      }
    }
  }

  internal static class Exts {
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
      return SF.TokenList(tokens.Where(m => !m.IsKind(kind)));
    }
  }
}
