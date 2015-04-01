﻿using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.MSBuild;
using SF = Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace RoslynTraits {
  public static class TraitMaker {
    public static Solution run(Solution sol, Workspace ws) {
      var listInfos = new List<Info>();
      var baseRewriter = new BaseTypeRewriter();
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
              .WithConstraintClauses(abs.ConstraintClauses)
              .WithBaseList(baseRewriter.VisitListElement(abs.BaseList));
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
          info.id = info.id.withSymbol(symbol);
          var parents = symbol.Interfaces
            .Select(i => {
              var id = extendableToInterface(new SymbolId(i));
              return listInfos.FirstOrDefault(info2 => info2.id.Equals(id))?.withSymbol(i);
            })
            .Where(_ => _ != null)
            .ToList();
          info.parents = parents;
        }
      }

      {
        foreach (var info in listInfos) {
          var doc = sol.GetDocument(info.decl.SyntaxTree);
          var model = doc.GetSemanticModelAsync().Result;
          var symbol = model.GetDeclaredSymbol(info.decl);
          if (symbol.Interfaces.Any() == false) continue;
          var newDoc = sol.GetDocument(symbol.Interfaces.First().DeclaringSyntaxReferences[0].SyntaxTree);
          var cu = (CompilationUnitSyntax) newDoc.GetSyntaxRootAsync().Result;
          var parentIdent = info.decl.Identifier + "Parent";
          var partial = SF.ClassDeclaration(info.decl.Identifier)
            .WithModifiers(addModifier(info.decl.Modifiers, SyntaxKind.PartialKeyword))
            .WithTypeParameterList(info.decl.TypeParameterList)
            .WithBaseList(SF.BaseList(SF.SingletonSeparatedList((BaseTypeSyntax)SF.SimpleBaseType(SF.ParseTypeName(parentIdent)))));
          var parent = SF.ClassDeclaration(parentIdent)
            .WithModifiers(info.decl.Modifiers)
            .WithTypeParameterList(info.decl.TypeParameterList);
          foreach (var info2 in info.getLinearization().Skip(1)) {
            var members = rewriteGenericParams(info2, info2.Item1.decl.Members);
            parent = parent.WithMembers(parent.Members.AddRange(members));
          }
          handleNamespaces(model, info.decl, partial, ref cu);
          handleNamespaces(model, info.decl, parent, ref cu);
          sol = addReplaceDocument(sol.GetDocument(doc.Id).Project, newDoc.Name, cu, ws).Solution;
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
          var parents = tuple.Item2.Interfaces.Select(i => {
            var id = new SymbolId(i);
            return listInfos.FirstOrDefault(info2 => info2.id.Equals(id))?.withSymbol(i);
          }).Where(_ => _ != null).ToList();
          var classInfo = new Info(new SymbolId(tuple.Item2), tuple.Item1, parents);
          var partial = SF.ClassDeclaration(tuple.Item1.Identifier)
            .WithModifiers(addModifier(tuple.Item1.Modifiers, SyntaxKind.PartialKeyword))
            .WithTypeParameterList(tuple.Item1.TypeParameterList);
          foreach (var info2 in classInfo.getLinearization()) {
            if (info2.Item1.decl.Modifiers.hasNot(SyntaxKind.AbstractKeyword)) continue;
            var abs = info2.Item1.decl;
            var members = rewriteGenericParams(info2, abs.Members);
            partial = partial.WithMembers(partial.Members.AddRange(partialMembers(members)));
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

    static SyntaxList<MemberDeclarationSyntax> rewriteGenericParams(Tuple<Info, INamedTypeSymbol> info2, SyntaxList<MemberDeclarationSyntax> members) {
      var symbol = info2.Item2;
      var replaces = new List<Tuple<string, string>>();
      for (var i = 0; i < symbol.Arity; i++) {
        var str1 = symbol.TypeParameters[0].Name;
        var str2 = symbol.TypeArguments[0].Name;
        if (str1 != str2) replaces.Add(Tuple.Create(str1, str2));
      }
      if (replaces.Count > 0) {
        var rewriter = new GenericRewriter(replaces);
        members = rewriter.VisitList(members);
      }
      return members;
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

    static Project addReplaceDocument(Project proj, string newName, CompilationUnitSyntax cu, Workspace ws) {
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
          .WithMembers(SF.SingletonList((MemberDeclarationSyntax)newMember));
        cu = cu.WithMembers(cu.Members.Add(ns));
      }
      cu = cu.WithUsings(usings);
      return new SymbolId(symbol);
    }

    static IEnumerable<MemberDeclarationSyntax> partialMembers(SyntaxList<MemberDeclarationSyntax> members) {
      var list = new List<MemberDeclarationSyntax>();
      foreach (var member in members) {
        if (member is FieldDeclarationSyntax) {
          var field = (FieldDeclarationSyntax)member;
          if (!handlePublicFields(field, list, false)) {
            list.Add(field);
          }
        }
        else if (member is PropertyDeclarationSyntax) {
          var prop = (PropertyDeclarationSyntax)member;
          list.Add(prop);
        }
        else if (member is MethodDeclarationSyntax) {
          var method = (MethodDeclarationSyntax)member;
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
        var field = (FieldDeclarationSyntax)member;
        handlePublicFields(field, list, true);
      }
      else if (member is PropertyDeclarationSyntax) {
        var prop = (PropertyDeclarationSyntax)member;
        handlePublicFields(prop, list);
      }
      else if (member is MethodDeclarationSyntax) {
        var method = (MethodDeclarationSyntax)member;
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
}
