using CxCompiler.Model.Common;
using CxCompiler.Model.Project;
using CxCompiler.Model.Types;

namespace CxCompiler.Semantics;

public sealed partial class SemanticBinder
{
    private readonly List<FunctionSymbol> _symbols = [];
    private readonly List<TypeSymbol> _types = [];
    private readonly List<(TypeBase Type, ClassDeclaration Declaration)> _primitiveExtensionTargets = [];
    private readonly List<ConstructorSymbol> _constructors = [];
    private readonly List<FieldSymbol> _fields = [];
    private readonly List<PropertySymbol> _properties = [];
    private readonly List<EnumTypeSymbol> _enums = [];
    private readonly List<DelegateTypeSymbol> _delegates = [];
    private readonly List<AliasSymbol> _aliases = [];
    private readonly List<(string ModuleName, ConceptDeclaration Declaration)> _concepts = [];
    private readonly Dictionary<ConceptDeclaration, IReadOnlyList<ConceptDeclaration>>
        _conceptBases = [];
    private readonly Dictionary<FunctionDeclaration,
        IReadOnlyList<(string ParameterName, string ModuleName, ConceptDeclaration Declaration)>>
        _resolvedConceptConstraints = [];
    private readonly Dictionary<ClassDeclaration,
        IReadOnlyList<(string ParameterName, string ModuleName, ConceptDeclaration Declaration)>>
        _resolvedClassConceptConstraints = [];
    private readonly Dictionary<ClassDeclaration, ClassDeclaration>
        _canonicalPartialTypeDeclarations = [];
    private readonly HashSet<(string Module, QualifiedIdentifier Name)> _resolvingAliases = [];
    private readonly HashSet<string> _expandingGenericTypeInstances = new(StringComparer.Ordinal);
    private readonly HashSet<string> _expandedGenericTypeInstances = new(StringComparer.Ordinal);
    private readonly Dictionary<FunctionDeclaration, ExtensionDeclaration> _extensionFunctionOwners = [];
    private readonly Dictionary<string, FunctionDeclaration> _genericFunctionSpecializations =
        new(StringComparer.Ordinal);
    private readonly Dictionary<(FunctionDeclaration Primary, string TypeArguments),
        FunctionDeclaration> _genericMethodSpecializationTemplates = [];
    private readonly HashSet<string> _expandedGenericForwardCalls = new(StringComparer.Ordinal);
    private readonly Dictionary<PropertySymbol, ExtensionDeclaration> _extensionPropertyOwners = [];
    private readonly Dictionary<PropertySymbol, ClassDeclaration> _extensionPropertyTargets = [];
    private CxProject? _project;
    private IReadOnlyList<QualifiedIdentifier>? _activeImports;
    private FunctionDeclaration? _activeFunction;
    private int _loopDepth;
    private int _breakableDepth;
    private int _catchDepth;
    private int _unsafeDepth;
    private int _objectCreationIndex;
    private int _propertyAssignmentIndex;
    private int _interfaceReceiverIndex;
    private int _foreachEnumeratorIndex;
    private int _operatorTemporaryIndex;
    private int _switchTemporaryIndex;
    private string? _genericConstraintFailure;

    public void Bind(CxProject project)
    {
        try { BindCore(project); }
        finally { project.RemoveSyntheticCoreContexts(); }
    }

    private void BindCore(CxProject project)
    {
        _project = project;
        project.ClearGenericFunctionInstances();
        project.ClearGenericTypeInstances();
        _symbols.Clear();
        _types.Clear();
        _primitiveExtensionTargets.Clear();
        _constructors.Clear();
        _fields.Clear();
        _properties.Clear();
        _enums.Clear();
        _delegates.Clear();
        _aliases.Clear();
        _concepts.Clear();
        _conceptBases.Clear();
        _resolvedConceptConstraints.Clear();
        _resolvedClassConceptConstraints.Clear();
        _canonicalPartialTypeDeclarations.Clear();
        _resolvingAliases.Clear();
        _expandingGenericTypeInstances.Clear();
        _expandedGenericTypeInstances.Clear();
        _extensionFunctionOwners.Clear();
        _extensionPropertyOwners.Clear();
        _extensionPropertyTargets.Clear();
        _genericFunctionSpecializations.Clear();
        _genericMethodSpecializationTemplates.Clear();
        _expandedGenericForwardCalls.Clear();
        _activeImports = null;
        _activeFunction = null;
        _genericConstraintFailure = null;
        _operatorTemporaryIndex = 0;
        _switchTemporaryIndex = 0;
        AddCoreTextDeclarations();
        AddCoreSymbols();

        MergePartialTypeDeclarations(project.CompilationContexts);
        ValidateStaticConstructors(project.CompilationContexts);
        AddImplicitParameterlessConstructors(project.CompilationContexts);
        ValidateProjectReferenceTypeConflicts(project.CompilationContexts);

        foreach (var context in project.CompilationContexts)
        {
            BindDeclarationParameters(context.DeclarationScope.Declarations, []);
        }

        foreach (var context in project.CompilationContexts)
        {
            AddAliases(
                context.ProjectName ?? project.Name,
                context.DeclarationScope.Declarations,
                context.Imports);
        }

        foreach (var context in project.CompilationContexts)
        {
            AddProjectTypes(
                context.ProjectName ?? project.Name,
                context.DeclarationScope.Declarations,
                context.Imports);
        }
        foreach (var context in project.CompilationContexts)
        {
            ClassifyFunctionGenericTypeLists(context);
            BindDeclarationParameters(
                context.DeclarationScope.Declarations,
                [],
                bindFieldAndPropertyTypes: false);
            foreach (var concept in context.DeclarationScope.Declarations
                .OfType<ConceptDeclaration>())
            {
                BindDeclarationParameters(
                    concept.DeclaredMethodRequirements, [], validateFunctions: false);
                BindDeclarationParameters(
                    concept.DeclaredOperatorRequirements, [], validateFunctions: false);
            }
        }
        IndexConcepts(project.CompilationContexts);
        ResolveConceptBases(project.CompilationContexts);

        foreach (var context in project.CompilationContexts)
        {
            AddExtensionMembers(context);
        }
        ValidateAliasNames();

        foreach (var context in project.CompilationContexts)
        {
            ResolveBaseTypes(
                context.ProjectName ?? project.Name,
                context.DeclarationScope.Declarations,
                context.Imports);
        }
        foreach (var context in project.CompilationContexts)
        {
            ResolveGenericTypeConstraints(
                context.DeclarationScope.Declarations,
                context.Imports);
            foreach (var concept in context.DeclarationScope.Declarations
                .OfType<ConceptDeclaration>())
            {
                foreach (var requirement in concept.MethodRequirements)
                {
                    ResolveConceptConstraints(
                        requirement, context.Namespace, context.Imports);
                    foreach (var constraint in requirement.GenericConstraints)
                    {
                        foreach (var type in constraint.Types)
                        {
                            ResolveTypeReference(
                                type, context.Namespace, context.Imports);
                        }
                        ValidateGenericConstraintTypes(constraint);
                    }
                    ValidateGenericConstraintSet(requirement.GenericConstraints);
                    ResolveTypeReference(
                        requirement.ReturnType,
                        context.Namespace,
                        context.Imports);
                    foreach (var parameter in requirement.Parameters)
                    {
                        ResolveTypeReference(
                            parameter.ParameterType,
                            context.Namespace,
                            context.Imports);
                    }
                }
            }
        }
        foreach (var context in project.CompilationContexts)
        {
            ValidateGenericBaseTypeConstraints(context.DeclarationScope.Declarations);
        }
        ValidateInheritanceCycles();
        BindNativeTypes(project);
        BindNativeFunctions(project);

        foreach (var context in project.CompilationContexts)
        {
            ResolveDeclarationTypes(
                context.DeclarationScope.Declarations,
                context.Namespace,
                context.Imports,
                IsExternalReference(context));
            foreach (var extension in context.DeclarationScope.Declarations
                .OfType<ExtensionDeclaration>())
            {
                if (IsExternalReference(context) && extension.Visibility != Visibility.Public)
                {
                    continue;
                }
                ResolveDeclarationTypes(
                    extension.Members.MemberDeclarations.Declarations,
                    extension.Namespace,
                    context.Imports,
                    IsExternalReference(context));
            }
        }

        var asyncLoweringFunctions = project.CompilationContexts
            .Where(context => !context.IsSyntheticCore)
            .SelectMany(context => EnumerateFunctions(
                context.DeclarationScope.Declarations).Where(function =>
                    !IsExternalReference(context) || IsPublicApiFunction(function)))
            .Distinct()
            .ToArray();
        foreach (var context in project.CompilationContexts)
        {
            if (!IsExternalReference(context))
            {
                foreach (var function in EnumerateFunctions(
                    context.DeclarationScope.Declarations))
                {
                    AsyncFunctionLowerer.Lower(
                        function, asyncLoweringFunctions, context.Imports);
                }
            }
            AddProjectSymbols(
                context.ProjectName ?? project.Name,
                context.DeclarationScope.Declarations);
            foreach (var extension in context.DeclarationScope.Declarations
                .OfType<ExtensionDeclaration>())
            {
                if (IsExternalReference(context) && extension.Visibility != Visibility.Public)
                {
                    continue;
                }
                AddProjectSymbols(
                    context.ProjectName ?? project.Name,
                    extension.Members.MemberDeclarations.Declarations);
                foreach (var function in EnumerateFunctions(
                    extension.Members.MemberDeclarations.Declarations)
                    .Where(function => function.OperatorToken is not null))
                {
                    ValidateOperatorDeclaration(function);
                }
            }
        }
        RegisterGenericFunctionSpecializations(project.CompilationContexts);

        foreach (var context in project.CompilationContexts)
        {
            AddExtensionPropertySymbols(context);
        }

        BindVirtualMethods();

        BindFieldInitializers();
        BindEnumMembers();
        BindAnnotations(project);

        foreach (var context in project.CompilationContexts)
        {
            if (IsExternalReference(context))
            {
                continue;
            }
            foreach (var function in EnumerateFunctions(context.DeclarationScope.Declarations))
            {
                BindFunction(function, context.Imports);
            }
            foreach (var accessor in EnumeratePropertyAccessors(
                context.DeclarationScope.Declarations))
            {
                BindPropertyAccessor(accessor, context.Imports);
            }
            foreach (var extension in context.DeclarationScope.Declarations
                .OfType<ExtensionDeclaration>())
            {
                foreach (var function in EnumerateFunctions(
                    extension.Members.MemberDeclarations.Declarations))
                {
                    BindFunction(function, context.Imports);
                }
                foreach (var accessor in EnumeratePropertyAccessors(
                    extension.Members.MemberDeclarations.Declarations))
                {
                    BindPropertyAccessor(accessor, context.Imports);
                }
            }
        }

        foreach (var context in project.CompilationContexts)
        {
            if (IsExternalReference(context))
            {
                continue;
            }

            var generatedIteratorTypes = IteratorDeclarationLowerer.Lower(
                context, project.CompilationContexts);
            if (generatedIteratorTypes.Count == 0)
            {
                continue;
            }

            var moduleName = context.ProjectName ?? project.Name;
            AddProjectTypes(moduleName, generatedIteratorTypes, context.Imports);
            ResolveGenericTypeConstraints(
                generatedIteratorTypes, context.Imports);
            ResolveBaseTypes(
                moduleName, generatedIteratorTypes, context.Imports);
            ValidateGenericBaseTypeConstraints(generatedIteratorTypes);
            ResolveDeclarationTypes(
                generatedIteratorTypes,
                context.Namespace,
                context.Imports,
                publicApiOnly: false);
            AddProjectSymbols(moduleName, generatedIteratorTypes);
        }

        BindVirtualMethods();

        foreach (var context in project.CompilationContexts)
        {
            if (IsExternalReference(context))
            {
                continue;
            }

            foreach (var function in EnumerateFunctions(context.DeclarationScope.Declarations)
                .Where(function => function.IsIteratorFactory))
            {
                BindFunction(function, context.Imports);
            }

            foreach (var iteratorType in context.DeclarationScope.Declarations
                .OfType<ClassDeclaration>()
                .Where(declaration => declaration.Name.StartsWith(
                    "__CxIterator_", StringComparison.Ordinal)))
            {
                foreach (var function in EnumerateFunctions([iteratorType]))
                {
                    BindFunction(function, context.Imports);
                }
                foreach (var accessor in EnumeratePropertyAccessors([iteratorType]))
                {
                    BindPropertyAccessor(accessor, context.Imports);
                }
            }
        }

        RegisterClosedClassConceptFunctionInstances();
        while (true)
        {
            var genericTypeCount = project.GenericTypeInstances.Count;
            var genericFunctionCount = project.GenericFunctionInstances.Count;
            RegisterClosedGenericClassFunctionInstances();
            RegisterClosedGenericClassFunctionTargets();
            RegisterClosedGenericMethodFunctionTargets();
            RegisterClosedGenericConstructorObjectCreations();
            BindGenericVirtualDispatchTargets(project);
            if (project.GenericTypeInstances.Count == genericTypeCount &&
                project.GenericFunctionInstances.Count == genericFunctionCount)
            {
                break;
            }
        }
        ValidateConstructorInitializerCycles();
    }
}
