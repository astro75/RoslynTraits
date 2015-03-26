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
			var sol = ws.OpenSolutionAsync(@"..\..\..\ConsoleApplication1.sln").Result;
			var proj = sol.Projects.First();
		  var listAll = new List<Tuple<ISymbol, ClassDeclarationSyntax>>();
		  foreach (var doc in proj.Documents) {
        if (doc.Name.EndsWith(".generated.cs")) continue;
		    var root = doc.GetSyntaxRootAsync().Result;
		    var abstracts = root.DescendantNodes()
		      .OfType<ClassDeclarationSyntax>()
		      .Where(c => c.Modifiers.Any(m => m.Text == "abstract"))
          .Where(c => c.Identifier.Text.EndsWith("Trait")).ToList();
		    if (abstracts.Count != 0) {
		      var newName = doc.Name.Replace(".cs", ".trait.generated.cs");
		      var abs = abstracts.First();
		      var ns = abs.Ancestors().OfType<NamespaceDeclarationSyntax>().First();
          var cu = abs.Ancestors().OfType<CompilationUnitSyntax>().First();
		      var interf =
            SF.InterfaceDeclaration("I" + abs.Identifier.Text.Replace("Trait", ""))
		          .WithModifiers(SF.TokenList(abs.Modifiers.Where(m => m.Kind() != SyntaxKind.AbstractKeyword)));
		      interf =
		        interf.WithMembers(SF.List(abs.Members.SelectMany(interfaceMember)));
		      ns = ns.WithMembers(SF.SingletonList((MemberDeclarationSyntax) interf));
		      cu = cu.WithMembers(SF.SingletonList((MemberDeclarationSyntax) ns));
		      try {
		        proj = proj.RemoveDocument(proj.Documents.First(d => d.Name == newName).Id);
		      }
		      catch {
		      }
		      var newDoc = proj.AddDocument(newName, Formatter.Format(cu, ws));
		      proj = newDoc.Project;
		      var model = newDoc.GetSemanticModelAsync().Result;
		      var info = model.GetSymbolInfo(interf).Symbol;
          listAll.Add(Tuple.Create(info, abs));
		    }
        foreach (var a in abstracts) {
		      Console.WriteLine(a);
		    }
		  }
		  ws.TryApplyChanges(proj.Solution);
		  Console.ReadLine();
		}

	  static IEnumerable<MemberDeclarationSyntax> interfaceMember(MemberDeclarationSyntax member) {
	    var list = new List<MemberDeclarationSyntax>();
	    if (member is FieldDeclarationSyntax) {
	      var field = (FieldDeclarationSyntax) member;
	      if (field.Modifiers.Any(m => m.Kind() == SyntaxKind.PublicKeyword)) {
          foreach (var variable in field.Declaration.Variables) {
            list.Add(SF.PropertyDeclaration(field.Declaration.Type, variable.Identifier)
              .AddAccessorListAccessors(
                SF.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                    .WithSemicolonToken(SF.Token(SyntaxKind.SemicolonToken)),
                SF.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
                    .WithSemicolonToken(SF.Token(SyntaxKind.SemicolonToken)))
              );
          }
        }
      } else if (member is MethodDeclarationSyntax) {
        var method = (MethodDeclarationSyntax) member;
        if (method.Modifiers.Any(m => m.Kind() == SyntaxKind.AbstractKeyword)) {
          list.Add(method.WithModifiers(SF.TokenList()));
        }
      }
	    return list;
	  }
	}

  abstract class CC {
    
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
