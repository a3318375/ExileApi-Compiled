using System.Collections;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Text.Json;

internal static class Verify
{
    public static void Run(string package, string catalog)
    {
        var root = Path.GetFullPath(package);
        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            var f = Path.Combine(root, name.Name + ".dll");
            return File.Exists(f) ? ctx.LoadFromAssemblyPath(f) : null;
        };
        var labels = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(
            File.ReadAllText(Path.Combine(catalog, "settings.json")))!;
        var core = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(root, "ExileCore.dll"));
        foreach (var (group, props) in labels)
        {
            var asm = group == "ExileCore" ? core : AssemblyLoadContext.Default.LoadFromAssemblyPath(
                Path.Combine(root, "Plugins", "Compiled", group, group + ".dll"));
            foreach (var (key, label) in props)
            {
                int split = key.LastIndexOf('.');
                var prop = asm.GetType(key[..split].Replace('/', '+'), throwOnError: true)!.GetProperty(key[(split + 1)..])!;
                var attr = prop.CustomAttributes.Single(a => a.AttributeType.FullName == "ExileCore.Shared.Attributes.MenuAttribute");
                if (!Equals(attr.ConstructorArguments[0].Value, label)) throw new InvalidDataException(key);
            }
        }
        var font = Activator.CreateInstance(core.GetType("ExileCore.CoreFontSettings")!)!;
        var main = font.GetType().GetProperty("MainFont")!.GetValue(font)!;
        if (!Equals(main.GetType().GetProperty("Value")!.GetValue(main), "unifont") ||
            !File.Exists(Path.Combine(root, "fonts", "unifont.otf"))) throw new InvalidDataException("Missing default CJK font");
        var cs = Activator.CreateInstance(core.GetType("ExileCore.CoreSettings")!)!;
        var holder = core.GetType("ExileCore.Shared.Interfaces.ISettingsHolder")!;
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(holder))!;
        core.GetType("ExileCore.SettingsParser")!.GetMethod("Parse", BindingFlags.Static | BindingFlags.Public)!
            .Invoke(null, [cs, list, -1]);
        var names = list.Cast<object>().Select(h => (string?)holder.GetProperty("Name")!.GetValue(h)).ToArray();
        if (!names.Contains("字体设置") || !names.Contains("插件设置"))
            throw new InvalidDataException("Core settings parser produced: " + string.Join(" / ", names));
        Console.WriteLine($"Windows loaded the localized assemblies and parsed {names.Length} top-level core settings using the bundled font.");
    }
}
