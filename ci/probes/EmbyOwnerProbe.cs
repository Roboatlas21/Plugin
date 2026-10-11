using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;

internal static class EmbyOwnershipProbe
{
    private static readonly BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static readonly Dictionary<short, OpCode> Codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(code => code.Value);

    private static void Main()
    {
        Console.WriteLine("=== Emby owner / parent relationship probe ===");
        Console.WriteLine("MediaBrowser.Controller assembly: " + typeof(BaseItem).Assembly.GetName().FullName);
        Console.WriteLine("BaseItem: " + typeof(BaseItem).FullName);

        var ownerProp = PrintProperty("OwnerId", typeof(Video));
        var parentProp = PrintProperty("ParentId");
        PrintProperty("ExtraType");
        Console.WriteLine("Owner-related BaseItem and Video members:");
        foreach (var t in new[] { typeof(BaseItem), typeof(Video), typeof(Movie) })
        {
            foreach (var m in t.GetMembers(Flags).Where(m => m.Name.Contains("owner", StringComparison.OrdinalIgnoreCase)))
                Console.WriteLine("  " + t.Name + ": " + m.MemberType + " " + m);
        }

        PrintMethod("GetParents");
        PrintMethod("GetParent");
        PrintMethod("GetOwner");

        Console.WriteLine("All GetParents overloads and IL calls:");
        foreach (var m in typeof(BaseItem).GetMethods(Flags).Where(m => m.Name == "GetParents"))
        {
            Console.WriteLine("  OVERLOAD: " + m);
            PrintCalls(m);
        }
        Console.WriteLine("All GetParent overloads and IL calls:");
        foreach (var m in typeof(BaseItem).GetMethods(Flags).Where(m => m.Name == "GetParent"))
        {
            Console.WriteLine("  OVERLOAD: " + m);
            PrintCalls(m);
        }

        Console.WriteLine("IL calls from BaseItem.GetParent:");
        PrintCalls(FindMethod("GetParent"));
        Console.WriteLine("IL calls from BaseItem.GetOwner:");
        PrintCalls(FindMethod("GetOwner"));

        Console.WriteLine("ILibraryManager.GetItemById overloads:");
        foreach (var m in typeof(ILibraryManager).GetMethods().Where(x => x.Name == "GetItemById"))
            Console.WriteLine("  " + m);
        Console.WriteLine("ILibraryManager.GetItemList overloads:");
        foreach (var m in typeof(ILibraryManager).GetMethods().Where(x => x.Name == "GetItemList"))
            Console.WriteLine("  " + m);

        Console.WriteLine("InternalItemsQuery filters:");
        var qt = typeof(InternalItemsQuery);
        foreach (string name in new[] { "Path", "ExtraTypes", "HasOwnerId", "GroupByPresentationUniqueKey" })
        {
            var p = qt.GetProperty(name, Flags);
            Console.WriteLine("  " + name + ": " + (p?.PropertyType.FullName ?? "(not found)"));
        }

        try
        {
            var item = new Movie();
            if (ownerProp?.CanWrite == true && parentProp?.CanWrite == true)
            {
                ownerProp.SetValue(item, NonEmpty(ownerProp.PropertyType));
                parentProp.SetValue(item, Default(parentProp.PropertyType));
                Console.WriteLine("Constructed Movie: nonempty OwnerId, empty ParentId.");
                var ancestors = item.GetParents().ToArray();
                Console.WriteLine("GetParents result count: " + ancestors.Length);
                Console.WriteLine("GetParents item types: " + string.Join(", ", ancestors.Select(x => x.GetType().Name)));
            }
            else Console.WriteLine("Cannot set OwnerId / ParentId for instance test.");
        }
        catch (Exception ex) { Console.WriteLine("Instance probe exception: " + ex); }
    }

    private static PropertyInfo? PrintProperty(string name, Type? declaringType = null)
    {
        var p = (declaringType ?? typeof(BaseItem)).GetProperty(name, Flags);
        Console.WriteLine(name + " property: " + (p == null ? "(not found)" : p.PropertyType.FullName + " / writable=" + p.CanWrite));
        return p;
    }

    private static MethodInfo? FindMethod(string name) => typeof(BaseItem).GetMethods(Flags)
        .Where(method => method.Name == name && method.GetParameters().Length == 0)
        .OrderBy(method => method.IsGenericMethod ? 1 : 0).FirstOrDefault();

    private static void PrintMethod(string name)
    {
        var m = FindMethod(name);
        Console.WriteLine(name + " method: " + (m?.ToString() ?? "(not found)"));
    }

    private static object? Default(Type type) => type.IsValueType ? Activator.CreateInstance(type) : null;
    private static object NonEmpty(Type type)
    {
        if (type == typeof(Guid)) return Guid.Parse("ca3bbab2-e5c3-4bf6-9140-df1c1c938095");
        if (type == typeof(string)) return "1337";
        if (type == typeof(long)) return 1337L;
        if (type == typeof(int)) return 1337;
        if (type == typeof(ulong)) return 1337UL;
        return Convert.ChangeType(1337, type);
    }

    private static void PrintCalls(MethodInfo? method)
    {
        if (method == null) { Console.WriteLine("  (method missing)"); return; }
        var state = method.GetCustomAttribute<IteratorStateMachineAttribute>();
        if (state != null)
        {
            Console.WriteLine("  iterator type: " + state.StateMachineType.FullName);
            method = state.StateMachineType.GetMethod("MoveNext", Flags) ?? method;
        }
        var bytes = method.GetMethodBody()?.GetILAsByteArray();
        if (bytes == null) { Console.WriteLine("  (no method body)"); return; }
        var found = new List<string>();
        var i = 0;
        while (i < bytes.Length)
        {
            short key = bytes[i] == 0xFE
                ? unchecked((short)((bytes[i++] << 8) | bytes[i++]))
                : bytes[i++];
            if (!Codes.TryGetValue(key, out var op)) { Console.WriteLine("  Unrecognized opcode " + key); break; }
            if (op.OperandType == OperandType.InlineMethod)
            {
                var token = BitConverter.ToInt32(bytes, i);
                try
                {
                    var called = method.Module.ResolveMethod(token, method.DeclaringType?.GetGenericArguments(), method.GetGenericArguments());
                    found.Add(called?.DeclaringType?.Name + "." + called?.Name);
                }
                catch (Exception e) { found.Add("unresolved call: " + e.GetType().Name); }
            }

            i += op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(bytes, i),
                _ => 4
            };
        }
        foreach (var call in found.Distinct()) Console.WriteLine("  calls " + call);
    }
}
