using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Content.IntegrationTests.Fixtures;
using Robust.Shared.GameObjects;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.Serialization.Markdown.Value;

namespace Content.IntegrationTests.Tests.Persistence;

/// <summary>
/// Every saved field of every component (and of the data types inside them) has to be writable to a save file.
/// A dictionary keyed by something that doesn't turn into plain text can't be: saving it throws, and the world save
/// then has to drop that entity and everything on it (this happened with stocked smart fridges). The problem only
/// shows up once the dictionary has something in it, so nothing else catches it until a real save.
/// </summary>
[TestFixture]
public sealed class SaveableDataFieldsTest : GameTest
{
    [Test]
    public async Task DictionaryKeysCanBeSaved()
    {
        var server = Pair.Server;
        var factory = server.ResolveDependency<IComponentFactory>();
        var serialization = server.ResolveDependency<ISerializationManager>();

        var problems = new SortedSet<string>();
        var checkedKeys = new Dictionary<Type, bool>();
        var visited = new HashSet<Type>();

        bool KeyIsPlainText(Type key)
        {
            if (checkedKeys.TryGetValue(key, out var known))
                return known;

            // Data definitions and records write out as a mapping (several fields) unless they have their own
            // serializer that turns them into plain text (e.g. DeviceAddress).
            var isDataType = key.GetCustomAttribute<DataDefinitionAttribute>(true) != null ||
                             key.GetCustomAttribute<DataRecordAttribute>(true) != null ||
                             key.GetCustomAttribute<ImplicitDataDefinitionForInheritorsAttribute>(true) != null;

            bool ok;
            try
            {
                object? sample = key == typeof(string) ? "sample"
                    : key.IsValueType ? Activator.CreateInstance(key)
                    : key.GetConstructor(Type.EmptyTypes) != null ? Activator.CreateInstance(key)
                    : null;

                // Reference types we can't make a sample of (no default constructor) are given the benefit of the doubt.
                ok = sample == null ? !isDataType : serialization.WriteValue(key, sample, alwaysWrite: true) is ValueDataNode;
            }
            catch (Exception)
            {
                // A bare default value can fail to write for unrelated reasons (e.g. it needs an entity context, or a
                // required field is empty). Only data types are known not to be plain text.
                ok = !isDataType;
            }

            checkedKeys[key] = ok;
            return ok;
        }

        void CheckType(Type type, string path)
        {
            if (type.IsArray)
            {
                CheckType(type.GetElementType()!, path + "[]");
                return;
            }

            if (type.IsGenericType)
            {
                var args = type.GetGenericArguments();
                var def = type.GetGenericTypeDefinition();
                var isDictionary = type.GetInterfaces().Append(type).Any(i =>
                    i.IsGenericType && (i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>) ||
                                        i.GetGenericTypeDefinition() == typeof(IDictionary<,>)));

                if (isDictionary && args.Length == 2)
                {
                    if (!KeyIsPlainText(args[0]))
                        problems.Add($"{path}: dictionary keyed by {args[0].Name}");

                    CheckType(args[1], path + "{}");
                    return;
                }

                if (def != typeof(Nullable<>) && !typeof(System.Collections.IEnumerable).IsAssignableFrom(type))
                {
                    // e.g. ProtoId<T>: nothing nested to look at.
                    return;
                }

                foreach (var arg in args)
                {
                    CheckType(arg, path + "<>");
                }

                return;
            }

            if (!visited.Add(type))
                return;

            var isDataDefinition = type.GetCustomAttribute<DataDefinitionAttribute>(true) != null ||
                                   type.GetCustomAttribute<DataRecordAttribute>(true) != null ||
                                   type.GetCustomAttribute<ImplicitDataDefinitionForInheritorsAttribute>(true) != null;
            if (!isDataDefinition && !typeof(IComponent).IsAssignableFrom(type))
                return;

            CheckMembers(type, path);
        }

        void CheckMembers(Type type, string path)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (var field in t.GetFields(flags | BindingFlags.DeclaredOnly))
                {
                    if (field.GetCustomAttribute<DataFieldAttribute>() is { } attr && !attr.ReadOnly)
                        CheckType(field.FieldType, $"{path}.{field.Name}");
                }

                foreach (var prop in t.GetProperties(flags | BindingFlags.DeclaredOnly))
                {
                    if (prop.GetCustomAttribute<DataFieldAttribute>() is { } attr && !attr.ReadOnly)
                        CheckType(prop.PropertyType, $"{path}.{prop.Name}");
                }
            }
        }

        await server.WaitPost(() =>
        {
            foreach (var reg in factory.GetAllRegistrations())
            {
                if (reg.Unsaved)
                    continue;

                CheckType(reg.Type, reg.Name);
            }
        });

        Assert.That(problems, Is.Empty,
            "These saved fields can't be written to a save file once they have something in them:\n" + string.Join("\n", problems));
    }
}
