using Mono.Cecil;

if (args.Length != 1) { Console.Error.WriteLine("Usage: AbiCheck <plugin.dll>"); return 2; }
var packageRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
var target = new Dictionary<string, AssemblyDefinition>(StringComparer.OrdinalIgnoreCase);
foreach (var pkg in new[] { "jellyfin.controller", "jellyfin.model", "jellyfin.common", "jellyfin.naming", "jellyfin.data" })
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
var fieldRefs = plugin.MainModule.GetMemberReferences().OfType<FieldReference>()
    .Where(r => TargetAssembly(r.DeclaringType) is { } name && definitions.ContainsKey(name))
    .DistinctBy(f => f.FullName + "@" + TargetAssembly(f.DeclaringType)).ToArray();
var missingFields = new List<string>();
foreach (var f in fieldRefs)
{
    var owner = f.DeclaringType.GetElementType().FullName;
    var assembly = TargetAssembly(f.DeclaringType)!;
    if (!definitions[assembly].TryGetValue(owner, out var td))
    {
        missingFields.Add($"MISSING_FIELD_TYPE [{assembly}] {owner}::{f.Name}");
        continue;
    }
    if (!td.Fields.Any(df => df.Name == f.Name && df.FieldType.FullName == f.FieldType.FullName))
        missingFields.Add($"MISSING_FIELD [{assembly}] {f.FullName}");
}
var typeRefs = plugin.MainModule.GetTypeReferences()
    .Where(t => TargetAssembly(t) is { } name && definitions.ContainsKey(name))
    .DistinctBy(t => t.FullName + "@" + TargetAssembly(t)).ToArray();
var missingTypes = typeRefs.Where(t => !definitions[TargetAssembly(t)!].ContainsKey(t.FullName))
    .Select(t => $"MISSING_TYPE [{TargetAssembly(t)}] {t.FullName}").ToArray();
foreach (var s in missing) Console.WriteLine(s);
foreach (var s in missingFields) Console.WriteLine(s);
foreach (var s in missingTypes) Console.WriteLine(s);
Console.WriteLine($"ABI_CHECK_DONE: {apiRefs.Length} methods ({missing.Count} mismatched), {fieldRefs.Length} fields ({missingFields.Count} mismatched), {typeRefs.Length} types ({missingTypes.Length} missing)");
Console.WriteLine("HOST_ASSEMBLY_REFERENCES: " + string.Join(", ", plugin.MainModule.AssemblyReferences.Where(a => a.Name.StartsWith("MediaBrowser") || a.Name.StartsWith("Jellyfin") || a.Name.StartsWith("Emby")).Select(a => a.FullName)));
return 0;
