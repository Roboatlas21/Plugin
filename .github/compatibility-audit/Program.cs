using Mono.Cecil;

if (args.Length != 1) { Console.Error.WriteLine("Usage: AbiCheck <plugin.dll>"); return 2; }
var packageRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
var target = new Dictionary<string, AssemblyDefinition>(StringComparer.OrdinalIgnoreCase);
foreach (var pkg in new[] { "jellyfin.controller", "jellyfin.model", "jellyfin.common", "jellyfin.naming" })
{
    var dir = Path.Combine(packageRoot, pkg, "12.2.0", "lib", "net10.0");
    if (!Directory.Exists(dir)) continue;
    foreach (var file in Directory.EnumerateFiles(dir, "*.dll"))
    {
        var asm = AssemblyDefinition.ReadAssembly(file);
        target[asm.Name.Name] = asm;
        Console.WriteLine($"TARGET {asm.Name.Name}: {file}");
    }
}
if (target.Count < 2) { Console.Error.WriteLine("Jellyfin 12.2 packages not loaded"); return 2; }
static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
{
    foreach (var t in types)
    {
        yield return t;
        foreach (var nested in AllTypes(t.NestedTypes)) yield return nested;
    }
}
var definitions = target.ToDictionary(
    pair => pair.Key,
    pair => AllTypes(pair.Value.MainModule.Types).ToDictionary(t => t.FullName, StringComparer.Ordinal),
    StringComparer.OrdinalIgnoreCase);
static string? TargetAssembly(TypeReference type)
{
    var core = type.GetElementType();
    return core.Scope is AssemblyNameReference name ? name.Name : null;
}
var plugin = AssemblyDefinition.ReadAssembly(args[0]);
var apiRefs = plugin.MainModule.GetMemberReferences().OfType<MethodReference>()
    .Select(r => r is GenericInstanceMethod g ? g.ElementMethod : r)
    .Where(r => TargetAssembly(r.DeclaringType) is { } name && definitions.ContainsKey(name))
    .DistinctBy(m => m.FullName + "@" + TargetAssembly(m.DeclaringType))
    .OrderBy(m => TargetAssembly(m.DeclaringType)).ThenBy(m => m.FullName).ToArray();
Console.WriteLine($"METHOD_REFERENCES_TO_12_2_ASSEMBLIES: {apiRefs.Length}");
var missing = new List<string>();
foreach (var mr in apiRefs)
{
    var assembly = TargetAssembly(mr.DeclaringType)!;
    var owner = mr.DeclaringType.GetElementType().FullName;
    if (!definitions[assembly].TryGetValue(owner, out var td))
    {
        missing.Add($"MISSING_TYPE [{assembly}] {owner} referenced by {mr.FullName}");
        continue;
    }
    bool matches(MethodDefinition md) =>
        md.Name == mr.Name &&
        md.Parameters.Count == mr.Parameters.Count &&
        md.GenericParameters.Count == mr.GenericParameters.Count &&
        md.ReturnType.FullName == mr.ReturnType.FullName &&
        md.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(mr.Parameters.Select(p => p.ParameterType.FullName));
    if (td.Methods.Any(matches)) continue;
    var candidates = td.Methods.Where(md => md.Name == mr.Name).Select(md => md.FullName).Take(4).ToArray();
    missing.Add($"MISSING_METHOD [{assembly}] {mr.FullName}\n  12.2 candidates: {string.Join(" | ", candidates)}");
}
foreach (var s in missing) Console.WriteLine(s);
Console.WriteLine($"ABI_CHECK_DONE: {apiRefs.Length} method references scanned, {missing.Count} missing or changed");
return 0;
