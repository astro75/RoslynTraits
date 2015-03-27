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
  internal class Program {
    static void Main(string[] args) {
      var ws = MSBuildWorkspace.Create();
      var sol = ws.OpenSolutionAsync(args[0]).Result;
      var proj = sol.Projects.First();
      var listAll = new List<Tuple<string, ClassDeclarationSyntax>>();
      foreach (var doc in proj.Documents) {
        if (doc.Name.EndsWith(".generated.cs")) continue;
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
          var mutableAbs = abs;
          var interf =
            SF.InterfaceDeclaration(abstractNameToInterfaceName(abs.Identifier.Text))
              .WithModifiers(SF.TokenList(abs.Modifiers.Where(m => m.Kind() != SyntaxKind.AbstractKeyword)));
          interf = interf.WithMembers(SF.List(abs.Members.SelectMany(interfaceMember)));
          var symbol = model.GetDeclaredSymbol(abs);
          if (symbol.BaseType.Name.EndsWith("Trait")) {
            interf = interf.WithBaseList(
              SF.BaseList(SF.SingletonSeparatedList<BaseTypeSyntax>(
                SF.SimpleBaseType(SF.ParseTypeName(
                  abstractNameToInterfaceName(symbol.BaseType.Name)
                ))
              ))
            );
          }
          {
            while (symbol.BaseType.Name.EndsWith("Trait")) {
              symbol = symbol.BaseType;

              var absParent = (ClassDeclarationSyntax) symbol.DeclaringSyntaxReferences.First().GetSyntax();
              mutableAbs = mutableAbs.AddMembers(absParent.Members.ToArray());
            }
          }
          var nsName = handleNamespaces(model, abs, interf, ref cu);
          var id = (nsName + "." + interf.Identifier).TrimStart('.');
          listAll.Add(Tuple.Create(id, mutableAbs));
        }
        proj = addReplaceDocument(proj, newName, cu, ws);
      }
      foreach (var doc in proj.Documents) {
        if (doc.Name.EndsWith(".generated.cs")) continue;
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
          var interfaces = tuple.Item2.Interfaces.Select(i => i.ToString());
          foreach (var imp in interfaces) {
            foreach (var t in listAll) {
              if (t.Item1.Equals(imp)) {
                var abs = t.Item2;
                var partial =
                  SF.ClassDeclaration(tuple.Item1.Identifier)
                    .WithModifiers(addModifier(tuple.Item1.Modifiers, SyntaxKind.PartialKeyword));
                partial = partial.WithMembers(partial.Members.AddRange(partialMembers(abs.Members)));
                handleNamespaces(model, tuple.Item1, partial, ref cu);
                worked = true;
              }
            }
          }
        }
        if (worked) {
          proj = addReplaceDocument(proj, newName, cu, ws);
        }
      }
      ws.TryApplyChanges(proj.Solution);
    }

    static string abstractNameToInterfaceName(string name) {
      return "T" + name.Replace("Trait", "");
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
            if (method.has(SyntaxKind.OverrideKeyword)) {
              method = method.remove(SyntaxKind.OverrideKeyword);
            }
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
          if (field.Modifiers.All(m => m.Kind() != SyntaxKind.ReadOnlyKeyword)) {
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
    public static bool has(this BasePropertyDeclarationSyntax decl, SyntaxKind kind) {
      return decl.Modifiers.has(kind);
    }

    public static bool has(this BaseMethodDeclarationSyntax decl, SyntaxKind kind) {
      return decl.Modifiers.has(kind);
    }

    public static bool has(this SyntaxTokenList mods, SyntaxKind kind) {
      return mods.Any(m => m.IsKind(kind));
    }

    public static bool hasNot(this BasePropertyDeclarationSyntax decl, SyntaxKind kind) {
      return decl.Modifiers.hasNot(kind);
    }

    public static bool hasNot(this BaseMethodDeclarationSyntax decl, SyntaxKind kind) {
      return decl.Modifiers.hasNot(kind);
    }

    public static bool hasNot(this SyntaxTokenList mods, SyntaxKind kind) {
      return mods.Any(m => !m.IsKind(kind));
    }

    public static PropertyDeclarationSyntax remove(this PropertyDeclarationSyntax decl, SyntaxKind kind) {
      return decl.WithModifiers(SF.TokenList(decl.Modifiers.Where(m => !m.IsKind(kind))));
    }

    public static MethodDeclarationSyntax remove(this MethodDeclarationSyntax decl, SyntaxKind kind) {
      return decl.WithModifiers(SF.TokenList(decl.Modifiers.Where(m => !m.IsKind(kind))));
    }
  }
}
