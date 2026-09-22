// Copyright (C) KitWright. All rights reserved.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using AssembliesType = UnityEditor.Compilation.AssembliesType;
using CompilationPipeline = UnityEditor.Compilation.CompilationPipeline;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("Reflection")]
    internal static class ReflectionFunctions
    {
        internal const BindingFlags DeclaredMembers =
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        internal const BindingFlags InheritedMembers =
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

        private const int MaxCandidates = 10;
        private const int MaxSearchResults = 25;
        private const int TypoPrefixLength = 4;

        internal static readonly string[] SearchScopes = { "unity", "packages", "project", "all" };

        private static Dictionary<string, List<Type>> s_byShortName;
        private static Dictionary<string, Type> s_byFullName;
        private static List<MethodInfo> s_extensionMethods;
        private static Dictionary<string, string> s_assemblyOrigin;
        private static readonly object s_lock = new object();

        [Description("Inspect the live C# API by reflection — verify a type and its members exist before writing an execute_code snippet. " +
                     "Without 'member': declared members of the type, names only (methods, properties, fields, events, applicable extension methods, obsolete members; enum values for enums). " +
                     "With 'member': full signatures including parameter names, falling back to inherited members and then to extension methods. " +
                     "With 'search': type-name lookup across loaded assemblies, ranked exact > prefix > contains, narrowed by 'scope'. " +
                     "Unresolved names return candidate suggestions, so a typo costs one call instead of a failed compile.")]
        [ReadOnlyTool]
        public static object ReflectApi(
            [ToolParam("Type name, short ('Rigidbody', 'Mathf') or fully qualified ('UnityEngine.Rigidbody'). Omit when using 'search'.", Required = false)] string name = null,
            [ToolParam("Member name to get full signatures for. Omit to list member names.", Required = false)] string member = null,
            [ToolParam("Look types up by substring instead of resolving one name. Ranked exact > prefix > contains.", Required = false)] string search = null,
            [ToolParam("Scope for 'search': unity (default), packages, project, all.", Required = false)] string scope = null,
            [ToolParam("Also report non-public members. Off by default.", Required = false)] bool includeNonPublic = false)
        {
            if (!string.IsNullOrWhiteSpace(search))
                return SearchTypes(search.Trim(), scope);

            if (string.IsNullOrWhiteSpace(name))
                return Response.Error("EMPTY_NAME", new { hint = "Pass 'name' to inspect a type, or 'search' to look one up by substring." });

            var type = Resolve(name, out var matches, out var ambiguous);

            if (ambiguous)
                return Response.Error("AMBIGUOUS_TYPE", new
                {
                    query = name,
                    matches,
                    hint = "Several loaded types share this short name. Re-run with the fully qualified name."
                });

            if (type == null)
                return Response.Error("TYPE_NOT_FOUND", new { query = name, candidates = matches });

            // Reflecting over members of an open generic definition segfaults Mono, so stop at the type header.
            if (type.IsGenericTypeDefinition)
                return Response.Success($"Type header for '{type.Name}' (open generic).", Header(type, new
                {
                    is_generic_type_definition = true,
                    hint = "Open generic type — query a closed type or consult the docs for member details."
                }));

            // The short name was shared and the Unity type won. Say so in the message rather than
            // letting a caller who meant System.Random read Unity's members as if they were the answer.
            var alsoNamed = matches.Length > 0
                ? $" Resolved to '{type.FullName}'; also loaded: {string.Join(", ", matches)}."
                : string.Empty;

            return string.IsNullOrWhiteSpace(member)
                ? DescribeType(type, includeNonPublic, alsoNamed)
                : DescribeMember(type, member.Trim(), includeNonPublic, alsoNamed);
        }

        private static object DescribeType(Type type, bool includeNonPublic, string alsoNamed)
        {
            if (type.IsEnum)
                return Response.Success($"Enum '{type.Name}' has {Enum.GetNames(type).Length} values.{alsoNamed}",
                    Header(type, new { values = Enum.GetNames(type) }));

            var flags = WithVisibility(DeclaredMembers, includeNonPublic);

            var methods = type.GetMethods(flags).Where(m => !m.IsSpecialName).Select(m => m.Name);
            var properties = type.GetProperties(flags).Select(p => p.Name);
            var fields = type.GetFields(flags).Select(f => f.Name);
            var events = type.GetEvents(flags).Select(e => e.Name);

            var obsolete = type.GetMembers(flags)
                .Where(m => !(m is MethodInfo method && method.IsSpecialName) && IsObsolete(m))
                .Select(m => m.Name);

            var extensions = ExtensionMethodsFor(type).Select(m => m.Name);

            return Response.Success($"Declared members of '{type.Name}'.{alsoNamed}", Header(type, new
            {
                members = new
                {
                    methods = Sorted(methods),
                    properties = Sorted(properties),
                    fields = Sorted(fields),
                    events = Sorted(events)
                },
                extension_methods = Sorted(extensions),
                obsolete_members = Sorted(obsolete),
                hint = "Names only — pass 'member' for full signatures. Inherited members are omitted here but still resolve when queried by name."
            }));
        }

        private static object DescribeMember(Type type, string member, bool includeNonPublic, string alsoNamed)
        {
            var signatures = Signatures(type, member, WithVisibility(DeclaredMembers, includeNonPublic));
            var inherited = signatures.Length == 0;
            if (inherited)
                signatures = Signatures(type, member, WithVisibility(InheritedMembers, includeNonPublic));

            if (signatures.Length == 0)
            {
                var extensions = ExtensionMethodsFor(type)
                    .Where(m => NameMatches(m.Name, member))
                    .ToArray();

                if (extensions.Length > 0)
                    return Response.Success($"{extensions.Length} extension signature(s) for '{type.Name}.{member}'.{alsoNamed}", new
                    {
                        type = type.FullName,
                        member,
                        extension = true,
                        declaring_types = Sorted(extensions.Select(m => m.DeclaringType?.FullName)),
                        signatures = extensions.Select(m => MethodSignature(m, skipFirstParameter: true)).ToArray()
                    });

                return Response.Error("MEMBER_NOT_FOUND", new
                {
                    type = type.FullName,
                    query = member,
                    candidates = SimilarMembers(type, member)
                });
            }

            return Response.Success($"{signatures.Length} signature(s) for '{type.Name}.{member}'.{alsoNamed}", new
            {
                type = type.FullName,
                member,
                inherited,
                declaring_types = inherited ? DeclaringTypes(type, member, includeNonPublic) : null,
                signatures
            });
        }

        private static string[] DeclaringTypes(Type type, string member, bool includeNonPublic) =>
            Sorted(type
                .GetMember(member, WithVisibility(InheritedMembers, includeNonPublic) | BindingFlags.IgnoreCase)
                .Select(m => m.DeclaringType?.FullName));

        private static object SearchTypes(string query, string scope)
        {
            scope = string.IsNullOrWhiteSpace(scope) ? "unity" : scope.Trim().ToLowerInvariant();

            if (Array.IndexOf(SearchScopes, scope) < 0)
                return Response.Error("INVALID_SCOPE", new { scope, supported = SearchScopes });

            EnsureIndex();

            var ranked = new List<(Type Type, int Rank)>();

            foreach (var candidates in s_byShortName.Values)
            {
                foreach (var type in candidates)
                {
                    if (!MatchesScope(type.Assembly.GetName().Name, scope))
                        continue;

                    var rank = Rank(type, query);
                    if (rank >= 0)
                        ranked.Add((type, rank));
                }
            }

            var results = ranked
                .OrderBy(hit => hit.Rank)
                .ThenBy(hit => hit.Type.FullName, StringComparer.Ordinal)
                .Take(MaxSearchResults)
                .Select(hit => new
                {
                    name = hit.Type.Name,
                    full_name = hit.Type.FullName,
                    @namespace = hit.Type.Namespace,
                    assembly = hit.Type.Assembly.GetName().Name,
                    is_enum = hit.Type.IsEnum,
                    is_interface = hit.Type.IsInterface,
                    is_struct = hit.Type.IsValueType && !hit.Type.IsEnum
                })
                .ToArray();

            return Response.Success($"Found {results.Length} type(s) matching '{query}' (scope: {scope}).", new
            {
                query,
                scope,
                count = results.Length,
                truncated = ranked.Count > results.Length,
                results
            });
        }

        private static int Rank(Type type, string query)
        {
            if (string.Equals(type.Name, query, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type.FullName, query, StringComparison.OrdinalIgnoreCase))
                return 0;

            if (type.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                return 1;

            if (type.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                (type.FullName?.IndexOf(query, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0)
                return 2;

            return -1;
        }

        // "project" and "packages" come from the compilation pipeline rather than the assembly name:
        // a package asmdef compiles into the same ScriptAssemblies folder as project code, so only
        // its source path separates the two.
        internal static bool MatchesScope(string assemblyName, string scope)
        {
            if (scope == "all")
                return true;

            if (scope == "unity")
                return assemblyName.StartsWith("UnityEngine", StringComparison.Ordinal)
                    || assemblyName.StartsWith("UnityEditor", StringComparison.Ordinal)
                    || assemblyName.StartsWith("Unity.", StringComparison.Ordinal);

            s_assemblyOrigin = s_assemblyOrigin ?? CompilationPipeline.GetAssemblies(AssembliesType.Editor)
                .Where(a => a.sourceFiles.Length > 0)
                .ToDictionary(
                    a => a.name,
                    a => a.sourceFiles[0].StartsWith("Packages/", StringComparison.Ordinal) ? "packages" : "project",
                    StringComparer.Ordinal);

            return s_assemblyOrigin.TryGetValue(assemblyName, out var origin) && origin == scope;
        }

        private static MethodInfo[] ExtensionMethodsFor(Type type)
        {
            EnsureIndex();
            return s_extensionMethods.Where(m => ExtendsType(m, type)).ToArray();
        }

        private static bool ExtendsType(MethodInfo method, Type target)
        {
            var parameter = method.GetParameters()[0].ParameterType;

            if (parameter.IsGenericParameter)
                return parameter.GetGenericParameterConstraints().All(c => c.IsAssignableFrom(target));

            if (parameter.IsAssignableFrom(target))
                return true;

            if (!parameter.IsGenericType || !parameter.ContainsGenericParameters)
                return false;

            var definition = parameter.GetGenericTypeDefinition();
            return target.GetInterfaces()
                .Concat(new[] { target })
                .Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == definition);
        }

        internal static string[] Signatures(Type type, string member, BindingFlags flags)
        {
            var results = new List<string>();

            foreach (var m in type.GetMethods(flags).Where(m => NameMatches(m.Name, member) && !m.IsSpecialName))
                results.Add(MethodSignature(m, skipFirstParameter: false));

            foreach (var p in type.GetProperties(flags).Where(p => NameMatches(p.Name, member)))
                results.Add(Obsolete(p) + Modifiers(p.GetMethod?.IsStatic ?? false) +
                            $"{Short(p.PropertyType)} {p.Name} {{ {(p.CanRead ? "get; " : "")}{(p.CanWrite ? "set; " : "")}}}");

            foreach (var f in type.GetFields(flags).Where(f => NameMatches(f.Name, member)))
                results.Add(Obsolete(f) + Modifiers(f.IsStatic) + $"{Short(f.FieldType)} {f.Name}");

            foreach (var e in type.GetEvents(flags).Where(e => NameMatches(e.Name, member)))
                results.Add(Obsolete(e) + $"event {Short(e.EventHandlerType)} {e.Name}");

            return results.ToArray();
        }

        /// <summary>
        /// Resolve a type name across every loaded assembly. Returns null with <paramref name="ambiguous"/>
        /// set when a short name hits more than one type, so callers never silently pick the wrong one.
        /// </summary>
        internal static Type Resolve(string name, out string[] matches, out bool ambiguous)
        {
            matches = Array.Empty<string>();
            ambiguous = false;

            if (string.IsNullOrWhiteSpace(name))
                return null;

            var query = name.Trim();
            EnsureIndex();

            if (s_byFullName.TryGetValue(query, out var exact))
                return exact;

            var sameName = ExactCase(query) ?? IgnoringCase(query);
            if (sameName != null)
            {
                if (sameName.Count == 1)
                    return sameName[0];

                // A short name shared by one top-level type and some nested ones means the
                // top-level one: nobody types "Physics" meaning Skeleton+Physics.
                var topLevel = sameName.Where(t => !t.IsNested).ToList();
                if (topLevel.Count == 1)
                    return topLevel[0];

                // Inside a Unity editor, a bare "Debug" or "Random" means the Unity one. Pick it,
                // but hand back what else carries the name so a caller after System.Random can see it.
                var unity = (topLevel.Count > 0 ? topLevel : sameName).Where(IsUnityType).ToList();
                if (unity.Count == 1)
                {
                    matches = FullNames(sameName.Where(t => t != unity[0]));
                    return unity[0];
                }

                ambiguous = true;
                matches = FullNames(topLevel.Count > 1 ? topLevel : sameName);
                return null;
            }

            matches = SimilarTypes(query);
            return null;
        }

        private static bool IsUnityType(Type type) =>
            type.Namespace != null &&
            (type.Namespace == "UnityEngine" || type.Namespace.StartsWith("UnityEngine.", StringComparison.Ordinal) ||
             type.Namespace == "UnityEditor" || type.Namespace.StartsWith("UnityEditor.", StringComparison.Ordinal));

        private static string[] FullNames(IEnumerable<Type> types) =>
            types.Select(t => t.FullName).OrderBy(n => n, StringComparer.Ordinal).Take(MaxCandidates).ToArray();

        private static List<Type> ExactCase(string query) =>
            s_byShortName.TryGetValue(query, out var hit) ? hit : null;

        // Type names are case-sensitive in C#; only fall back to a loose match when the exact
        // spelling misses, otherwise Mathf and System.MathF collide into a false ambiguity.
        private static List<Type> IgnoringCase(string query)
        {
            var loose = s_byShortName
                .Where(pair => string.Equals(pair.Key, query, StringComparison.OrdinalIgnoreCase))
                .SelectMany(pair => pair.Value)
                .ToList();

            return loose.Count > 0 ? loose : null;
        }

        private static void EnsureIndex()
        {
            if (s_byShortName != null)
                return;

            lock (s_lock)
            {
                if (s_byShortName != null)
                    return;

                var byShortName = new Dictionary<string, List<Type>>(StringComparer.Ordinal);
                var byFullName = new Dictionary<string, Type>(StringComparer.Ordinal);
                var extensions = new List<MethodInfo>();

                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    foreach (var type in SafeGetTypes(assembly))
                    {
                        // Nested public types report IsPublic false; without IsNestedPublic every
                        // enum declared inside a class (Camera.GateFitMode and friends) is invisible.
                        if (type == null || !(type.IsPublic || type.IsNestedPublic))
                            continue;

                        if (!byShortName.TryGetValue(type.Name, out var list))
                        {
                            list = new List<Type>();
                            byShortName[type.Name] = list;
                        }
                        list.Add(type);

                        if (type.FullName != null && !byFullName.ContainsKey(type.FullName))
                        {
                            byFullName[type.FullName] = type;

                            // Reflection spells a nested type UnityEngine.Camera+GateFitMode; C# source
                            // spells it UnityEngine.Camera.GateFitMode, and an agent usually drops the
                            // namespace entirely. Index both so either spelling resolves.
                            if (type.IsNested)
                            {
                                Alias(byFullName, type.FullName.Replace('+', '.'), type);
                                Alias(byFullName, $"{type.DeclaringType?.Name}.{type.Name}", type);
                            }
                        }

                        CollectExtensionMethods(type, extensions);
                    }
                }

                s_extensionMethods = extensions;
                s_byFullName = byFullName;
                s_byShortName = byShortName;
            }
        }

        private static void Alias(Dictionary<string, Type> byFullName, string key, Type type)
        {
            if (!string.IsNullOrEmpty(key) && !byFullName.ContainsKey(key))
                byFullName[key] = type;
        }

        private static void CollectExtensionMethods(Type type, List<MethodInfo> into)
        {
            if (!type.IsSealed || !type.IsAbstract || !type.IsDefined(typeof(ExtensionAttribute), false))
                return;

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (method.IsDefined(typeof(ExtensionAttribute), false) && method.GetParameters().Length > 0)
                    into.Add(method);
            }
        }

        private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                // Partially loaded assembly: keep whatever did resolve.
                return ex.Types.Where(t => t != null);
            }
            catch (Exception)
            {
                return Array.Empty<Type>();
            }
        }

        private static string[] SimilarTypes(string query)
        {
            var prefix = query.Length <= TypoPrefixLength ? query : query.Substring(0, TypoPrefixLength);

            return s_byShortName.Keys
                .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                            k.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(k => Math.Abs(k.Length - query.Length))
                .ThenBy(k => k, StringComparer.OrdinalIgnoreCase)
                .Take(MaxCandidates)
                .ToArray();
        }

        private static string[] SimilarMembers(Type type, string member)
        {
            var prefix = member.Length <= TypoPrefixLength ? member : member.Substring(0, TypoPrefixLength);

            return type.GetMembers(InheritedMembers)
                .Select(m => m.Name)
                .Concat(ExtensionMethodsFor(type).Select(m => m.Name))
                .Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                            n.IndexOf(member, StringComparison.OrdinalIgnoreCase) >= 0)
                .Distinct()
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Take(MaxCandidates)
                .ToArray();
        }

        private static object Header(Type type, object extra)
        {
            var header = new Dictionary<string, object>
            {
                ["name"] = type.Name,
                ["full_name"] = type.FullName,
                ["namespace"] = type.Namespace,
                ["assembly"] = type.Assembly.GetName().Name,
                ["base_class"] = type.BaseType?.FullName,
                ["interfaces"] = Sorted(type.GetInterfaces().Select(i => Short(i))),
                ["is_static"] = type.IsAbstract && type.IsSealed,
                ["is_abstract"] = type.IsAbstract && !type.IsSealed,
                ["is_sealed"] = type.IsSealed && !type.IsAbstract,
                ["is_interface"] = type.IsInterface,
                ["is_enum"] = type.IsEnum
            };

            foreach (var property in extra.GetType().GetProperties())
                header[property.Name] = property.GetValue(extra);

            return header;
        }

        private static bool NameMatches(string candidate, string member) =>
            string.Equals(candidate, member, StringComparison.OrdinalIgnoreCase);

        private static BindingFlags WithVisibility(BindingFlags flags, bool includeNonPublic) =>
            includeNonPublic ? flags | BindingFlags.NonPublic : flags;

        // Signatures are meant to be pasted into an execute_code snippet, so they carry everything
        // the call site needs to compile: out/ref/in/params, optional defaults, generic arguments.
        private static string MethodSignature(MethodInfo method, bool skipFirstParameter) =>
            Obsolete(method) + Modifiers(method, skipFirstParameter) +
            $"{Short(method.ReturnType)} {MethodName(method)}({Parameters(method, skipFirstParameter)})";

        private static string MethodName(MethodInfo method) =>
            method.IsGenericMethod
                ? $"{method.Name}<{string.Join(", ", method.GetGenericArguments().Select(a => a.Name))}>"
                : method.Name;

        private static string Parameters(MethodInfo method, bool skipFirst = false) =>
            string.Join(", ", method.GetParameters().Skip(skipFirst ? 1 : 0).Select(Parameter));

        private static string Parameter(ParameterInfo parameter)
        {
            var byRef = parameter.ParameterType.IsByRef;
            var prefix = parameter.IsOut ? "out "
                : byRef && parameter.IsIn ? "in "
                : byRef ? "ref "
                : parameter.IsDefined(typeof(ParamArrayAttribute)) ? "params "
                : string.Empty;

            var type = Short(byRef ? parameter.ParameterType.GetElementType() : parameter.ParameterType);
            var fallback = parameter.HasDefaultValue ? $" = {DefaultValue(parameter.DefaultValue)}" : string.Empty;

            return $"{prefix}{type} {parameter.Name}{fallback}";
        }

        private static string DefaultValue(object value)
        {
            switch (value)
            {
                case null: return "null";
                case string text: return $"\"{text}\"";
                case bool flag: return flag ? "true" : "false";
                case IFormattable formattable: return formattable.ToString(null, CultureInfo.InvariantCulture);
                default: return value.ToString();
            }
        }

        private static string Modifiers(MethodInfo method, bool asExtension)
        {
            if (asExtension)
                return string.Empty;
            if (method.IsStatic)
                return "static ";
            if (method.IsAbstract)
                return "abstract ";

            return method.IsVirtual && !method.IsFinal ? "virtual " : string.Empty;
        }

        private static string Modifiers(bool isStatic) => isStatic ? "static " : string.Empty;

        private static bool IsObsolete(MemberInfo member) =>
            member.GetCustomAttribute<ObsoleteAttribute>() != null;

        private static string Obsolete(MemberInfo member)
        {
            var attribute = member.GetCustomAttribute<ObsoleteAttribute>();
            if (attribute == null)
                return string.Empty;

            return string.IsNullOrEmpty(attribute.Message) ? "[Obsolete] " : $"[Obsolete: {attribute.Message}] ";
        }

        // Signatures get pasted straight into execute_code snippets, so emit C# keywords
        // rather than CLR type names ("float x", not "Single x").
        private static readonly Dictionary<string, string> CSharpKeywords = new Dictionary<string, string>
        {
            ["Void"] = "void",
            ["Boolean"] = "bool",
            ["Byte"] = "byte",
            ["SByte"] = "sbyte",
            ["Char"] = "char",
            ["Int16"] = "short",
            ["UInt16"] = "ushort",
            ["Int32"] = "int",
            ["UInt32"] = "uint",
            ["Int64"] = "long",
            ["UInt64"] = "ulong",
            ["Single"] = "float",
            ["Double"] = "double",
            ["Decimal"] = "decimal",
            ["String"] = "string",
            ["Object"] = "object"
        };

        private static string Short(Type type)
        {
            if (type == null)
                return "void";

            if (type.IsArray)
                return Short(type.GetElementType()) + "[]";

            var name = type.Name;

            if (type.Namespace == "System" && CSharpKeywords.TryGetValue(name, out var keyword))
                return keyword;

            var tick = name.IndexOf('`');
            if (tick < 0)
                return name;

            var args = type.GetGenericArguments().Select(Short);
            return $"{name.Substring(0, tick)}<{string.Join(", ", args)}>";
        }

        private static string[] Sorted(IEnumerable<string> names) =>
            names.Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
