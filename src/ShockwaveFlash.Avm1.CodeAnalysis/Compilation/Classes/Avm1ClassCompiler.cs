using ShockwaveFlash.Avm1.Compilation.Assembly;
using ShockwaveFlash.Avm1.Compilation.Analysis;
using ShockwaveFlash.Avm1.Source;
using ShockwaveFlash.Avm1.Special;
using ShockwaveFlash.Avm1.Swf4;
using ShockwaveFlash.Avm1.Swf5;
using ShockwaveFlash.Avm1.Swf7;
using ShockwaveFlash.Avm1.Types;

namespace ShockwaveFlash.Avm1.Compilation;

internal static class Avm1ClassCompiler
{
    public static Avm1ProgramArtifact CompileProgram(
        Avm1Compiler compiler,
        Avm1SourceProgram program,
        Avm1ClassCompilationOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(compiler);
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(options);

        var diagnostics = new List<Avm1CompilerDiagnostic>();
        if (options.MaxDegreeOfParallelism <= 0)
        {
            AddDiagnostic(
                diagnostics,
                "AVM1CLS007",
                "MaxDegreeOfParallelism must be greater than zero.");
            return new Avm1ProgramArtifact(
                program,
                [],
                diagnostics.ToArray());
        }

        var orderedClasses = OrderProgramClasses(
            program,
            diagnostics,
            cancellationToken);
        var classes = new Avm1ClassArtifact[orderedClasses.Count];
        if (options.MaxDegreeOfParallelism > 1 && orderedClasses.Count > 1)
        {
            var classOptions = options with { MaxDegreeOfParallelism = 1 };
            Parallel.For(
                0,
                orderedClasses.Count,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = options.MaxDegreeOfParallelism
                },
                i => classes[i] = Compile(
                    compiler,
                    orderedClasses[i],
                    classOptions,
                    cancellationToken));
        }
        else
        {
            for (var i = 0; i < orderedClasses.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                classes[i] = Compile(
                    compiler,
                    orderedClasses[i],
                    options,
                    cancellationToken);
            }
        }

        foreach (var artifact in classes)
            diagnostics.AddRange(artifact.Diagnostics);

        return new Avm1ProgramArtifact(
            program,
            classes,
            diagnostics.ToArray());
    }

    private static List<Avm1SourceClass> OrderProgramClasses(
        Avm1SourceProgram program,
        List<Avm1CompilerDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var sourceClasses = program.Files
            .SelectMany(file => file.Classes)
            .ToArray();
        var classesBySymbol = new Dictionary<SourceProgramSymbolIndex, Avm1SourceClass>();
        foreach (var sourceClass in sourceClasses)
        {
            if (program.TryGetSymbol(sourceClass, out var symbol))
                classesBySymbol.Add(symbol, sourceClass);
        }

        var states = new Dictionary<Avm1SourceClass, byte>(
            ReferenceEqualityComparer.Instance);
        var path = new List<Avm1SourceClass>();
        var reportedCycles = new HashSet<Avm1SourceClass>(
            ReferenceEqualityComparer.Instance);
        var result = new List<Avm1SourceClass>(sourceClasses.Length);
        foreach (var sourceClass in sourceClasses)
        {
            VisitProgramClass(
                program,
                sourceClass,
                classesBySymbol,
                states,
                path,
                reportedCycles,
                result,
                diagnostics,
                cancellationToken);
        }
        return result;
    }

    private static void VisitProgramClass(
        Avm1SourceProgram program,
        Avm1SourceClass sourceClass,
        Dictionary<SourceProgramSymbolIndex, Avm1SourceClass> classesBySymbol,
        Dictionary<Avm1SourceClass, byte> states,
        List<Avm1SourceClass> path,
        HashSet<Avm1SourceClass> reportedCycles,
        List<Avm1SourceClass> result,
        List<Avm1CompilerDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (states.TryGetValue(sourceClass, out var state))
        {
            if (state == 1 && reportedCycles.Add(sourceClass))
            {
                var start = path.FindIndex(item => ReferenceEquals(item, sourceClass));
                var names = path.Skip(Math.Max(0, start))
                    .Select(item => item.Name.Value)
                    .Append(sourceClass.Name.Value);
                AddDiagnostic(
                    diagnostics,
                    "AVM1CLS100",
                    "Class initialization dependency cycle: " +
                    string.Join(" -> ", names) + ".");
            }
            return;
        }

        states.Add(sourceClass, 1);
        path.Add(sourceClass);
        if (program.TryGetSymbol(sourceClass, out var symbol))
        {
            foreach (var dependency in GetProgramDependencies(program, symbol))
            {
                if (classesBySymbol.TryGetValue(dependency, out var declaration))
                {
                    VisitProgramClass(
                        program,
                        declaration,
                        classesBySymbol,
                        states,
                        path,
                        reportedCycles,
                        result,
                        diagnostics,
                        cancellationToken);
                }
            }
        }
        path.RemoveAt(path.Count - 1);
        states[sourceClass] = 2;
        result.Add(sourceClass);
    }

    private static IEnumerable<SourceProgramSymbolIndex> GetProgramDependencies(
        Avm1SourceProgram program,
        SourceProgramSymbolIndex symbol)
    {
        if (program.TryGetBaseType(symbol, out var baseType) &&
            program[baseType].Symbol.IsValid)
        {
            yield return program[baseType].Symbol;
        }

        var interfaces = program.GetInterfaceTypes(symbol);
        for (var i = 0; i < interfaces.Count; i++)
        {
            var type = program[program.GetInterfaceType(interfaces, i)];
            if (type.Symbol.IsValid)
                yield return type.Symbol;
        }
    }

    public static Avm1ClassArtifact Compile(
        Avm1Compiler compiler,
        Avm1SourceClass sourceClass,
        Avm1ClassCompilationOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(compiler);
        ArgumentNullException.ThrowIfNull(sourceClass);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        var diagnostics = new List<Avm1CompilerDiagnostic>();
        ValidateOptions(sourceClass, options, diagnostics);
        var methods = sourceClass.Kind is Avm1SourceTypeDeclarationKind.Interface
            ? []
            : CompileMethods(
                compiler,
                sourceClass,
                options,
                diagnostics,
                cancellationToken);
        if (HasErrors(diagnostics))
        {
            return new Avm1ClassArtifact(
                sourceClass,
                options.SwfVersion,
                null,
                methods,
                diagnostics.ToArray(),
                options.AbiProfile,
                Avm1OriginReuseKind.None);
        }

        if (options.AbiProfile is Avm1ClassAbiProfile.OriginPreserving &&
            TryCompileFromOrigin(
                sourceClass,
                options,
                methods,
                out var originBody,
                out methods,
                out var originReuseKind))
        {
            AddAssemblyDiagnostics(originBody, diagnostics);
            var originSourceMap = Avm1CompiledClassSourceMapBuilder.BuildOrigin(
                methods,
                originBody);
            return new Avm1ClassArtifact(
                sourceClass,
                options.SwfVersion,
                originBody,
                methods,
                diagnostics.ToArray(),
                options.AbiProfile,
                originReuseKind,
                originSourceMap);
        }

        var initializer = new Avm1CodeUnit();
        var methodSites = new List<Avm1ClassMethodAssemblySite>();
        var methodLookup = methods.ToDictionary(item => item.Declaration);
        if (sourceClass.Kind is Avm1SourceTypeDeclarationKind.Interface)
        {
            EmitInterfaceInitializer(
                initializer,
                sourceClass,
                methodLookup,
                options,
                methodSites);
        }
        else if (options.AbiProfile is not Avm1ClassAbiProfile.Canonical)
        {
            EmitAdobeInitializer(
                compiler,
                initializer,
                sourceClass,
                methodLookup,
                methodSites,
                options,
                diagnostics,
                cancellationToken);
        }
        else
        {
            EmitCanonicalInitializer(
                compiler,
                initializer,
                sourceClass,
                methodLookup,
                methodSites,
                options,
                diagnostics,
                cancellationToken);
        }

        if (HasErrors(diagnostics))
        {
            return new Avm1ClassArtifact(
                sourceClass,
                options.SwfVersion,
                null,
                methods,
                diagnostics.ToArray(),
                options.AbiProfile,
                Avm1OriginReuseKind.None);
        }

        initializer.Emit(new ActionEnd());
        var body = initializer.Assemble(CreateClassAssemblyOptions(options));
        AddAssemblyDiagnostics(body, diagnostics);
        var classSourceMap = Avm1CompiledClassSourceMapBuilder.BuildCompiled(
            methods,
            body,
            methodSites);
        return new Avm1ClassArtifact(
            sourceClass,
            options.SwfVersion,
            body,
            methods,
            diagnostics.ToArray(),
            options.AbiProfile,
            Avm1OriginReuseKind.None,
            classSourceMap);
    }

    private static void EmitInterfaceInitializer(
        Avm1CodeUnit initializer,
        Avm1SourceClass sourceInterface,
        IReadOnlyDictionary<Avm1SourceMethodDeclaration, Avm1CompiledClassMember>
            methods,
        Avm1ClassCompilationOptions options,
        List<Avm1ClassMethodAssemblySite> methodSites)
    {
        if (options.AbiProfile is Avm1ClassAbiProfile.Canonical)
            EmitPackageDefinitions(initializer, sourceInterface.Name);
        else
            EmitAdobePackageDefinitions(initializer, sourceInterface.Name, 0);

        EmitClassDefinition(initializer, sourceInterface, methods, methodSites);
        EmitInterfaces(initializer, sourceInterface);
    }

    private static Avm1CompiledClassMember[] CompileMethods(
        Avm1Compiler compiler,
        Avm1SourceClass sourceClass,
        Avm1ClassCompilationOptions options,
        List<Avm1CompilerDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var declarations = sourceClass.Members
            .OfType<Avm1SourceMethodDeclaration>()
            .ToArray();
        var result = new Avm1CompiledClassMember[declarations.Length];
        if (options.MaxDegreeOfParallelism > 1 && declarations.Length > 1)
        {
            Parallel.For(
                0,
                declarations.Length,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = options.MaxDegreeOfParallelism
                },
                CompileAt);
        }
        else
        {
            for (var i = 0; i < declarations.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CompileAt(i);
            }
        }

        foreach (var method in result)
            diagnostics.AddRange(method.Method.Diagnostics);
        return result;

        void CompileAt(int index)
        {
            var method = declarations[index];
            var closureAnalysis = Avm1ClosureAnalysis.Analyze(
                method.Body,
                cancellationToken);
            var functionFlags = GetFunctionFlags(
                sourceClass,
                method,
                closureAnalysis,
                options.AbiProfile);
            var functionPreloads = Avm1FunctionPreloadPlan.FromFlags(
                functionFlags);
            var artifact = compiler.CompileMethod(
                method.Body,
                CreateFunctionOptions(options, functionFlags),
                functionPreloads,
                cancellationToken);
            var registerCount = artifact.ActionBody is { Succeeded: true } body
                ? GetRequiredRegisterCount(body.Actions, options.SwfVersion)
                : (byte)0;
            registerCount = Math.Max(
                registerCount,
                GetRequiredRegisterCount(
                    GetPreloadRegisterCount(functionFlags)));
            result[index] = new Avm1CompiledClassMember(
                method,
                artifact,
                registerCount,
                functionFlags,
                GetParameters(method.Body),
                OriginBodyReused: false);
        }
    }

    private static bool TryCompileFromOrigin(
        Avm1SourceClass sourceClass,
        Avm1ClassCompilationOptions options,
        Avm1CompiledClassMember[] methods,
        out Avm1ActionBody body,
        out Avm1CompiledClassMember[] emittedMethods,
        out Avm1OriginReuseKind reuseKind)
    {
        body = null!;
        emittedMethods = methods;
        reuseKind = Avm1OriginReuseKind.None;
        if (sourceClass.Origin.Bytecode is not { } origin ||
            origin.SwfVersion != options.SwfVersion ||
            (!options.EmitPropertyFlags && HasRecoveredStep(
                sourceClass,
                Avm1SourceClassInitializationStepKind.PropertyFlags)) ||
            (!options.EmitRecoveredLinkage && HasRecoveredStep(
                sourceClass,
                Avm1SourceClassInitializationStepKind.Linkage)) ||
            Avm1SourceFingerprint.ComputeClassShape(sourceClass) !=
                origin.ShapeFingerprint)
        {
            return false;
        }

        var originBody = Avm1ActionBody.FromBytecode(
            origin.InitializerBody,
            CreateClassAssemblyOptions(options));
        if (!originBody.Succeeded)
            return false;

        if (Avm1SourceFingerprint.ComputeClass(sourceClass) ==
            origin.SourceFingerprint)
        {
            emittedMethods = methods
                .Select(method => method with { OriginBodyReused = true })
                .ToArray();
            body = originBody;
            reuseKind = Avm1OriginReuseKind.CompleteInitializer;
            return true;
        }

        var replacements = new Dictionary<int, Action>();
        var reused = new bool[methods.Length];
        var sourceActionIndices = new HashSet<int>();
        for (var i = 0; i < methods.Length; i++)
        {
            var methodOrigin = methods[i].Declaration.Origin.Bytecode;
            if (methodOrigin is null ||
                methodOrigin.SwfVersion != options.SwfVersion ||
                methodOrigin.InitializerActionIndex >= originBody.Actions.Count ||
                !sourceActionIndices.Add(methodOrigin.InitializerActionIndex) ||
                originBody.Actions[methodOrigin.InitializerActionIndex] is not
                    ActionDefineFunction2 originalFunction)
            {
                return false;
            }

            if (Avm1SourceFingerprint.ComputeMethod(
                    methods[i].Declaration.Body) ==
                methodOrigin.SourceFingerprint)
            {
                reused[i] = true;
                continue;
            }

            replacements.Add(
                methodOrigin.InitializerActionIndex,
                new ActionDefineFunction2(
                    originalFunction.Name,
                    methods[i].RegisterCount,
                    methods[i].FunctionFlags,
                    methods[i].Parameters,
                    methods[i].Method.Bytecode));
        }

        if (sourceActionIndices.Count != originBody.Actions
                .Count(action => action is ActionDefineFunction2) ||
            replacements.Count == 0)
        {
            return false;
        }

        var codeUnit = new Avm1CodeUnit();
        codeUnit.Append(originBody, replacements);
        var patchedBody = codeUnit.Assemble(CreateClassAssemblyOptions(options));
        if (!patchedBody.Succeeded)
            return false;

        emittedMethods = methods
            .Select((method, index) => method with
            {
                OriginBodyReused = reused[index]
            })
            .ToArray();
        body = patchedBody;
        reuseKind = Avm1OriginReuseKind.MethodBodies;
        return true;
    }

    private static Avm1AssemblyOptions CreateClassAssemblyOptions(
        Avm1ClassCompilationOptions options) =>
        new(options.SwfVersion)
        {
            VerifyDataFlow = options.AbiProfile is not
                Avm1ClassAbiProfile.OriginPreserving,
            RequireEndAction = true,
            RequireEmptyStackAtExit = true,
            CodeUnitContext = Avm1CodeUnitContext.Legacy(
                Avm1ConstantPoolContext.Missing)
        };

    private static void EmitCanonicalInitializer(
        Avm1Compiler compiler,
        Avm1CodeUnit initializer,
        Avm1SourceClass sourceClass,
        IReadOnlyDictionary<Avm1SourceMethodDeclaration, Avm1CompiledClassMember>
            methods,
        List<Avm1ClassMethodAssemblySite> methodSites,
        Avm1ClassCompilationOptions options,
        List<Avm1CompilerDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        EmitPackageDefinitions(initializer, sourceClass.Name);
        EmitClassDefinition(initializer, sourceClass, methods, methodSites);
        EmitInheritance(initializer, sourceClass);
        EmitInterfaces(initializer, sourceClass);

        foreach (var member in sourceClass.Members)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EmitMember(
                compiler,
                initializer,
                sourceClass,
                member,
                methods,
                methodSites,
                options,
                diagnostics,
                cancellationToken);
            if (HasErrors(diagnostics))
                return;
        }

        EmitAccessors(initializer, sourceClass);
        var hasRecoveredPropertyFlags = HasRecoveredStep(
            sourceClass,
            Avm1SourceClassInitializationStepKind.PropertyFlags);
        if (options.EmitPropertyFlags &&
            (hasRecoveredPropertyFlags || sourceClass.Initializer is null))
        {
            EmitPropertyFlags(initializer, sourceClass, isStatic: false);
            EmitPropertyFlags(initializer, sourceClass, isStatic: true);
        }

        if (sourceClass.Initializer is { HasResidualStatements: true } sourceInitializer)
        {
            EmitResidualBody(
                compiler,
                initializer,
                sourceInitializer.Body,
                options,
                diagnostics,
                cancellationToken);
        }

        if (options.EmitRecoveredLinkage &&
            HasRecoveredStep(
                sourceClass,
                Avm1SourceClassInitializationStepKind.Linkage))
        {
            EmitLinkage(initializer, sourceClass);
        }
    }

    private static void EmitAdobeInitializer(
        Avm1Compiler compiler,
        Avm1CodeUnit initializer,
        Avm1SourceClass sourceClass,
        IReadOnlyDictionary<Avm1SourceMethodDeclaration, Avm1CompiledClassMember>
            methods,
        List<Avm1ClassMethodAssemblySite> methodSites,
        Avm1ClassCompilationOptions options,
        List<Avm1CompilerDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var emittedMembers = new HashSet<Avm1SourceClassMember>();
        var emittedAccessors = new HashSet<(bool IsStatic, string Name)>();
        var emittedResidualBody = false;
        var emittedPackages = false;
        var emittedClass = false;
        var emittedInheritance = false;
        var emittedInterfaces = false;
        var emittedPropertyFlags = false;
        var emittedLinkage = false;
        var recoveredPackageDepth = GetRecoveredPackagePrefixDepth(sourceClass);

        if (sourceClass.Initializer is { } sourceInitializer)
        {
            foreach (var step in sourceInitializer.Steps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stepMember = ResolveCurrentMember(sourceClass, step.Member);
                switch (step.Kind)
                {
                    case Avm1SourceClassInitializationStepKind.ResidualStatement
                        when !emittedResidualBody:
                        EmitResidualBody(
                            compiler,
                            initializer,
                            sourceInitializer.Body,
                            options,
                            diagnostics,
                            cancellationToken);
                        emittedResidualBody = true;
                        break;
                    case Avm1SourceClassInitializationStepKind.PackageDefinition
                        when !emittedPackages:
                        EmitAdobePackageDefinitions(
                            initializer,
                            sourceClass.Name,
                            recoveredPackageDepth);
                        emittedPackages = true;
                        break;
                    case Avm1SourceClassInitializationStepKind.ClassDefinition
                        when !emittedClass:
                        EmitClassDefinition(
                            initializer,
                            sourceClass,
                            methods,
                            methodSites);
                        emittedClass = true;
                        if (stepMember is not null)
                            emittedMembers.Add(stepMember);
                        break;
                    case Avm1SourceClassInitializationStepKind.Inheritance
                        when !emittedInheritance:
                        EmitInheritance(initializer, sourceClass);
                        emittedInheritance = true;
                        break;
                    case Avm1SourceClassInitializationStepKind.Interfaces
                        when !emittedInterfaces:
                        EmitInterfaces(initializer, sourceClass);
                        emittedInterfaces = true;
                        break;
                    case Avm1SourceClassInitializationStepKind.MemberDefinition
                        when stepMember is not null &&
                             emittedMembers.Add(stepMember):
                        EmitMember(
                            compiler,
                            initializer,
                            sourceClass,
                            stepMember,
                            methods,
                            methodSites,
                            options,
                            diagnostics,
                            cancellationToken);
                        break;
                    case Avm1SourceClassInitializationStepKind.AccessorDefinition
                        when stepMember is Avm1SourceMethodDeclaration accessor:
                        {
                            var key = (IsStatic(accessor), accessor.Name);
                            if (emittedAccessors.Add(key))
                            {
                                EmitAccessor(
                                    initializer,
                                    sourceClass,
                                    key.Item1,
                                    key.Name);
                            }
                            break;
                        }
                    case Avm1SourceClassInitializationStepKind.PropertyFlags
                        when options.EmitPropertyFlags && !emittedPropertyFlags:
                        EmitPropertyFlags(initializer, sourceClass, isStatic: false);
                        emittedPropertyFlags = true;
                        break;
                    case Avm1SourceClassInitializationStepKind.Linkage
                        when options.EmitRecoveredLinkage && !emittedLinkage:
                        EmitLinkage(initializer, sourceClass);
                        emittedLinkage = true;
                        break;
                }

                if (HasErrors(diagnostics))
                    return;
            }
        }

        if (!emittedPackages)
        {
            EmitAdobePackageDefinitions(
                initializer,
                sourceClass.Name,
                recoveredPackageDepth);
        }
        if (!emittedClass)
            EmitClassDefinition(initializer, sourceClass, methods, methodSites);
        if (!emittedInheritance)
            EmitInheritance(initializer, sourceClass);
        if (!emittedInterfaces)
            EmitInterfaces(initializer, sourceClass);

        foreach (var member in sourceClass.Members)
        {
            if (!emittedMembers.Add(member))
                continue;
            EmitMember(
                compiler,
                initializer,
                sourceClass,
                member,
                methods,
                methodSites,
                options,
                diagnostics,
                cancellationToken);
            if (HasErrors(diagnostics))
                return;
        }

        foreach (var accessor in sourceClass.Methods.Where(method => method.Kind is
                     Avm1SourceMethodKind.Getter or Avm1SourceMethodKind.Setter))
        {
            var key = (IsStatic(accessor), accessor.Name);
            if (emittedAccessors.Add(key))
                EmitAccessor(initializer, sourceClass, key.Item1, key.Name);
        }

        if (!emittedResidualBody &&
            sourceClass.Initializer is { HasResidualStatements: true } remainingInitializer)
        {
            EmitResidualBody(
                compiler,
                initializer,
                remainingInitializer.Body,
                options,
                diagnostics,
                cancellationToken);
            if (HasErrors(diagnostics))
                return;
        }

        if (options.EmitPropertyFlags &&
            !emittedPropertyFlags &&
            (sourceClass.Initializer is null || HasRecoveredStep(
                sourceClass,
                Avm1SourceClassInitializationStepKind.PropertyFlags)))
        {
            EmitPropertyFlags(initializer, sourceClass, isStatic: false);
        }

        if (options.EmitRecoveredLinkage &&
            !emittedLinkage &&
            HasRecoveredStep(
                sourceClass,
                Avm1SourceClassInitializationStepKind.Linkage))
        {
            EmitLinkage(initializer, sourceClass);
        }
    }

    private static Avm1SourceClassMember? ResolveCurrentMember(
        Avm1SourceClass sourceClass,
        Avm1SourceClassMember? recoveredMember)
    {
        if (recoveredMember is null)
            return null;
        if (sourceClass.Members.Contains(recoveredMember))
            return recoveredMember;

        return sourceClass.Members.SingleOrDefault(member =>
            member.GetType() == recoveredMember.GetType() &&
            member.Name == recoveredMember.Name &&
            member.Modifiers == recoveredMember.Modifiers &&
            member.Origin.RuntimeName == recoveredMember.Origin.RuntimeName &&
            (member is not Avm1SourceMethodDeclaration method ||
             recoveredMember is Avm1SourceMethodDeclaration recoveredMethod &&
             method.Kind == recoveredMethod.Kind));
    }

    private static void EmitMember(
        Avm1Compiler compiler,
        Avm1CodeUnit initializer,
        Avm1SourceClass sourceClass,
        Avm1SourceClassMember member,
        IReadOnlyDictionary<Avm1SourceMethodDeclaration, Avm1CompiledClassMember>
            methods,
        List<Avm1ClassMethodAssemblySite> methodSites,
        Avm1ClassCompilationOptions options,
        List<Avm1CompilerDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        switch (member)
        {
            case Avm1SourceField { Initializer: { } fieldInitializer } field:
                EmitField(
                    compiler,
                    initializer,
                    sourceClass,
                    field,
                    fieldInitializer,
                    options,
                    diagnostics,
                    cancellationToken);
                break;
            case Avm1SourceMethodDeclaration
            {
                Kind: not Avm1SourceMethodKind.Constructor
            } method:
                EmitMethod(
                    initializer,
                    sourceClass,
                    method,
                    methods[method],
                    methodSites);
                break;
        }
    }

    private static void EmitInheritance(
        Avm1CodeUnit initializer,
        Avm1SourceClass sourceClass)
    {
        if (sourceClass.BaseType is null)
            return;
        EmitLoadClass(initializer, sourceClass.Name.Value);
        EmitLoadClass(initializer, sourceClass.BaseType.Value);
        initializer.Emit(new ActionExtends());
    }

    private static void EmitInterfaces(
        Avm1CodeUnit initializer,
        Avm1SourceClass sourceClass)
    {
        if (sourceClass.Interfaces.Count == 0)
            return;
        foreach (var interfaceName in sourceClass.Interfaces)
            EmitLoadClass(initializer, interfaceName.Value);
        initializer.Emit(new ActionPush([
            PushValue.Integer(sourceClass.Interfaces.Count)
        ]));
        EmitLoadClass(initializer, sourceClass.Name.Value);
        initializer.Emit(new ActionImplementsOp());
    }

    private static void EmitResidualBody(
        Avm1Compiler compiler,
        Avm1CodeUnit initializer,
        Avm1SourceMethod body,
        Avm1ClassCompilationOptions options,
        List<Avm1CompilerDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var residual = compiler.CompileMethod(
            body,
            CreateInitializerOptions(options),
            cancellationToken);
        diagnostics.AddRange(residual.Diagnostics);
        if (residual.ActionBody is { Succeeded: true } residualBody)
            initializer.Append(residualBody);
    }

    private static bool HasRecoveredStep(
        Avm1SourceClass sourceClass,
        Avm1SourceClassInitializationStepKind kind) =>
        sourceClass.Initializer?.Steps.Any(step => step.Kind == kind) is true;

    private static void EmitClassDefinition(
        Avm1CodeUnit codeUnit,
        Avm1SourceClass sourceClass,
        IReadOnlyDictionary<Avm1SourceMethodDeclaration, Avm1CompiledClassMember>
            methods,
        List<Avm1ClassMethodAssemblySite> methodSites)
    {
        EmitLoadGlobal(codeUnit);
        foreach (var segment in GetNameSegments(sourceClass.Name.NamespaceName))
        {
            codeUnit.Emit(new ActionPush([PushValue.String(segment)]));
            codeUnit.Emit(new ActionGetMember());
        }
        codeUnit.Emit(new ActionPush([
            PushValue.String(sourceClass.Name.SimpleName)
        ]));

        if (sourceClass.Constructor is { } constructor)
        {
            var compiled = methods[constructor];
            var instruction = codeUnit.Emit(CreateFunctionAction(compiled));
            methodSites.Add(new Avm1ClassMethodAssemblySite(
                constructor,
                instruction));
        }
        else
        {
            codeUnit.Emit(new ActionDefineFunction2(
                string.Empty,
                registerCount: 0,
                flags: (FunctionFlags)0,
                parameters: [],
                body: ReadOnlyMemory<byte>.Empty));
        }
        codeUnit.Emit(new ActionSetMember());
    }

    private static void EmitField(
        Avm1Compiler compiler,
        Avm1CodeUnit codeUnit,
        Avm1SourceClass sourceClass,
        Avm1SourceField field,
        Avm1SourceExpressionFragment initializer,
        Avm1ClassCompilationOptions options,
        List<Avm1CompilerDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        EmitMemberOwner(codeUnit, sourceClass, IsStatic(field));
        codeUnit.Emit(new ActionPush([
            PushValue.String(GetRuntimeName(field))
        ]));

        var expression = compiler.CompileMethod(
            initializer.CreateReturnMethod(),
            CreateInitializerOptions(options),
            cancellationToken);
        diagnostics.AddRange(expression.Diagnostics);
        if (expression.ActionBody is not { Succeeded: true } expressionBody)
            return;
        if (expressionBody.Actions.Count == 0 ||
            expressionBody.Actions[^1] is not ActionReturn)
        {
            AddDiagnostic(
                diagnostics,
                "AVM1CLS020",
                $"Initializer for field {field.Name} did not compile to a " +
                "terminal return expression.");
            return;
        }

        codeUnit.Append(expressionBody, expressionBody.Actions.Count - 1);
        codeUnit.Emit(new ActionSetMember());
    }

    private static void EmitMethod(
        Avm1CodeUnit codeUnit,
        Avm1SourceClass sourceClass,
        Avm1SourceMethodDeclaration method,
        Avm1CompiledClassMember compiled,
        List<Avm1ClassMethodAssemblySite> methodSites)
    {
        EmitMemberOwner(codeUnit, sourceClass, IsStatic(method));
        codeUnit.Emit(new ActionPush([
            PushValue.String(GetRuntimeName(method))
        ]));
        var instruction = codeUnit.Emit(CreateFunctionAction(compiled));
        methodSites.Add(new Avm1ClassMethodAssemblySite(method, instruction));
        codeUnit.Emit(new ActionSetMember());
    }

    private static ActionDefineFunction2 CreateFunctionAction(
        Avm1CompiledClassMember compiled) =>
        new(
            string.Empty,
            compiled.RegisterCount,
            compiled.FunctionFlags,
            compiled.Parameters,
            compiled.Method.Bytecode);

    private static FunctionParameter[] GetParameters(Avm1SourceMethod method)
    {
        var result = new FunctionParameter[method.Parameters.Count];
        for (var i = 0; i < result.Length; i++)
        {
            var parameter = method.Arena[method.Parameters[i]];
            result[i] = new FunctionParameter(
                Register: 0,
                Name: method.Arena[parameter.Name]);
        }
        return result;
    }

    private static void EmitAccessors(
        Avm1CodeUnit codeUnit,
        Avm1SourceClass sourceClass)
    {
        var groups = sourceClass.Methods
            .Where(method => method.Kind is
                Avm1SourceMethodKind.Getter or Avm1SourceMethodKind.Setter)
            .GroupBy(method => (IsStatic(method), method.Name));
        foreach (var group in groups)
            EmitAccessor(
                codeUnit,
                sourceClass,
                group.Key.Item1,
                group.Key.Name);
    }

    private static void EmitAccessor(
        Avm1CodeUnit codeUnit,
        Avm1SourceClass sourceClass,
        bool isStatic,
        string name)
    {
        var getter = sourceClass.Methods.FirstOrDefault(method =>
            IsStatic(method) == isStatic &&
            method.Name == name &&
            method.Kind is Avm1SourceMethodKind.Getter);
        var setter = sourceClass.Methods.FirstOrDefault(method =>
            IsStatic(method) == isStatic &&
            method.Name == name &&
            method.Kind is Avm1SourceMethodKind.Setter);

        EmitAccessorArgument(codeUnit, sourceClass, setter);
        EmitAccessorArgument(codeUnit, sourceClass, getter);
        codeUnit.Emit(new ActionPush([
            PushValue.String(name),
            PushValue.Integer(3)
        ]));
        EmitMemberOwner(codeUnit, sourceClass, isStatic);
        codeUnit.Emit(new ActionPush([PushValue.String("addProperty")]));
        codeUnit.Emit(new ActionCallMethod());
        codeUnit.Emit(new ActionPop());
    }

    private static void EmitAccessorArgument(
        Avm1CodeUnit codeUnit,
        Avm1SourceClass sourceClass,
        Avm1SourceMethodDeclaration? accessor)
    {
        if (accessor is null)
        {
            codeUnit.Emit(new ActionPush([PushValue.Null()]));
            return;
        }

        EmitMemberOwner(codeUnit, sourceClass, IsStatic(accessor));
        codeUnit.Emit(new ActionPush([
            PushValue.String(GetRuntimeName(accessor))
        ]));
        codeUnit.Emit(new ActionGetMember());
    }

    private static void EmitPropertyFlags(
        Avm1CodeUnit codeUnit,
        Avm1SourceClass sourceClass,
        bool isStatic)
    {
        codeUnit.Emit(new ActionPush([
            PushValue.Integer(1),
            PushValue.Null()
        ]));
        EmitMemberOwner(codeUnit, sourceClass, isStatic);
        codeUnit.Emit(new ActionPush([
            PushValue.Integer(3),
            PushValue.String("ASSetPropFlags")
        ]));
        codeUnit.Emit(new ActionCallFunction());
        codeUnit.Emit(new ActionPop());
    }

    private static void EmitLinkage(
        Avm1CodeUnit codeUnit,
        Avm1SourceClass sourceClass)
    {
        EmitLoadClass(codeUnit, sourceClass.Name.Value);
        codeUnit.Emit(new ActionPush([
            PushValue.String(
                sourceClass.Origin.RuntimeName ?? sourceClass.Name.Value),
            PushValue.Integer(2)
        ]));
        EmitLoadClass(codeUnit, "Object");
        codeUnit.Emit(new ActionPush([PushValue.String("registerClass")]));
        codeUnit.Emit(new ActionCallMethod());
        codeUnit.Emit(new ActionPop());
    }

    private static void EmitMemberOwner(
        Avm1CodeUnit codeUnit,
        Avm1SourceClass sourceClass,
        bool isStatic)
    {
        EmitLoadClass(codeUnit, sourceClass.Name.Value);
        if (isStatic)
            return;
        codeUnit.Emit(new ActionPush([PushValue.String("prototype")]));
        codeUnit.Emit(new ActionGetMember());
    }

    private static void EmitPackageDefinitions(
        Avm1CodeUnit codeUnit,
        Avm1SourceQualifiedName className)
    {
        var segments = GetNameSegments(className.NamespaceName);
        for (var depth = 0; depth < segments.Length; depth++)
        {
            EmitLoadGlobal(codeUnit);
            for (var parent = 0; parent < depth; parent++)
            {
                codeUnit.Emit(new ActionPush([
                    PushValue.String(segments[parent])
                ]));
                codeUnit.Emit(new ActionGetMember());
            }
            codeUnit.Emit(new ActionPush([
                PushValue.String(segments[depth])
            ]));
            codeUnit.Emit(new ActionGetMember());
            codeUnit.Emit(new ActionNot());
            var create = codeUnit.DefineLabel();
            var complete = codeUnit.DefineLabel();
            codeUnit.EmitIf(create);
            codeUnit.EmitJump(complete);
            codeUnit.MarkLabel(create);

            EmitLoadGlobal(codeUnit);
            for (var parent = 0; parent < depth; parent++)
            {
                codeUnit.Emit(new ActionPush([
                    PushValue.String(segments[parent])
                ]));
                codeUnit.Emit(new ActionGetMember());
            }
            codeUnit.Emit(new ActionPush([
                PushValue.String(segments[depth]),
                PushValue.Integer(0)
            ]));
            codeUnit.Emit(new ActionInitObject());
            codeUnit.Emit(new ActionSetMember());
            codeUnit.MarkLabel(complete);
        }
    }

    private static void EmitAdobePackageDefinitions(
        Avm1CodeUnit codeUnit,
        Avm1SourceQualifiedName className,
        int firstDepth)
    {
        var segments = GetNameSegments(className.NamespaceName);
        firstDepth = Math.Clamp(firstDepth, 0, segments.Length);
        for (var depth = firstDepth; depth < segments.Length; depth++)
        {
            EmitLoadGlobal(codeUnit);
            for (var parent = 0; parent < depth; parent++)
            {
                codeUnit.Emit(new ActionPush([
                    PushValue.String(segments[parent])
                ]));
                codeUnit.Emit(new ActionGetMember());
            }
            codeUnit.Emit(new ActionPush([
                PushValue.String(segments[depth])
            ]));
            codeUnit.Emit(new ActionGetMember());
            codeUnit.Emit(new ActionNot());
            codeUnit.Emit(new ActionNot());
            var complete = codeUnit.DefineLabel();
            codeUnit.EmitIf(complete);

            EmitLoadGlobal(codeUnit);
            for (var parent = 0; parent < depth; parent++)
            {
                codeUnit.Emit(new ActionPush([
                    PushValue.String(segments[parent])
                ]));
                codeUnit.Emit(new ActionGetMember());
            }
            codeUnit.Emit(new ActionPush([
                PushValue.String(segments[depth]),
                PushValue.Integer(0),
                PushValue.String("Object")
            ]));
            codeUnit.Emit(new ActionNewObject());
            codeUnit.Emit(new ActionSetMember());
            codeUnit.MarkLabel(complete);
        }
    }

    private static int GetRecoveredPackagePrefixDepth(
        Avm1SourceClass sourceClass)
    {
        if (sourceClass.Initializer is not { } initializer)
            return 0;
        var segments = GetNameSegments(sourceClass.Name.NamespaceName);
        var maximum = 0;
        foreach (var statementIndex in initializer.ResidualStatements)
        {
            if (!statementIndex.IsValid ||
                statementIndex.Value >= initializer.Body.Arena.Statements.Count)
            {
                continue;
            }

            var statement = initializer.Body.Arena[statementIndex];
            if (!statement.Expression.IsValid ||
                !TryGetAssignedPath(
                    initializer.Body.Arena,
                    statement.Expression,
                    out var assignedPath))
            {
                continue;
            }

            assignedPath = NormalizeQualifiedName(assignedPath);
            for (var depth = maximum + 1; depth <= segments.Length; depth++)
            {
                if (assignedPath == string.Join('.', segments, 0, depth))
                    maximum = depth;
            }
        }
        return maximum;
    }

    private static bool TryGetAssignedPath(
        Avm1SourceArena arena,
        SourceExpressionIndex expressionIndex,
        out string path)
    {
        path = string.Empty;
        if (!expressionIndex.IsValid ||
            expressionIndex.Value >= arena.Expressions.Count)
        {
            return false;
        }

        var expression = arena[expressionIndex];
        if (expression.Kind is not Avm1SourceExpressionKind.Assignment ||
            expression.Operator is not Avm1SourceOperator.Assign ||
            expression.Children.Count < 1)
        {
            return false;
        }
        return TryGetStaticPath(arena, arena.GetChild(expression, 0), out path);
    }

    private static bool TryGetStaticPath(
        Avm1SourceArena arena,
        SourceExpressionIndex expressionIndex,
        out string path)
    {
        path = string.Empty;
        if (!expressionIndex.IsValid ||
            expressionIndex.Value >= arena.Expressions.Count)
        {
            return false;
        }

        var expression = arena[expressionIndex];
        switch (expression.Kind)
        {
            case Avm1SourceExpressionKind.SymbolReference
                when expression.Symbol.IsValid &&
                     expression.Symbol.Value < arena.Symbols.Count:
                {
                    var symbol = arena[expression.Symbol];
                    if (symbol.Kind is Avm1SourceSymbolKind.Global)
                    {
                        path = "_global";
                        return true;
                    }
                    if (symbol.Name.IsValid && symbol.Name.Value < arena.Strings.Count)
                    {
                        path = arena[symbol.Name];
                        return true;
                    }
                    return false;
                }
            case Avm1SourceExpressionKind.DynamicName or
                 Avm1SourceExpressionKind.QualifiedName
                when expression.Name.IsValid &&
                     expression.Name.Value < arena.Strings.Count:
                path = arena[expression.Name];
                return true;
            case Avm1SourceExpressionKind.MemberAccess
                when expression.Children.Count >= 2 &&
                     TryGetStaticPath(
                         arena,
                         arena.GetChild(expression, 0),
                         out var receiver) &&
                     TryGetStaticMemberName(
                         arena,
                         arena.GetChild(expression, 1),
                         out var member):
                path = receiver + "." + member;
                return true;
            default:
                return false;
        }
    }

    private static bool TryGetStaticMemberName(
        Avm1SourceArena arena,
        SourceExpressionIndex expressionIndex,
        out string name)
    {
        name = string.Empty;
        if (!expressionIndex.IsValid ||
            expressionIndex.Value >= arena.Expressions.Count)
        {
            return false;
        }

        var expression = arena[expressionIndex];
        if (expression.Kind is Avm1SourceExpressionKind.Literal &&
            expression.Literal.IsValid &&
            expression.Literal.Value < arena.Literals.Count)
        {
            var literal = arena[expression.Literal];
            if (literal.Kind is Avm1SourceLiteralKind.String &&
                literal.StringValue.IsValid &&
                literal.StringValue.Value < arena.Strings.Count)
            {
                name = arena[literal.StringValue];
                return true;
            }
        }
        if (expression.Kind is Avm1SourceExpressionKind.QualifiedName or
                Avm1SourceExpressionKind.DynamicName &&
            expression.Name.IsValid &&
            expression.Name.Value < arena.Strings.Count)
        {
            name = arena[expression.Name];
            return true;
        }
        return false;
    }

    private static void EmitLoadClass(Avm1CodeUnit codeUnit, string name)
    {
        EmitLoadGlobal(codeUnit);
        foreach (var segment in GetNameSegments(NormalizeQualifiedName(name)))
        {
            codeUnit.Emit(new ActionPush([PushValue.String(segment)]));
            codeUnit.Emit(new ActionGetMember());
        }
    }

    private static void EmitLoadGlobal(Avm1CodeUnit codeUnit)
    {
        codeUnit.Emit(new ActionPush([PushValue.String("_global")]));
        codeUnit.Emit(new ActionGetVariable());
    }

    private static string NormalizeQualifiedName(string name)
    {
        while (name.StartsWith("_global.", StringComparison.Ordinal) ||
               name.StartsWith("__Packages.", StringComparison.Ordinal))
        {
            var separator = name.IndexOf('.');
            name = name[(separator + 1)..];
        }
        return name;
    }

    private static string[] GetNameSegments(string name) =>
        string.IsNullOrWhiteSpace(name)
            ? []
            : name.Split('.', StringSplitOptions.RemoveEmptyEntries);

    private static string GetRuntimeName(Avm1SourceClassMember member)
    {
        if (!string.IsNullOrWhiteSpace(member.Origin.RuntimeName))
            return member.Origin.RuntimeName!;
        return member switch
        {
            Avm1SourceMethodDeclaration { Kind: Avm1SourceMethodKind.Getter } =>
                "__get__" + member.Name,
            Avm1SourceMethodDeclaration { Kind: Avm1SourceMethodKind.Setter } =>
                "__set__" + member.Name,
            _ => member.Name
        };
    }

    private static bool IsStatic(Avm1SourceClassMember member) =>
        member.Modifiers.HasFlag(Avm1SourceDeclarationModifiers.Static);

    private static Avm1CompilationOptions CreateFunctionOptions(
        Avm1ClassCompilationOptions options,
        FunctionFlags functionFlags)
    {
        var firstTemporary = checked((byte)(
            GetPreloadRegisterCount(functionFlags) + 1));
        return new(options.SwfVersion)
        {
            OptimizationLevel = options.OptimizationLevel,
            RegisterFile = Avm1CompilationRegisterFile.DefineFunction2,
            RegisterCount = byte.MaxValue,
            ProtectedRegionExitMode = options.AbiProfile is
                Avm1ClassAbiProfile.AdobeFlashCs6Compatible
                    ? Avm1ProtectedRegionExitMode.AdobePhysicalBranch
                    : Avm1ProtectedRegionExitMode.SemanticCompletion,
            ExpressionEvaluationMode = options.AbiProfile is
                Avm1ClassAbiProfile.AdobeFlashCs6Compatible
                    ? Avm1ExpressionEvaluationMode.AdobeFlashCs6Compatible
                    : Avm1ExpressionEvaluationMode.Semantic,
            ReservedTemporaryRegisters = new Avm1TemporaryRegisterRange(
                firstTemporary,
                checked((byte)(byte.MaxValue - firstTemporary)))
        };
    }

    private static Avm1CompilationOptions CreateInitializerOptions(
        Avm1ClassCompilationOptions options) =>
        new(options.SwfVersion)
        {
            OptimizationLevel = options.OptimizationLevel,
            ProtectedRegionExitMode = options.AbiProfile is
                Avm1ClassAbiProfile.AdobeFlashCs6Compatible
                    ? Avm1ProtectedRegionExitMode.AdobePhysicalBranch
                    : Avm1ProtectedRegionExitMode.SemanticCompletion,
            ExpressionEvaluationMode = options.AbiProfile is
                Avm1ClassAbiProfile.AdobeFlashCs6Compatible
                    ? Avm1ExpressionEvaluationMode.AdobeFlashCs6Compatible
                    : Avm1ExpressionEvaluationMode.Semantic
        };

    private static byte GetRequiredRegisterCount(
        IReadOnlyList<Action> actions,
        byte swfVersion)
    {
        var maximum = GetMaximumRegister(actions, swfVersion);
        return maximum < 0
            ? (byte)0
            : checked((byte)(maximum + 1));
    }

    private static int GetMaximumRegister(
        IReadOnlyList<Action> actions,
        byte swfVersion)
    {
        var maximum = -1;
        foreach (var action in actions)
        {
            switch (action)
            {
                case ActionPush push:
                    foreach (var value in push.PushValues)
                    {
                        if (value is PushValue.PushValueRegister register)
                            maximum = Math.Max(maximum, register.RegisterIndex);
                    }
                    break;
                case ActionStoreRegister store:
                    maximum = Math.Max(maximum, store.RegisterNumber);
                    break;
                case ActionWith with:
                    maximum = Math.Max(
                        maximum,
                        GetMaximumRegister(
                            Action.DecodeCollection(with.Body, swfVersion),
                            swfVersion));
                    break;
                case ActionTry actionTry:
                    if (actionTry.Flags.HasFlag(TryFlags.CatchInRegister))
                        maximum = Math.Max(maximum, actionTry.CatchRegister);
                    maximum = Math.Max(
                        maximum,
                        GetMaximumRegister(
                            Action.DecodeCollection(actionTry.TryBody, swfVersion),
                            swfVersion));
                    maximum = Math.Max(
                        maximum,
                        GetMaximumRegister(
                            Action.DecodeCollection(actionTry.CatchBody, swfVersion),
                            swfVersion));
                    maximum = Math.Max(
                        maximum,
                        GetMaximumRegister(
                            Action.DecodeCollection(actionTry.FinallyBody, swfVersion),
                            swfVersion));
                    break;
            }
        }
        return maximum;
    }

    private static byte GetRequiredRegisterCount(byte maximumRegister) =>
        maximumRegister == 0
            ? (byte)0
            : checked((byte)(maximumRegister + 1));

    private static FunctionFlags GetFunctionFlags(
        Avm1SourceClass sourceClass,
        Avm1SourceMethodDeclaration method,
        Avm1ClosureAnalysis closure,
        Avm1ClassAbiProfile profile)
    {
        if (profile is Avm1ClassAbiProfile.Canonical)
            return 0;

        var codeUnitFlags = closure[closure.RootCodeUnit].Flags;
        var flags = FunctionFlags.PreloadThis;
        flags |= codeUnitFlags.HasFlag(Avm1ClosureCodeUnitFlags.UsesArguments)
            ? FunctionFlags.PreloadArguments
            : FunctionFlags.SuppressArguments;
        var usesSuper = codeUnitFlags.HasFlag(Avm1ClosureCodeUnitFlags.UsesSuper) ||
            method.Kind is Avm1SourceMethodKind.Constructor &&
            sourceClass.BaseType is not null;
        flags |= usesSuper
            ? FunctionFlags.PreloadSuper
            : FunctionFlags.SuppressSuper;
        return flags;
    }

    private static byte GetPreloadRegisterCount(FunctionFlags flags) =>
        checked((byte)(
            (flags.HasFlag(FunctionFlags.PreloadThis) ? 1 : 0) +
            (flags.HasFlag(FunctionFlags.PreloadArguments) ? 1 : 0) +
            (flags.HasFlag(FunctionFlags.PreloadSuper) ? 1 : 0) +
            (flags.HasFlag(FunctionFlags.PreloadRoot) ? 1 : 0) +
            (flags.HasFlag(FunctionFlags.PreloadParent) ? 1 : 0) +
            (flags.HasFlag(FunctionFlags.PreloadGlobal) ? 1 : 0)));

    private static void ValidateOptions(
        Avm1SourceClass sourceClass,
        Avm1ClassCompilationOptions options,
        List<Avm1CompilerDiagnostic> diagnostics)
    {
        if (options.SwfVersion < 7)
        {
            AddDiagnostic(
                diagnostics,
                "AVM1CLS001",
                "AS2 class lowering requires SWF 7 or newer.");
        }
        if (!Enum.IsDefined(options.AbiProfile))
        {
            AddDiagnostic(
                diagnostics,
                "AVM1CLS002",
                $"Class ABI profile {options.AbiProfile} is not supported.");
        }
        if (options.MaxDegreeOfParallelism <= 0)
        {
            AddDiagnostic(
                diagnostics,
                "AVM1CLS007",
                "MaxDegreeOfParallelism must be greater than zero.");
        }
        if (!sourceClass.IsComplete)
        {
            AddDiagnostic(
                diagnostics,
                "AVM1CLS003",
                $"Source class {sourceClass.Name} contains incomplete Source HIR.");
        }
        if (sourceClass.Kind is Avm1SourceTypeDeclarationKind.Interface &&
            sourceClass.Fields.Count != 0)
        {
            AddDiagnostic(
                diagnostics,
                "AVM1CLS004",
                $"Source interface {sourceClass.Name} cannot contain fields.");
        }
        if (sourceClass.Kind is Avm1SourceTypeDeclarationKind.Interface &&
            sourceClass.Methods.Any(method => method.HasBody))
        {
            AddDiagnostic(
                diagnostics,
                "AVM1CLS005",
                $"Source interface {sourceClass.Name} cannot contain method bodies.");
        }
        if (sourceClass.Kind is Avm1SourceTypeDeclarationKind.Interface &&
            sourceClass.Methods.Any(method => method.Modifiers.HasFlag(
                Avm1SourceDeclarationModifiers.Static)))
        {
            AddDiagnostic(
                diagnostics,
                "AVM1CLS006",
                $"Source interface {sourceClass.Name} cannot contain static methods.");
        }
    }

    private static void AddAssemblyDiagnostics(
        Avm1ActionBody body,
        List<Avm1CompilerDiagnostic> diagnostics)
    {
        foreach (var diagnostic in body.Diagnostics)
        {
            diagnostics.Add(new Avm1CompilerDiagnostic(
                diagnostic.Code,
                diagnostic.Severity,
                SourceOriginIndex.Invalid,
                diagnostic.ByteOffset,
                diagnostic.Message));
        }
    }

    private static void AddDiagnostic(
        List<Avm1CompilerDiagnostic> diagnostics,
        string code,
        string message) =>
        diagnostics.Add(new Avm1CompilerDiagnostic(
            code,
            Avm1CompilationDiagnosticSeverity.Error,
            SourceOriginIndex.Invalid,
            -1,
            message));

    private static bool HasErrors(
        IReadOnlyList<Avm1CompilerDiagnostic> diagnostics) =>
        diagnostics.Any(diagnostic =>
            diagnostic.Severity is Avm1CompilationDiagnosticSeverity.Error);
}
