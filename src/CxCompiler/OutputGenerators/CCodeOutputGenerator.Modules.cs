using CxCompiler.Model.Common;
using CxCompiler.Model.Project;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;
using System.Text;

namespace CxCompiler.OutputGenerators;

public static partial class CCodeOutputGenerator
{
    private static void WriteModuleInfoDeclaration(IndentingWriter writer, CxProject project)
    {
        writer.WriteLine("struct cx_module_info;");
        writer.WriteLine($"extern {GetModuleDataApiName(project.Name)} const struct cx_module_info {GetModuleToken(project.Name)}___module_info;");
        writer.WriteLine($"extern {GetModuleApiName(project.Name)} const struct cx_module_info *__cx_module_info_{GetModuleToken(project.Name)}(void);");
        foreach (var type in GetModulePublicTypes(GetDeclarations(project)).OfType<DelegateDeclaration>())
            writer.WriteLine($"extern {GetModuleDataApiName(project.Name)} struct cxcore_System_Reflection_TypeInfo {new QualifiedIdentifier(project.Name, type.FullName, "__typeinfo").ToCIdentifier()};");
    }

    private static IEnumerable<DeclarationBase> GetModulePublicTypes(
        IEnumerable<DeclarationBase> declarations)
    {
        foreach (var declaration in declarations)
        {
            if (declaration is ClassDeclaration { Visibility: Visibility.Public } owner)
            {
                yield return owner;
                foreach (var nested in GetModulePublicTypes(owner.MemberDeclarations.Declarations))
                    yield return nested;
            }
            else if (declaration is EnumDeclaration { Visibility: Visibility.Public } or DelegateDeclaration { Visibility: Visibility.Public })
                yield return declaration;
        }
    }

    private static void WriteModuleInfoDefinition(IndentingWriter writer, CxProject project)
    {
        writer.WriteLine($"#if !defined(CX_MODULE_METADATA_FORMAT_VERSION) || CX_MODULE_METADATA_FORMAT_VERSION != {CurrentModuleMetadataFormatVersion}");
        writer.WriteLine($"#error This generated module requires module metadata format {CurrentModuleMetadataFormatVersion}.");
        writer.WriteLine("#endif");
        var token = GetModuleToken(project.Name);
        var types = GetModulePublicTypes(GetDeclarations(project))
            .OrderBy(type => type.FullName.ToString(), StringComparer.Ordinal).ToArray();
        var references = project.ResolvedProjectNames.Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        foreach (var type in types.OfType<DelegateDeclaration>())
        {
            var name = new QualifiedIdentifier("__name", type.FullName).ToCIdentifier();
            var ns = new QualifiedIdentifier("__namespace", type.Namespace).ToCIdentifier();
            var size = ContainsOpenGenericParameter(type.Signature) ? "0" : $"sizeof({GetFunctionTypeCIdentifier(type.Signature)})";
            writer.WriteLine($"{GetModuleDataApiName(project.Name)} struct cxcore_System_Reflection_TypeInfo {new QualifiedIdentifier(project.Name, type.FullName, "__typeinfo").ToCIdentifier()} = {{");
            writer.WriteLine($"    .Hash = 0x{GetTypeNameHash(type.FullName, project.Name):X}ULL,");
            writer.WriteLine($"    ._module = (cx_ptr)&{token}___module_info,");
            writer.WriteLine("    .Flags = CX_REFLECTION_FLAG_VISIBILITY_PUBLIC | CX_REFLECTION_FLAG_TYPE_DELEGATE,");
            writer.WriteLine($"    .Size = {size}, .Name = &{name}, .Namespace = &{ns},");
            writer.WriteLine("};");
        }
        var annotations = WriteModuleAnnotations(writer, project, types);
        var arrayReaders = WriteModuleArrayReaders(writer, project, types);
        var genericTypes = WriteModuleGenericArguments(writer, project);
        if (types.Length != 0)
        {
            writer.WriteLine($"static const struct cxcore_System_Reflection_TypeInfo *const {token}___module_types[] = {{");
            foreach (var type in types)
                writer.WriteLine($"    &{new QualifiedIdentifier(project.Name, type.FullName, "__typeinfo").ToCIdentifier()},");
            writer.WriteLine("};");
        }
        if (references.Length != 0)
        {
            writer.WriteLine($"static const cx_module_info_provider {token}___module_references[] = {{");
            foreach (var reference in references)
                writer.WriteLine($"    __cx_module_info_{GetModuleToken(reference)},");
            writer.WriteLine("};");
        }
        writer.WriteLine($"{GetModuleDataApiName(project.Name)} const struct cx_module_info {token}___module_info = {{");
        writer.WriteLine($"    .formatVersion = {CurrentModuleMetadataFormatVersion}u,");
        writer.WriteLine($"    .name = \"{EscapeCString(project.Name)}\",");
        writer.WriteLine($"    .version = \"{EscapeModuleMetadata(project.Version)}\",");
        writer.WriteLine($"    .description = \"{EscapeModuleMetadata(project.Description)}\",");
        writer.WriteLine($"    .author = \"{EscapeModuleMetadata(project.Author)}\",");
        writer.WriteLine($"    .types = {(types.Length == 0 ? "CX_NULL" : $"{token}___module_types")},");
        writer.WriteLine($"    .typeCount = {types.Length}u,");
        writer.WriteLine($"    .references = {(references.Length == 0 ? "CX_NULL" : $"{token}___module_references")},");
        writer.WriteLine($"    .referenceCount = {references.Length}u,");
        writer.WriteLine($"    .annotations = {(annotations == 0 ? "CX_NULL" : $"{token}___module_annotations")},");
        writer.WriteLine($"    .annotationCount = {annotations}u,");
        writer.WriteLine($"    .arrayReaders = {(arrayReaders == 0 ? "CX_NULL" : $"{token}___array_readers")},");
        writer.WriteLine($"    .arrayReaderCount = {arrayReaders}u,");
        writer.WriteLine($"    .genericTypes = {(genericTypes == 0 ? "CX_NULL" : $"{token}___generic_types")},");
        writer.WriteLine($"    .genericTypeCount = {genericTypes}u,");
        writer.WriteLine($"    .annotationSources = {(annotations == 0 ? "CX_NULL" : $"{token}___annotation_sources")},");
        writer.WriteLine("};");
        writer.WriteLine($"{GetModuleApiName(project.Name)} const struct cx_module_info *__cx_module_info_{token}(void) {{");
        writer.WriteLine($"    return &{token}___module_info;");
        writer.WriteLine("}");
    }

    private static int WriteModuleGenericArguments(IndentingWriter writer, CxProject project)
    {
        var token = GetModuleToken(project.Name);
        var instances = GetGenericTypeInstancesWithClosedInterfaces(project.GenericTypeInstances)
            .Where(instance => !ContainsOpenGenericParameter(instance.Type) &&
                instance.Type.TypeArguments.All(argument => ToTypeInfoPointer(argument) != "CX_NULL"))
            .OrderBy(instance => instance.Type.ConstructedIdentity!.CanonicalName, StringComparer.Ordinal).ToArray();
        for (var index = 0; index < instances.Length; index++)
        {
            var type = instances[index].Type;
            var prefix = $"{token}___generic_type_{index}";
            writer.WriteLine($"static const struct cxcore_System_Reflection_TypeInfo *{prefix}(void) {{ return {ToTypeInfoPointer(type)}; }}");
            for (var argument = 0; argument < type.TypeArguments.Count; argument++)
                writer.WriteLine($"static const struct cxcore_System_Reflection_TypeInfo *{prefix}_arg_{argument}(void) {{ return {ToTypeInfoPointer(type.TypeArguments[argument])}; }}");
            writer.WriteLine($"static const cx_reflection_element_provider {prefix}_args[] = {{");
            for (var argument = 0; argument < type.TypeArguments.Count; argument++)
                writer.WriteLine($"    {prefix}_arg_{argument},");
            writer.WriteLine("};");
        }
        if (instances.Length == 0) return 0;
        writer.WriteLine($"static const struct cx_reflection_generic_entry {token}___generic_types[] = {{");
        for (var index = 0; index < instances.Length; index++)
            writer.WriteLine($"    {{ {token}___generic_type_{index}, {token}___generic_type_{index}_args, {instances[index].Type.TypeArguments.Count}u }},");
        writer.WriteLine("};");
        return instances.Length;
    }

    private static int WriteModuleArrayReaders(IndentingWriter writer, CxProject project,
        IReadOnlyList<DeclarationBase> types)
    {
        var token = GetModuleToken(project.Name);
        var entries = new List<(ClassDeclaration Owner, int Index, TypeBase Element)>();
        foreach (var owner in types.OfType<ClassDeclaration>())
        {
            var functions = GetReflectionFunctions(owner).ToArray();
            for (var index = 0; index < functions.Length; index++)
            {
                if (!ArrayType.TryGetElementType(UnwrapConst(functions[index].ReturnType), out var element) ||
                    ContainsOpenGenericParameter(element) || GetFunctionType(element) is not null || element is NullableType ||
                    ToTypeInfoPointer(element) == "CX_NULL" ||
                    !(IsCReferenceType(element) || IsBoxableValueType(element) || element is NamedType { ClassType: ClassType.Interface })) continue;
                entries.Add((owner, index, element));
            }
        }
        for (var index = 0; index < entries.Count; index++)
        {
            var element = entries[index].Element;
            var prefix = $"{token}___array_reader_{index}";
            var kind = element is NamedType { ClassType: ClassType.Interface } ? 2 : IsCReferenceType(element) ? 0 : 1;
            writer.WriteLine($"static const struct cxcore_System_Reflection_TypeInfo *{prefix}_element(void) {{ return {ToTypeInfoPointer(element)}; }}");
            writer.WriteLine($"static struct cxcore_System_Collections_Array *{prefix}(cx_ptr value, cx_uint maximumLength) {{");
            writer.WriteLine($"    return cx_array_with_type(cx_reflection_array_values(value, {ArrayTypeKey(element)}, " +
                $"(cx_uint)sizeof({element.ToCIdentifier(false)}), {ToTypeInfoPointer(element)}, {kind}u, maximumLength), {ArrayTypeKey(BuiltInSystemTypes.Object)});");
            writer.WriteLine("}");
        }
        if (entries.Count == 0) return 0;
        writer.WriteLine($"static const struct cx_reflection_array_entry {token}___array_readers[] = {{");
        for (var index = 0; index < entries.Count; index++)
            writer.WriteLine($"    {{ &{new QualifiedIdentifier(project.Name, entries[index].Owner.FullName, "__typeinfo").ToCIdentifier()}, " +
                $"{entries[index].Index}u, {token}___array_reader_{index}, {token}___array_reader_{index}_element }},");
        writer.WriteLine("};");
        return entries.Count;
    }

    private sealed record ModuleAnnotation(DeclarationBase Owner, AnnotationApplication Annotation,
        int FunctionIndex, int ParameterIndex, string Target, string MemberName);

    private static int WriteModuleAnnotations(IndentingWriter writer, CxProject project,
        IReadOnlyList<DeclarationBase> types)
    {
        var entries = new List<ModuleAnnotation>();
        foreach (var type in types)
        {
            foreach (var annotation in type.Annotations)
                entries.Add(new(type, annotation, -1, -1, type is EnumDeclaration ? "enum" : type is DelegateDeclaration ? "delegate" : "class", ""));
            if (type is not ClassDeclaration owner) continue;
            var functions = GetReflectionFunctions(owner).ToArray();
            for (var index = 0; index < functions.Length; index++)
            {
                if (functions[index].Declaration is not { } function) continue;
                foreach (var annotation in function.Annotations)
                    entries.Add(new(type, annotation, index, -1,
                        function is ConstructorDeclaration ? "constructor" : "function", function.Name));
                for (var parameterIndex = 0; parameterIndex < function.Parameters.Count; parameterIndex++)
                    foreach (var annotation in function.Parameters[parameterIndex].Annotations)
                        entries.Add(new(type, annotation, index, parameterIndex, "parameter", function.Name));
            }
            foreach (var member in owner.MemberDeclarations.Declarations.Where(member =>
                member is FieldDeclaration or PropertyDeclaration))
                foreach (var annotation in member.Annotations)
                    entries.Add(new(type, annotation, -1, -1,
                        member is FieldDeclaration ? "field" : "property", member.Name));
        }
        var token = GetModuleToken(project.Name);
        for (var index = 0; index < entries.Count; index++)
        {
            var arguments = entries[index].Annotation.BoundArguments;
            if (arguments.Count == 0) continue;
            var argumentIndex = 0;
            foreach (var name in arguments.Keys)
            {
                WriteAnnotationValueProvider(writer, $"{token}___annotation_value_{index}_{argumentIndex}",
                    entries[index].Annotation.BoundConstants[name]);
                argumentIndex++;
            }
            writer.WriteLine($"static const struct cx_annotation_argument {token}___annotation_args_{index}[] = {{");
            argumentIndex = 0;
            foreach (var (name, expression) in arguments)
            {
                writer.WriteLine($"    {{ \"{EscapeModuleMetadata(name)}\", \"{EscapeModuleMetadata(expression)}\", {token}___annotation_value_{index}_{argumentIndex} }},");
                argumentIndex++;
            }
            writer.WriteLine("};");
        }
        if (entries.Count == 0) return 0;
        writer.WriteLine($"static const struct cx_annotation_info {token}___module_annotations[] = {{");
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var arguments = entry.Annotation.BoundArguments;
            var typeName = entry.Annotation.ResolvedTypeName ?? entry.Annotation.Name.ToString();
            var target = entry.Annotation.Target ?? entry.Target;
            writer.WriteLine($"    {{ &{new QualifiedIdentifier(project.Name, entry.Owner.FullName, "__typeinfo").ToCIdentifier()}, " +
                $"{entry.FunctionIndex}, {entry.ParameterIndex}, \"{EscapeModuleMetadata(target)}\", " +
                $"\"{EscapeModuleMetadata(entry.MemberName)}\", \"{EscapeModuleMetadata(typeName)}\", " +
                $"{(arguments.Count == 0 ? "CX_NULL" : $"{token}___annotation_args_{index}")}, {arguments.Count}u }},");
        }
        writer.WriteLine("};");
        writer.WriteLine($"static const struct cx_source_location {token}___annotation_sources[] = {{");
        foreach (var entry in entries)
        {
            var source = entry.Annotation.SourceSpan;
            writer.WriteLine($"    {{ \"{EscapeModuleMetadata(source?.FilePath ?? "")}\", {source?.StartLine ?? 0}u, {source?.StartColumn ?? 0}u }},");
        }
        writer.WriteLine("};");
        return entries.Count;
    }

    private static void WriteAnnotationValueProvider(IndentingWriter writer, string identifier,
        AnnotationConstant constant)
    {
        var bytes = constant.Type is StringType && !constant.IsNull ? Encoding.UTF8.GetBytes(constant.Value) : [];
        if (bytes.Length != 0)
            writer.WriteLine($"static const unsigned char {identifier}_bytes[] = {{ {string.Join(", ", bytes.Select(value => $"0x{value:X2}"))} }};");
        writer.WriteLine($"static cx_ptr {identifier}(void) {{");
        if (constant.IsNull) writer.WriteLine("    return CX_NULL;");
        else if (constant.Type is StringType)
            writer.WriteLine($"    return cx_annotation_string_value({(bytes.Length == 0 ? "CX_NULL" : $"{identifier}_bytes")}, {bytes.Length}u);");
        else
        {
            var literal = constant.Type switch
            {
                BoolType => constant.Value == "true" ? "CX_TRUE" : "CX_FALSE",
                FloatTypeBase => constant.Value.Contains('.') || constant.Value.Contains('E', StringComparison.OrdinalIgnoreCase)
                    ? constant.Value : constant.Value + ".0",
                _ => constant.Value.StartsWith('-')
                    ? $"(-{constant.Value[1..]}ULL)" : $"{constant.Value}ULL",
            };
            // Use signed arithmetic for negative values, including INT64_MIN.
            if (constant.Value.StartsWith('-') && constant.Type is not FloatTypeBase)
            {
                var magnitude = System.Numerics.BigInteger.Parse(constant.Value[1..], System.Globalization.CultureInfo.InvariantCulture);
                literal = $"(-{magnitude - 1}LL - 1LL)";
            }
            writer.WriteLine($"    {constant.Type.ToCIdentifier(false)} value = ({constant.Type.ToCIdentifier(false)})({literal});");
            writer.WriteLine($"    return cx_box_value(&value, (cx_uint)sizeof(value), {ToTypeInfoPointer(constant.Type)});");
        }
        writer.WriteLine("}");
    }

    private static string EscapeModuleMetadata(string value) =>
        EscapeCString(value).Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);
}
