using Microsoft.CodeAnalysis.MSBuild;

namespace RoslynTraits {
  internal class Program {
    static void Main(string[] args) {
      var ws = MSBuildWorkspace.Create();
      var sol = ws.OpenSolutionAsync(args[0]).Result;
      sol = TraitMaker.run(sol, ws);
      ws.TryApplyChanges(sol);
    }
  }
}
