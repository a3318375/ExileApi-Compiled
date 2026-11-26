using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class Program
{
    const string Menu = "ExileCore.Shared.Attributes.MenuAttribute";

    static void Main(string[] args)
    {
        var catalog = Path.Combine(args[0], "localization", "zh-CN");
        var package = args[1];
        if (args.Length == 3 && args[2] == "--verify")
        {
            Verify.Run(package, catalog);
            return;
        }
        T Read<T>(string name) => JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(catalog, name)))!;
        var settings = Read<Dictionary<string, Dictionary<string, string>>>("settings.json");
        var tips = Read<Dictionary<string, string>>("tooltips.json");
        var ui = Read<Dictionary<string, string>>("core-text.json");
        var plugins = Read<Dictionary<string, Dictionary<string, Dictionary<string, string>>>>("plugin-text.json");
        var root = Path.GetFullPath(args[0]);
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(root);
        resolver.AddSearchDirectory(Path.GetDirectoryName(typeof(object).Assembly.Location));
        var options = new ReaderParameters { AssemblyResolver = resolver };
        using var core = AssemblyDefinition.ReadAssembly(Path.Combine(root, "ExileCore.dll"), options);
        var ctor = core.MainModule.GetType(Menu).Methods.Single(m => m.IsConstructor && m.Parameters.Count == 1);
        using (var native = new CoreImage(Path.Combine(root, "ExileCore.dll")))
        {
            foreach (var (key, label) in settings["ExileCore"])
            {
                var p = Property(core.MainModule, key);
                var menu = p.CustomAttributes.SingleOrDefault(a => a.AttributeType.FullName == Menu);
                bool hasTip = menu?.Constructor.Parameters.ElementAtOrDefault(1)?.ParameterType.MetadataType == MetadataType.String;
                native.SetLabel((int)p.MetadataToken.RID, (int)ctor.MetadataToken.RID, label, hasTip, tips);
            }
            native.TranslateLiterals(ui);
            native.Write(Path.Combine(package, "ExileCore.dll"));
        }
        foreach (var name in settings.Keys.Union(plugins.Keys).Where(n => n != "ExileCore"))
        {
            var relative = Path.Combine("Plugins", "Compiled", name, name + ".dll");
            resolver.AddSearchDirectory(Path.GetDirectoryName(Path.Combine(root, relative))!);
            using var dll = AssemblyDefinition.ReadAssembly(Path.Combine(root, relative), options);
            var module = dll.MainModule;
            if (settings.TryGetValue(name, out var fields))
            {
                foreach (var (key, label) in fields)
                {
                    var prop = Property(module, key);
                    var attr = prop.CustomAttributes.SingleOrDefault(a => a.AttributeType.FullName == Menu);
                    if (attr is null)
                    {
                        attr = new CustomAttribute(module.ImportReference(ctor));
                        prop.CustomAttributes.Add(attr);
                        attr.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String, label));
                    }
                    else
                    {
                        attr.ConstructorArguments[0] = new CustomAttributeArgument(module.TypeSystem.String, label);
                        if (attr.ConstructorArguments.Count > 1 && attr.ConstructorArguments[1].Type.MetadataType == MetadataType.String &&
                            attr.ConstructorArguments[1].Value is string tip && tip.Length != 0)
                            attr.ConstructorArguments[1] = new CustomAttributeArgument(module.TypeSystem.String, tips[tip]);
                    }
                }
            }
            if (plugins.TryGetValue(name, out var methods))
            {
                foreach (var (methodKey, translations) in methods)
                {
                    int separator = methodKey.LastIndexOf("::", StringComparison.Ordinal);
                    var type = module.GetType(methodKey[..separator]) ?? throw new InvalidDataException(methodKey);
                    var method = methodKey[(separator + 2)..];
                    var missing = translations.Keys.ToHashSet();
                    foreach (var m in MenuMethods(type, method))
                    {
                        foreach (var i in m.Body.Instructions)
                        {
                            if (i.OpCode != OpCodes.Ldstr || i.Operand is not string source || !translations.TryGetValue(source, out var target)) continue;
                            i.Operand = target;
                            missing.Remove(source);
                        }
                    }
                    if (missing.Count != 0) throw new InvalidDataException($"{methodKey}: missing {string.Join(", ", missing)}");
                }
            }
            module.Write(Path.Combine(package, relative));
        }
        Console.WriteLine($"Localized {settings.Sum(s => s.Value.Count)} settings and {ui.Count} core strings.");
    }

    // Callbacks written inside a menu method can live on a compiler-generated
    // closure type. Match their owning method, never a whole plugin assembly.
    static IEnumerable<MethodDefinition> MenuMethods(TypeDefinition type, string method)
    {
        var prefix = "<" + method + ">";
        var methods = type.Methods.Where(m => m.HasBody && (m.Name == method || m.Name.StartsWith(prefix, StringComparison.Ordinal)))
            .Concat(type.NestedTypes.SelectMany(t => t.Methods)
                .Where(m => m.HasBody && m.Name.StartsWith(prefix, StringComparison.Ordinal)));
        foreach (var m in methods)
        {
            yield return m;
            foreach (var a in m.CustomAttributes.Where(a => a.AttributeType.FullName is
                "System.Runtime.CompilerServices.AsyncStateMachineAttribute" or "System.Runtime.CompilerServices.IteratorStateMachineAttribute"))
            {
                var state = ((TypeReference)a.ConstructorArguments[0].Value).Resolve();
                yield return state.Methods.Single(x => x.Name == "MoveNext");
            }
        }
    }

    static PropertyDefinition Property(ModuleDefinition module, string key)
    {
        int split = key.LastIndexOf('.');
        var type = module.GetType(key[..split]);
        return type?.Properties.SingleOrDefault(p => p.Name == key[(split + 1)..]) ??
            throw new InvalidDataException($"Missing setting {module.Name}: {key}");
    }
}
