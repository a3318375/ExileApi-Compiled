using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

internal static class FavoriteIndex
{
    // Use the shipped settings parser and PathOf in each language. Pair nodes
    // by owner/type and tree position, independent of spelling and capitalization.
    public static void Export(string root, string catalog, string file, bool localized)
    {
        root = Path.GetFullPath(root);
        var groups = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(
            File.ReadAllText(Path.Combine(catalog, "settings.json")))!;
        var pluginNames = JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(catalog, "plugin-names.json")))!.Keys.ToHashSet();
        var indexed = new HashSet<string>();
        var paths = new Dictionary<string, string>();
        Directory.SetCurrentDirectory(root);
        var dirs = new List<string> { root };
        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            var dep = dirs.Select(d => Path.Combine(d, name.Name + ".dll")).FirstOrDefault(File.Exists);
            return dep is null ? null : ctx.LoadFromAssemblyPath(dep);
        };
        var core = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(root, "ExileCore.dll"));
        // SettingsParser reports unrecognized non-menu properties through the
        // process logger. Give it the same interface without starting the HUD.
        var logger = Assembly.Load("Serilog").GetType("Serilog.Log")!.GetProperty("Logger")!.GetValue(null);
        core.GetType("ExileCore.Core")!.GetProperty("Logger")!.SetValue(null, logger);
        var holder = core.GetType("ExileCore.Shared.Interfaces.ISettingsHolder")!;
        var childList = holder.GetProperty("Children")!;
        var parse = core.GetType("ExileCore.SettingsParser")!.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static)!;
        var pathOf = core.GetType("ExileCore.SettingsFavoritesTab")!.GetMethod("PathOf", BindingFlags.NonPublic | BindingFlags.Static)!;
        var entryType = pathOf.GetParameters()[0].ParameterType;
        var pluginInterface = core.GetType("ExileCore.Shared.Interfaces.IPlugin")!;
        foreach (string group in groups.Keys)
        {
            var owners = new List<(string Name, Type Settings)>();
            if (group == "ExileCore") owners.Add(("Core", core.GetType("ExileCore.CoreSettings")!));
            else
            {
                var dir = Path.Combine(root, "Plugins", "Compiled", group);
                dirs.Add(dir);
                var asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(dir, group + ".dll"));
                foreach (var plugin in asm.GetTypes().Where(t => !t.IsAbstract && !t.ContainsGenericParameters && pluginInterface.IsAssignableFrom(t)))
                {
                    Type? b = plugin.BaseType;
                    while (b != null && !(b.IsGenericType && b.GetGenericTypeDefinition().FullName == "ExileCore.BaseSettingsPlugin`1")) b = b.BaseType;
                    if (b is null) continue;
                    // BaseSettingsPlugin defaults to the namespace. These bundled
                    // plugins assign a different public name in their startup code.
                    var name = group switch
                    {
                        "BasicFlaskRoutine" => plugin.Name,
                        "HeistIcons" => "Heist Icons",
                        "PassiveSkillTreePlanter" => "Passive Skill Tree Planner",
                        "PickIt" => "Pickit",
                        _ => plugin.Namespace!
                    };
                    owners.Add((name, b.GenericTypeArguments[0]));
                    indexed.Add(name);
                }
            }
            foreach (var (owner, type) in owners)
            {
                var roots = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(holder))!;
                parse.Invoke(null, [Activator.CreateInstance(type), roots, -1]);
                var entry = Activator.CreateInstance(entryType)!;
                entryType.GetField("OwnerName")!.SetValue(entry, group == "ExileCore" && localized ? "核心" : owner);
                void Walk(IEnumerable nodes, List<object> parents, string pos)
                {
                    int i = 0;
                    foreach (var h in nodes)
                    {
                        string id = pos + "/" + i++;
                        parents.Add(h);
                        var arr = Array.CreateInstance(holder, parents.Count);
                        for (int j = 0; j < arr.Length; j++) arr.SetValue(parents[j], j);
                        entryType.GetField("Path")!.SetValue(entry, arr);
                        paths.Add(id, (string)pathOf.Invoke(null, [entry])!);
                        if (childList.GetValue(h) is IEnumerable children) Walk(children, parents, id);
                        parents.RemoveAt(parents.Count - 1);
                    }
                }
                Walk(roots, [], owner);
            }
        }
        if (!pluginNames.SetEquals(indexed)) throw new InvalidDataException("Favorite index and sidebar have different plugin owners");
        File.WriteAllText(file, JsonSerializer.Serialize(paths));
        Console.WriteLine($"Read {paths.Count} favorite paths.");
    }

    public static void Pair(string original, string translated, string output)
    {
        var before = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(original))!;
        var after = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(translated))!;
        if (!before.Keys.ToHashSet().SetEquals(after.Keys)) throw new InvalidDataException("Setting tree structure changed");
        var pairs = before.Select(k => new KeyValuePair<string, string>(k.Value, after[k.Key])).Distinct()
            .Where(k => k.Key != k.Value).ToList();
        var unique = pairs.GroupBy(k => k.Key).Where(g => g.Count() == 1).Select(g => g.Single())
            .GroupBy(k => k.Value).Where(g => g.Count() == 1).Select(g => g.Single()).ToDictionary();
        File.WriteAllText(output, JsonSerializer.Serialize(unique));
        Console.WriteLine($"Mapped {unique.Count} distinct favorite paths.");
    }
}
