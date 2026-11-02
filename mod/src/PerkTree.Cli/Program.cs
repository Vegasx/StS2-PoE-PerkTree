using PerkTree.Core;

// Dev/CI validator.
//   perktree validate <effects.json> <tree.json> [<tree.json> ...] [--keywords <file>]
//   perktree validate-catalog <effects.json> [--keywords <file>]
// Exit code 0 = all valid (warnings allowed), 1 = errors, 2 = bad usage.

const string Usage =
    "usage: perktree validate <effects.json> <tree.json> [<tree.json> ...] [--keywords <file>]\n" +
    "       perktree validate-catalog <effects.json> [--keywords <file>]\n" +
    "  --keywords: text file, one known keyword/power id per line ('#' comments). Omit to skip the keyword check.";

if (args.Length < 2 || args[0] is not ("validate" or "validate-catalog"))
{
    Console.Error.WriteLine(Usage);
    return 2;
}

var files = new List<string>();
HashSet<string>? keywords = null;
for (int i = 1; i < args.Length; i++)
{
    if (args[i] == "--keywords" && i + 1 < args.Length)
        keywords = File.ReadAllLines(args[++i]).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'))
                       .ToHashSet(StringComparer.Ordinal);
    else
        files.Add(args[i]);
}

var catalog = PerkJson.LoadCatalogFile(files[0]);

if (args[0] == "validate-catalog")
{
    var report = TreeValidator.ValidateCatalog(catalog, keywords);
    Console.WriteLine($"{files[0]}: {(report.IsValid ? "valid" : "INVALID")} ({catalog.Effects.Count} effects, {report.Errors.Count()} errors, {report.Warnings.Count()} warnings)");
    if (report.Issues.Count > 0) Console.WriteLine(report);
    return report.IsValid ? 0 : 1;
}

if (files.Count < 2)
{
    Console.Error.WriteLine(Usage);
    return 2;
}

bool anyErrors = false;
foreach (var treePath in files.Skip(1))
{
    var report = TreeValidator.Validate(PerkJson.LoadTreeFile(treePath), catalog, keywords);
    Console.WriteLine($"{treePath}: {(report.IsValid ? "valid" : "INVALID")} ({report.Errors.Count()} errors, {report.Warnings.Count()} warnings)");
    if (report.Issues.Count > 0) Console.WriteLine(report.ToString());
    anyErrors |= !report.IsValid;
}
return anyErrors ? 1 : 0;
