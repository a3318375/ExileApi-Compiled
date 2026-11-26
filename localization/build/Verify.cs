using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Hexa.NET.ImGui;
using Newtonsoft.Json.Linq;
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
        Directory.SetCurrentDirectory(root);
        VerifyFavorites(core);
        DrawMenu(core);
        Console.WriteLine($"Windows loaded the localized assemblies and parsed {names.Length} top-level core settings using the bundled font.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void VerifyFavorites(Assembly core)
    {
        var converter = Assembly.Load("MenuZhCN");
        using var stream = converter.GetManifestResourceStream("Favorites.json")!;
        var map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
        var (english, chinese) = map.First(p => p.Key.StartsWith("Core++") && p.Value.Contains("字号"));
        const string external = "MyOtherPlugin++My own label";
        var input = new JObject { ["FavoriteSettings"] = new JObject { ["Content"] = new JArray(
            new JObject { ["Value"] = english }, new JObject { ["Value"] = external }) } };
        var conf = core.GetType("ExileCore.CorePluginSettings")!;
        var serializer = Newtonsoft.Json.JsonSerializer.CreateDefault();
        var instance = input.ToObject(conf, serializer)!;
        var node = conf.GetProperty("FavoriteSettings")!.GetValue(instance)!;
        var entries = ((IList)node.GetType().GetProperty("Content")!.GetValue(node)!).Cast<object>()
            .Select(v => v.GetType().GetProperty("Value")!.GetValue(v)).ToArray();
        if (!entries.SequenceEqual(new[] { chinese, external })) throw new InvalidDataException("Loading English favorites");
        var saved = JObject.FromObject(instance, serializer)["FavoriteSettings"]!["Content"]!;
        if ((string?)saved[0]!["Value"] != english || (string?)saved[1]!["Value"] != external)
            throw new InvalidDataException("Saving compatible favorite paths");
    }

    // Exercise the native ReadyToRun help panel through the real ImGui library
    // without starting a game or an overlay window.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void DrawMenu(Assembly core)
    {
        var cwd = Directory.GetCurrentDirectory();
        var scratch = Directory.CreateTempSubdirectory("exile-menu-");
        var log = Path.Combine(scratch.FullName, "menu.txt");
        ImGui.CreateContext();
        try
        {
            Directory.SetCurrentDirectory(scratch.FullName);
            var io = ImGui.GetIO();
            io.DisplaySize = new System.Numerics.Vector2(1100, 700);
            io.DeltaTime = 1f / 60;
            io.BackendFlags |= ImGuiBackendFlags.RendererHasTextures;
            ImGui.NewFrame();
            ImGui.SetNextWindowSize(new System.Numerics.Vector2(1000, 600));
            ImGui.Begin("F12");
            ImGui.LogToFile(0, log);
            var menu = core.GetType("ExileCore.MenuWindow")!;
            menu.GetMethod("DrawHelpMenu", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(RuntimeHelpers.GetUninitializedObject(menu), null);
            ImGui.Selectable("HealthBars###select");
            ImGui.LogFinish();
            ImGui.End();
            ImGui.EndFrame();
            var visible = File.ReadAllText(log);
            if (!visible.Contains("本软件可免费使用。") || !visible.Contains("打开使用指南") ||
                !visible.Contains("生命与能量护盾条 (HealthBars)")) throw new InvalidDataException(visible);
            Console.WriteLine("Rendered the Chinese help panel and bilingual plugin selector.");
        }
        finally
        {
            ImGui.DestroyContext();
            Directory.SetCurrentDirectory(cwd);
            scratch.Delete(recursive: true);
        }
    }
}
