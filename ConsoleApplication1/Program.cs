using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SF = Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

namespace ConsoleApplication1
{
  class Program
	{
		static void Main(string[] args) {
			var ws = MSBuildWorkspace.Create();
			var sol = ws.OpenSolutionAsync(@"..\..\..\..\ConsoleApplication2\ConsoleApplication2.sln").Result;
			var proj = sol.Projects.First();
		  var listAll = new List<Tuple<INamedTypeSymbol, ClassDeclarationSyntax>>();
		  foreach (var doc in proj.Documents) {
        if (doc.Name.EndsWith(".generated.cs")) continue;
		    var root = doc.GetSyntaxRootAsync().Result;
		    var abstracts = root.DescendantNodes()
		      .OfType<ClassDeclarationSyntax>()
		      .Where(c => c.Modifiers.Any(m => m.Kind() == SyntaxKind.AbstractKeyword))
          .Where(c => c.Identifier.Text.EndsWith("Trait")).ToList();
		    if (abstracts.Count == 0) continue;
		    var newName = doc.Name.Replace(".cs", ".traitInterface.generated.cs");
		    var abs = abstracts.First();
		    var ns = abs.Ancestors().OfType<NamespaceDeclarationSyntax>().First();
		    var cu = abs.Ancestors().OfType<CompilationUnitSyntax>().First();
		    var interf =
		      SF.InterfaceDeclaration("T" + abs.Identifier.Text.Replace("Trait", ""))
		        .WithModifiers(SF.TokenList(abs.Modifiers.Where(m => m.Kind() != SyntaxKind.AbstractKeyword)));
		    interf =
		      interf.WithMembers(SF.List(abs.Members.SelectMany(interfaceMember)));
		    ns = ns.WithMembers(SF.SingletonList((MemberDeclarationSyntax) interf));
		    cu = cu.WithMembers(SF.SingletonList((MemberDeclarationSyntax) ns));
		    try {
		      proj = proj.RemoveDocument(proj.Documents.First(d => d.Name == newName).Id);
		    }
		    catch {
		      // ignored
		    }
		    var newDoc = proj.AddDocument(newName, Formatter.Format(cu, ws));
		    proj = newDoc.Project;
		    var newRoot = newDoc.GetSyntaxRootAsync().Result;
		    var newInterf = newRoot.DescendantNodes().OfType<InterfaceDeclarationSyntax>().First();
		    var model = newDoc.GetSemanticModelAsync().Result;
		    var info = model.GetDeclaredSymbol(newInterf);
		    listAll.Add(Tuple.Create(info, abs));
		  }
      foreach (var doc in proj.Documents) {
        if (doc.Name.EndsWith(".generated.cs")) continue;
        var model = doc.GetSemanticModelAsync().Result;
        var root = doc.GetSyntaxRootAsync().Result;
        var classes = root.DescendantNodes()
          .OfType<ClassDeclarationSyntax>()
          .Where(c => c.Modifiers.All(m => m.Kind() != SyntaxKind.AbstractKeyword))
          .Select(c => Tuple.Create(c, model.GetDeclaredSymbol(c)))
          .Where(tpl => tpl.Item2.Interfaces.Any())
          .ToList();
        if (classes.Count == 0) continue;

        var tuple = classes.First();

        var interfaces = tuple.Item2.Interfaces;

        var newName = doc.Name.Replace(".cs", ".traitPartial.generated.cs");

        var cu = SF.CompilationUnit();
        var worked = false;
        foreach (var imp in interfaces) {
          foreach (var t in listAll) {
            if (t.Item1.Equals(imp)) {
              var abs = t.Item2;
              var ns = abs.Ancestors().OfType<NamespaceDeclarationSyntax>().First();
              var usings = cu.Usings.AddRange(abs.Ancestors().OfType<CompilationUnitSyntax>().First().Usings);
              var partial =
                SF.ClassDeclaration(tuple.Item1.Identifier)
                .WithModifiers(addModifier(tuple.Item1.Modifiers, SyntaxKind.PartialKeyword));
              partial = partial.WithMembers(partial.Members.AddRange(partialMembers(abs.Members)));
              ns = ns.WithMembers(SF.SingletonList((MemberDeclarationSyntax)partial)).WithUsings(usings);
              cu = cu.WithMembers(cu.Members.Add(ns));
              worked = true;
            }
          }
        }
        if (worked) {
          try {
            proj = proj.RemoveDocument(proj.Documents.First(d => d.Name==newName).Id);
          }
          catch {
            // ignored
          }
          var newDoc = proj.AddDocument(newName, Formatter.Format(cu, ws));
          proj = newDoc.Project;
        }
      }
      ws.TryApplyChanges(proj.Solution);
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
          if (method.Modifiers.All(m => m.Kind() != SyntaxKind.AbstractKeyword)) {
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
        if (method.Modifiers.Any(m => m.Kind() == SyntaxKind.AbstractKeyword)) {
          list.Add(method.WithModifiers(SF.TokenList()));
        }
      }
      return list;
    }

    static bool handlePublicFields(FieldDeclarationSyntax field, List<MemberDeclarationSyntax> list, bool useMmodifiers = false) {
      if (field.Modifiers.Any(m => m.Kind() == SyntaxKind.PublicKeyword)) {
        foreach (var variable in field.Declaration.Variables) {
          var newProp = SF.PropertyDeclaration(field.Declaration.Type, variable.Identifier)
            .AddAccessorListAccessors(
              SF.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                .WithSemicolonToken(SF.Token(SyntaxKind.SemicolonToken)),
              SF.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
                .WithSemicolonToken(SF.Token(SyntaxKind.SemicolonToken)));
          if (useMmodifiers) newProp = newProp.WithModifiers(field.Modifiers);
          list.Add(newProp);
        }
        return true;
      }
      return false;
    }
  }

  class CC {
  }

  abstract class ClassTrait {
    int x, x2;
    protected int y;
    public int z;

    public int doStuff(int x) {
      y += x;
      return z + x;
    }

    public abstract int abs(int x);

  }
}
