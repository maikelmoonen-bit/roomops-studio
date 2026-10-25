// Generates web/src/bridge/contracts.gen.ts from the bridge registry so the frontend and backend share one typed contract.
// Usage: dotnet run --project tools/RoomOps.ContractGen -- <output.ts> [--check]

using RoomOps.Bridge;
using RoomOps.ContractGen;

var output = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "web/src/bridge/contracts.gen.ts";
var check = args.Contains("--check");
var source = TypeScriptGenerator.Generate(BridgeContract.Build());

if (check)
{
    var existing = File.Exists(output) ? File.ReadAllText(output) : string.Empty;
    if (!string.Equals(existing.ReplaceLineEndings("\n"), source, StringComparison.Ordinal))
    {
        Console.Error.WriteLine($"{output} is out of date. Run: dotnet run --project tools/RoomOps.ContractGen -- {output}");
        return 1;
    }
    Console.WriteLine($"{output} is up to date.");
    return 0;
}

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
File.WriteAllText(output, source);
Console.WriteLine($"Wrote {output}");
return 0;
