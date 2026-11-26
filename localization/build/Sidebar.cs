using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class Sidebar
{
    // The sidebar passes Name + "###select" to ImGui. Keep the name used by
    // the plugin manager, the folder/filter logic, and the original widget ID.
    public static void Translate(string root, string package, Dictionary<string, string> names, ReaderParameters options)
    {
        const string dll = "Hexa.NET.ImGui.dll";
        const string suffix = "###select";
        using var module = ModuleDefinition.ReadModule(Path.Combine(root, dll), options);
        var imgui = module.GetType("Hexa.NET.ImGui.ImGui");
        var select = imgui.Methods.Where(m => m.Name == "Selectable" && m.HasBody &&
            m.Parameters.Count > 0 && m.Parameters[0].ParameterType.MetadataType == MetadataType.String).ToList();
        if (select.Count == 0) throw new InvalidDataException("Missing ImGui.Selectable(string, ...)");
        var lookup = new MethodDefinition("LocalizePluginMenuLabel", MethodAttributes.Private | MethodAttributes.Static, module.TypeSystem.String);
        lookup.Parameters.Add(new ParameterDefinition("label", ParameterAttributes.None, module.TypeSystem.String));
        imgui.Methods.Add(lookup);
        var il = lookup.Body.GetILProcessor();
        var original = il.Create(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, original);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldstr, suffix);
        il.Emit(OpCodes.Ldc_I4, (int)StringComparison.Ordinal);
        il.Emit(OpCodes.Callvirt, module.ImportReference(typeof(string).GetMethod(nameof(string.EndsWith), [typeof(string), typeof(StringComparison)])!));
        il.Emit(OpCodes.Brfalse, original);
        var equals = module.ImportReference(typeof(string).GetMethod("op_Equality", [typeof(string), typeof(string)])!);
        foreach (var (name, chinese) in names)
        {
            var next = il.Create(OpCodes.Nop);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldstr, name + suffix);
            il.Emit(OpCodes.Call, equals);
            il.Emit(OpCodes.Brfalse_S, next);
            il.Emit(OpCodes.Ldstr, $"{chinese} ({name}){suffix}");
            il.Emit(OpCodes.Ret);
            il.Append(next);
        }
        il.Append(original);
        il.Emit(OpCodes.Ret);
        foreach (var overload in select)
        {
            var body = overload.Body.GetILProcessor();
            var first = overload.Body.Instructions[0];
            body.InsertBefore(first, body.Create(OpCodes.Ldarg, overload.Parameters[0]));
            body.InsertBefore(first, body.Create(OpCodes.Call, lookup));
            body.InsertBefore(first, body.Create(OpCodes.Starg, overload.Parameters[0]));
        }
        module.Write(Path.Combine(package, dll));
    }
}
