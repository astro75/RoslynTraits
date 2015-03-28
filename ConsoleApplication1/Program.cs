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

  class Info {
    public readonly string id;
    public readonly ClassDeclarationSyntax decl;
    public List<Info> parents;
    bool isLinearized;
    bool marked;

    public Info(string id, ClassDeclarationSyntax decl) {
      this.id = id;
      this.decl = decl;
    }

    public Info(string id, ClassDeclarationSyntax decl, List<Info> parents) {
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
              .WithModifiers(abs.Modifiers.remove(SyntaxKind.AbstractKeyword));
          var interf2 = interf.WithIdentifier(SF.Identifier(nameToExtendableInterface(abs.Identifier.Text)));
          interf = interf.WithMembers(SF.List(abs.Members.SelectMany(interfaceMember)));

          var nsName = handleNamespaces(model, abs, interf, ref cu);
          handleNamespaces(model, abs, interf2, ref cu);
          var id = (nsName + "." + interf.Identifier).TrimStart('.');
          listInfos.Add(new Info(id, abs));
        }
        sol = addReplaceDocument(sol.GetDocument(doc.Id).Project, newName, cu, ws).Solution;
      }

      {
        foreach (var info in listInfos) {
          var doc = sol.GetDocument(info.decl.SyntaxTree);
          var model = doc.GetSemanticModelAsync().Result;
          var symbol = model.GetDeclaredSymbol(info.decl);
          var parents = symbol.Interfaces
            .Select(i => extendableToInterface(i.ToString()))
            .Select(id => listInfos.FirstOrDefault(info2 => info2.id == id))
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
          var parents = tuple.Item2.Interfaces.Select(i => i.ToString())
            .Select(id => listInfos.FirstOrDefault(info => info.id == id))
            .Where(_ => _ != null)
            .ToList();
          var classInfo = new Info(tuple.Item2.ToString(), tuple.Item1, parents);
          var partial = SF.ClassDeclaration(tuple.Item1.Identifier)
            .WithModifiers(addModifier(tuple.Item1.Modifiers, SyntaxKind.PartialKeyword));
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

    static string extendableToInterface(string name) {
      var ind = name.LastIndexOf('.');
      return name.Substring(0, ind + 1) + "T" + name.Substring(ind + 2);
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

    static string handleNamespaces(
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
      return nsName;
    }

    static IEnumerable<MemberDeclarationSyntax> partialMembers(SyntaxList<MemberDeclarationSyntax> members) {
      var list = new List<MemberDeclarationSyntax>();
      foreach (var member in members) {
        if (member is FieldDeclarationSyntax) {
          var field = (FieldDeclarationSyntax) member;
          if (!handlePublicFields(field, list, true)) {
            list.Add(field);
          }
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
        handlePublicFields(field, list);
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
      bool useMmodifiers = false
    ) {
      if (field.Modifiers.has(SyntaxKind.PublicKeyword)) {
        foreach (var variable in field.Declaration.Variables) {
          var newProp = SF.PropertyDeclaration(field.Declaration.Type, variable.Identifier)
            .AddAccessorListAccessors(
              SF.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                .WithSemicolonToken(SF.Token(SyntaxKind.SemicolonToken)));
          if (field.Modifiers.hasNot(SyntaxKind.ReadOnlyKeyword)) {
            newProp = newProp.AddAccessorListAccessors(
              SF.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
                .WithSemicolonToken(SF.Token(SyntaxKind.SemicolonToken)));
          }
          if (useMmodifiers)
            newProp = newProp.WithModifiers(field.Modifiers).remove(SyntaxKind.ReadOnlyKeyword);
          list.Add(newProp);
        }
        return true;
      }
      return false;
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
