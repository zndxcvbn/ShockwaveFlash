using System.Text;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using ShockwaveFlash.Avm1.Decompilation;
using ShockwaveFlash.Avm1.Decompilation.Analysis;
using ShockwaveFlash.Avm1.Decompilation.Emit;
using ShockwaveFlash.Avm1.Decompilation.Ir;
using ShockwaveFlash.Avm1.Swf4;
using ShockwaveFlash.Avm1.Swf5;
using ShockwaveFlash.Avm1.Types;
using ShockwaveFlash.Tags;
using ShockwaveFlash.Tags.Action;
using ShockwaveFlash.Tags.Control;
using ShockwaveFlash.Tags.DisplayList;
using ShockwaveFlash.Tags.Metadata;
using ShockwaveFlash.Tags.Sprite;
using ShockwaveFlash.Avm1.Swf6;
using ShockwaveFlash.Avm1.Swf7;
using ShockwaveFlash.Avm1.Source;

namespace ShockwaveFlash.Avm1.Text;

public static class Avm1ClassDecompiler
{
    // Высокоуровневый метод для извлечения всех классов SWF в указанную папку
    public static void DecompileToFolder(ShockwaveFlashFile swf, string outputFolder, string? basePath = null)
    {
        _ = DecompileToFolderWithMetrics(swf, outputFolder, basePath, projectionOptions: null);
    }

    public static void DecompileToFolder(
        ShockwaveFlashFile swf,
        string outputFolder,
        string? basePath,
        Avm1SourceProjectionOptions? projectionOptions)
    {
        _ = DecompileToFolderWithMetrics(swf, outputFolder, basePath, projectionOptions);
    }

    public static Avm1ClassDecompilationMetrics DecompileToFolderWithMetrics(
        ShockwaveFlashFile swf,
        string outputFolder,
        string? basePath = null)
    {
        return DecompileToFolderWithMetrics(
            swf,
            outputFolder,
            basePath,
            projectionOptions: null);
    }

    public static Avm1ClassDecompilationMetrics DecompileToFolderWithMetrics(
        ShockwaveFlashFile swf,
        string outputFolder,
        string? basePath,
        Avm1SourceProjectionOptions? projectionOptions)
    {
        ArgumentNullException.ThrowIfNull(swf);
        long managedBytesAllocated = 0;
        var allocatedBytesBefore = GC.GetAllocatedBytesForCurrentThread();
        var readStarted = Stopwatch.GetTimestamp();
        var classes = SwfClassReader.Read(swf, basePath);
        var readDuration = Stopwatch.GetElapsedTime(readStarted);
        Directory.CreateDirectory(outputFolder);

        var outputClasses = SelectOutputClasses(classes);
        managedBytesAllocated += GC.GetAllocatedBytesForCurrentThread() - allocatedBytesBefore;

        var environments = new Avm1ClassTypeEnvironment[outputClasses.Length];
        var analysisStarted = Stopwatch.GetTimestamp();
        managedBytesAllocated += MeasureParallelAllocations(outputClasses.Length, i =>
        {
            var cls = outputClasses[i];
            var className = NormalizeClassName(cls.Name);
            PopulateClassMetadata(cls, className, swf.Header.Version);
            environments[i] = BuildTypeEnvironment(cls, className, swf.Header.Version);
            PopulateStaticFields(
                cls,
                className,
                swf.Header.Version,
                environments[i]);
        });

        var sourceFiles = ProjectSourceFiles(
            outputClasses,
            swf.Header.Version,
            environments,
            Avm1TimelineLayout.FromSwf(swf),
            out var sourceProjectionAllocations);
        managedBytesAllocated += sourceProjectionAllocations;
        allocatedBytesBefore = GC.GetAllocatedBytesForCurrentThread();
        var sourceProgram = Avm1SourceProgram.Create(sourceFiles);
        var sourceProjection = sourceProgram.CreateProjection(projectionOptions);
        var sourceMetrics = CollectSourceMetrics(sourceFiles);
        var sourceSemanticallyComplete = sourceProgram.IsSemanticallyComplete;
        var sourceScopeConflictCount = sourceProgram.ArenaBindings.Sum(bindings =>
            bindings.ScopeAnalysis.ConflictCount);
        managedBytesAllocated += GC.GetAllocatedBytesForCurrentThread() - allocatedBytesBefore;
        var analysisDuration = Stopwatch.GetElapsedTime(analysisStarted);

        var sources = new string[outputClasses.Length];
        var emitStarted = Stopwatch.GetTimestamp();
        managedBytesAllocated += MeasureParallelAllocations(outputClasses.Length, i =>
        {
            sources[i] = sourceProjection.Files[i].GetAs2Text();
        });

        allocatedBytesBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < outputClasses.Length; i++)
        {
            var className = sourceFiles[i].Classes.Single().Name.Value;
            var relativePath = GetClassOutputRelativePath(className);
            var fullPath = Path.Combine(outputFolder, relativePath);

            var parentDir = Path.GetDirectoryName(fullPath);
            if (parentDir != null)
            {
                Directory.CreateDirectory(parentDir);
            }

            File.WriteAllText(fullPath, sources[i], Encoding.UTF8);
        }
        managedBytesAllocated += GC.GetAllocatedBytesForCurrentThread() - allocatedBytesBefore;
        var emitDuration = Stopwatch.GetElapsedTime(emitStarted);

        allocatedBytesBefore = GC.GetAllocatedBytesForCurrentThread();
        var methodCount = outputClasses.Sum(cls => cls.Methods.Count + (cls.Constructor is null ? 0 : 1));
        var unsupportedActions = new Dictionary<ActionOpcode, int>();
        var stackDiagnosticCount = 0;
        var stackDepthDiagnosticCount = 0;
        foreach (var cls in outputClasses)
        {
            if (cls.InitializerCore is { } initializerCore)
            {
                CollectCoreMetrics(
                    initializerCore,
                    unsupportedActions,
                    ref stackDiagnosticCount,
                    ref stackDepthDiagnosticCount);
            }

            foreach (var core in cls.MethodCores.Values)
            {
                CollectCoreMetrics(
                    core,
                    unsupportedActions,
                    ref stackDiagnosticCount,
                    ref stackDepthDiagnosticCount);
            }
        }

        var unsupportedActionCounts = unsupportedActions
            .OrderBy(pair => (int)pair.Key)
            .Select(pair => new Avm1ActionOpcodeCount(pair.Key, pair.Value))
            .ToArray();
        managedBytesAllocated += GC.GetAllocatedBytesForCurrentThread() - allocatedBytesBefore;

        return new Avm1ClassDecompilationMetrics(
            outputClasses.Length,
            methodCount,
            readDuration,
            analysisDuration,
            emitDuration,
            unsupportedActionCounts.Sum(item => item.Count),
            stackDiagnosticCount,
            stackDepthDiagnosticCount,
            unsupportedActionCounts)
        {
            IncompleteSourceFileCount = sourceMetrics.IncompleteFileCount,
            SourceErrorCount = sourceMetrics.ErrorCount,
            SourceWarningCount = sourceMetrics.WarningCount,
            SourceOpaqueRegionCount = sourceMetrics.OpaqueRegionCount,
            SourceScopeConflictCount = sourceScopeConflictCount,
            SourceSemanticallyComplete = sourceSemanticallyComplete,
            ManagedBytesAllocated = managedBytesAllocated
        };
    }

    private static long MeasureParallelAllocations(int count, Action<int> action)
    {
        long allocatedBytes = 0;
        Parallel.For(0, count, i =>
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            try
            {
                action(i);
            }
            finally
            {
                Interlocked.Add(
                    ref allocatedBytes,
                    GC.GetAllocatedBytesForCurrentThread() - before);
            }
        });
        return allocatedBytes;
    }

    private static SourceProjectionMetrics CollectSourceMetrics(
        IReadOnlyList<Avm1SourceFile> sourceFiles)
    {
        var arenas = new HashSet<Avm1SourceArena>(ReferenceEqualityComparer.Instance);
        var errorCount = 0;
        var warningCount = 0;
        var opaqueRegionCount = 0;

        foreach (var sourceClass in sourceFiles.SelectMany(file => file.Classes))
        {
            foreach (var field in sourceClass.Fields)
            {
                if (field.Initializer is { } initializer)
                    Collect(initializer.Arena, initializer.Diagnostics);
            }

            foreach (var method in sourceClass.Members.OfType<Avm1SourceMethodDeclaration>())
                Collect(method.Body.Arena, method.Body.Diagnostics);

            if (sourceClass.Initializer is { } classInitializer)
            {
                Collect(
                    classInitializer.Body.Arena,
                    classInitializer.Body.Diagnostics);
            }
        }

        return new SourceProjectionMetrics(
            sourceFiles.Count(file => !file.IsComplete),
            errorCount,
            warningCount,
            opaqueRegionCount);

        void Collect(
            Avm1SourceArena arena,
            IReadOnlyList<Avm1SourceDiagnostic> diagnostics)
        {
            if (!arenas.Add(arena))
                return;

            opaqueRegionCount += arena.OpaqueRegions.Count;
            foreach (var diagnostic in diagnostics)
            {
                if (diagnostic.Severity is Avm1SourceDiagnosticSeverity.Error)
                    errorCount++;
                else if (diagnostic.Severity is Avm1SourceDiagnosticSeverity.Warning)
                    warningCount++;
            }
        }
    }

    private static void CollectCoreMetrics(
        Avm1MethodCore core,
        Dictionary<ActionOpcode, int> unsupportedActions,
        ref int stackDiagnosticCount,
        ref int stackDepthDiagnosticCount)
    {
        stackDiagnosticCount += core.StackIr.Diagnostics.Count;
        stackDepthDiagnosticCount += core.StackDepthAnalysis.Diagnostics.Count;
        foreach (var instruction in core.StackIr.Instructions)
        {
            if (instruction.Op is not Avm1StackIrOp.UnknownAction)
                continue;

            var opcode = core.Instructions[instruction.Action].Action.Opcode;
            unsupportedActions[opcode] = unsupportedActions.GetValueOrDefault(opcode) + 1;
        }
    }

    public static bool TryDecompileMethod(
        ShockwaveFlashFile swf,
        string className,
        string methodName,
        [NotNullWhen(true)] out Avm1MethodDecompilation? decompilation,
        string? basePath = null)
    {
        var normalizedClassName = NormalizeClassName(className);
        var cls = SwfClassReader.Read(swf, basePath)
            .Where(candidate => NormalizeClassName(candidate.Name) == normalizedClassName)
            .OrderByDescending(candidate => IsPackageClass(candidate.Name))
            .ThenByDescending(candidate => candidate.Methods.Count)
            .FirstOrDefault();
        if (cls is null)
        {
            decompilation = null;
            return false;
        }

        if (methodName == "$initializer" && cls.InitializerBody is { } initializer)
        {
            decompilation = Avm1Decompiler.DecompileMethod(
                GetInitializerCore(cls, initializer, swf.Header.Version),
                options: CreateDecompilationOptions(
                    setterPropertyName: null,
                    Avm1TimelineLayout.FromSwf(swf)));
            return true;
        }

        var function = methodName == GetConstructorName(normalizedClassName)
            ? cls.Constructor
            : cls.Methods.FirstOrDefault(candidate => candidate.Name == methodName);
        if (function is null)
        {
            decompilation = null;
            return false;
        }

        var typeEnvironment = BuildTypeEnvironment(cls, normalizedClassName, swf.Header.Version);
        var setterPropertyName = GetSetterPropertyName(function.Name);
        decompilation = Avm1Decompiler.DecompileMethod(
            GetMethodCore(cls, function, swf.Header.Version),
            typeEnvironment,
            CreateDecompilationOptions(
                setterPropertyName,
                Avm1TimelineLayout.FromSwf(swf)));
        return true;
    }

    public static IReadOnlyList<Avm1SourceFile> DecompileSourceFiles(
        ShockwaveFlashFile swf,
        string? basePath = null)
    {
        ArgumentNullException.ThrowIfNull(swf);

        var outputClasses = SelectOutputClasses(SwfClassReader.Read(swf, basePath));
        var environments = new Avm1ClassTypeEnvironment[outputClasses.Length];
        Parallel.For(0, outputClasses.Length, i =>
        {
            var cls = outputClasses[i];
            var className = NormalizeClassName(cls.Name);
            PopulateClassMetadata(cls, className, swf.Header.Version);
            environments[i] = BuildTypeEnvironment(cls, className, swf.Header.Version);
            PopulateStaticFields(cls, className, swf.Header.Version, environments[i]);
        });

        return ProjectSourceFiles(
            outputClasses,
            swf.Header.Version,
            environments,
            Avm1TimelineLayout.FromSwf(swf));
    }

    private static Avm1SourceFile[] ProjectSourceFiles(
        SwfClass[] outputClasses,
        byte swfVersion,
        Avm1ClassTypeEnvironment[] environments,
        Avm1TimelineLayout? timelineLayout)
    {
        return ProjectSourceFiles(
            outputClasses,
            swfVersion,
            environments,
            timelineLayout,
            out _);
    }

    private static Avm1SourceFile[] ProjectSourceFiles(
        SwfClass[] outputClasses,
        byte swfVersion,
        Avm1ClassTypeEnvironment[] environments,
        Avm1TimelineLayout? timelineLayout,
        out long managedBytesAllocated)
    {
        var files = new Avm1SourceFile[outputClasses.Length];
        managedBytesAllocated = MeasureParallelAllocations(outputClasses.Length, i =>
        {
            files[i] = ProjectSourceFile(
                outputClasses[i],
                swfVersion,
                environments[i],
                timelineLayout);
        });
        return files;
    }

    public static Avm1SourceProgram DecompileSourceProgram(
        ShockwaveFlashFile swf,
        string? basePath = null,
        IAvm1ReferenceProvider? referenceProvider = null)
    {
        ArgumentNullException.ThrowIfNull(swf);
        return Avm1SourceProgram.Create(
            DecompileSourceFiles(swf, basePath),
            referenceProvider);
    }

    public static bool TryDecompileSourceFile(
        ShockwaveFlashFile swf,
        string className,
        [NotNullWhen(true)] out Avm1SourceFile? sourceFile,
        string? basePath = null)
    {
        ArgumentNullException.ThrowIfNull(swf);
        ArgumentException.ThrowIfNullOrWhiteSpace(className);

        var normalizedClassName = NormalizeClassName(className);
        var cls = SwfClassReader.Read(swf, basePath)
            .Where(candidate => NormalizeClassName(candidate.Name) == normalizedClassName)
            .OrderByDescending(candidate => IsPackageClass(candidate.Name))
            .ThenByDescending(candidate => candidate.Methods.Count)
            .FirstOrDefault();
        if (cls is null)
        {
            sourceFile = null;
            return false;
        }

        PopulateClassMetadata(cls, normalizedClassName, swf.Header.Version);
        var typeEnvironment = BuildTypeEnvironment(
            cls,
            normalizedClassName,
            swf.Header.Version);
        PopulateStaticFields(
            cls,
            normalizedClassName,
            swf.Header.Version,
            typeEnvironment);
        sourceFile = ProjectSourceFile(
            cls,
            swf.Header.Version,
            typeEnvironment,
            Avm1TimelineLayout.FromSwf(swf));
        return true;
    }

    internal static string NormalizeClassName(string name)
    {
        var normalized = IsPackageClass(name)
            ? name["__Packages.".Length..]
            : name;
        return normalized.Replace("::", ".", StringComparison.Ordinal);
    }

    internal static string GetClassOutputRelativePath(string className)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(className);
        var segments = className
            .Replace("::", ".", StringComparison.Ordinal)
            .Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(SanitizePathSegment)
            .ToArray();
        if (segments.Length == 0)
            return "unnamed.as";

        segments[^1] += ".as";
        return Path.Combine(segments);
    }

    private static string SanitizePathSegment(string segment)
    {
        const string portableInvalidCharacters = "<>:\"/\\|?*";
        var invalidCharacters = Path.GetInvalidFileNameChars().ToHashSet();
        invalidCharacters.UnionWith(portableInvalidCharacters);
        var result = new StringBuilder(segment.Length);
        foreach (var character in segment)
        {
            result.Append(character < ' ' || invalidCharacters.Contains(character)
                ? '_'
                : character);
        }

        var sanitized = result.ToString().TrimEnd(' ', '.');
        if (sanitized.Length == 0)
            sanitized = "_";
        if (IsWindowsReservedFileName(sanitized))
            sanitized = "_" + sanitized;
        return sanitized;
    }

    private static bool IsWindowsReservedFileName(string name)
    {
        var stem = name.Split('.')[0];
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return stem.Length == 4 &&
            (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
            stem[3] is >= '1' and <= '9';
    }

    internal static string GetConstructorName(string className)
    {
        className = className.Replace("::", ".", StringComparison.Ordinal);
        var separator = className.LastIndexOf('.');
        return separator >= 0 ? className[(separator + 1)..] : className;
    }

    internal static ActionDefineFunction2 UpgradeFunction(ActionDefineFunction function) =>
        new(
            function.Name,
            registerCount: 0,
            flags: (FunctionFlags)0,
            function.Parameters.Select(name => new FunctionParameter(0, name)).ToArray(),
            function.Body);

    internal static IReadOnlyList<string>? FindActiveConstantPool(
        IReadOnlyList<Action> actions,
        int actionIndex)
    {
        IReadOnlyList<string>? pool = null;
        var lastAction = Math.Min(actionIndex, actions.Count - 1);
        for (var i = 0; i <= lastAction; i++)
            if (actions[i] is ActionConstantPool constantPool)
                pool = constantPool.Constants;

        return pool;
    }

    private static bool IsPackageClass(string name) =>
        name.StartsWith("__Packages.", StringComparison.Ordinal);

    private static SwfClass[] SelectOutputClasses(IEnumerable<SwfClass> classes) =>
        classes
            .GroupBy(cls => NormalizeClassName(cls.Name), StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(cls => IsPackageClass(cls.Name))
                .ThenByDescending(cls => cls.Methods.Count)
                .First())
            .OrderBy(cls => NormalizeClassName(cls.Name), StringComparer.Ordinal)
            .ToArray();

    private static void DecompileClass(
        StringBuilder builder,
        SwfClass cls,
        string className,
        byte swfVersion,
        Avm1ClassTypeEnvironment typeEnvironment)
    {
        builder.Append("class ").Append(className);
        if (!string.IsNullOrEmpty(cls.BaseClassName))
            builder.Append(" extends ").Append(GetConstructorName(cls.BaseClassName));
        else if (cls.InitializerBody is not { Length: > 0 })
            builder.Append(" extends MovieClip");

        if (cls.InterfaceNames.Count > 0)
        {
            builder.Append(" implements ");
            for (var i = 0; i < cls.InterfaceNames.Count; i++)
            {
                if (i > 0)
                    builder.Append(", ");
                builder.Append(GetConstructorName(cls.InterfaceNames[i]));
            }
        }

        builder.Append("\n{\n");

        // Поля экземпляра без инициализации (по алфавиту)
        foreach (var field in cls.Fields
                     .Where(field => field.Initializer is null)
                     .OrderBy(field => field.Name, StringComparer.Ordinal))
        {
            AppendField(builder, field, isStatic: false);
        }

        // Статические поля
        foreach (var staticField in cls.StaticFields.Where(field =>
                     !cls.StaticMethodNames.Contains(field.Name)))
        {
            AppendField(builder, staticField, isStatic: true);
        }

        // Поля экземпляра с инициализацией
        foreach (var field in cls.Fields.Where(field => field.Initializer is not null))
            AppendField(builder, field, isStatic: false);

        if (cls.Constructor is not null)
        {
            // ИСПРАВЛЕНО: Вычисляем контекст конструктора для регистрации "this" и super
            var sig = string.Join(", ", cls.Constructor.Parameters.Select(p => p.Name));
            builder.Append("\n   function ").Append(GetConstructorName(className)).Append('(').Append(sig).Append(")\n   {\n");
            builder.Append(DecompileBody(
                GetMethodCore(cls, cls.Constructor, swfVersion),
                typeEnvironment));
            builder.Append("   }\n");
        }

        foreach (var method in cls.Methods)
        {
            var sig = string.Join(", ", method.Parameters.Select(p => p.Name));
            // ИСПРАВЛЕНО: Вычисляем контекст метода для регистрации параметров и "this"
            var methodName = method.Name;
            var setterName = GetSetterPropertyName(methodName);

            if (methodName.StartsWith("__get__", StringComparison.Ordinal))
                methodName = "get " + methodName[7..];
            else if (setterName is not null)
                methodName = "set " + setterName;

            builder.Append("\n   ");
            if (cls.StaticMethodNames.Contains(method.Name))
                builder.Append("static ");
            builder.Append("function ").Append(methodName).Append('(').Append(sig).Append(")\n   {\n");
            var body = DecompileBody(
                GetMethodCore(cls, method, swfVersion),
                typeEnvironment,
                new Avm1MethodDecompilationOptions(setterName));
            builder.Append(body);
            builder.Append("   }\n");
        }

        builder.Append("}\n");
    }

    private static void AppendField(
        StringBuilder builder,
        RecoveredField field,
        bool isStatic)
    {
        builder.Append("   ");
        if (isStatic)
            builder.Append("static ");
        builder.Append("var ").Append(field.Name);
        if (field.LegacyInitializerText is not null)
            builder.Append(" = ").Append(field.LegacyInitializerText);
        builder.Append(";\n");
    }

    private static Avm1SourceFile ProjectSourceFile(
        SwfClass cls,
        byte swfVersion,
        Avm1ClassTypeEnvironment typeEnvironment,
        Avm1TimelineLayout? timelineLayout)
    {
        var className = NormalizeClassName(cls.Name);
        var members = new List<Avm1SourceClassMember>(
            cls.Fields.Count + cls.StaticFields.Count + cls.Methods.Count + 1);
        var instanceMethodNames = cls.Methods
            .Where(method => !cls.StaticMethodNames.Contains(method.Name))
            .SelectMany(method => GetSourceMemberNames(method.Name))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var field in cls.Fields
                     .Where(field =>
                         field.Initializer is null &&
                         !instanceMethodNames.Contains(field.Name))
                     .OrderBy(field => field.Name, StringComparer.Ordinal))
        {
            members.Add(ProjectSourceField(field, isStatic: false));
        }

        foreach (var field in cls.StaticFields.Where(field =>
                     !cls.StaticMethodNames.Contains(field.Name)))
        {
            members.Add(ProjectSourceField(field, isStatic: true));
        }

        foreach (var field in cls.Fields.Where(field =>
                     field.Initializer is not null &&
                     !instanceMethodNames.Contains(field.Name)))
            members.Add(ProjectSourceField(field, isStatic: false));

        if (cls.Constructor is not null)
        {
            var body = Avm1Decompiler.DecompileMethod(
                GetMethodCore(cls, cls.Constructor, swfVersion),
                typeEnvironment,
                CreateDecompilationOptions(
                    setterPropertyName: null,
                    timelineLayout)).ProjectSource();
            members.Add(new Avm1SourceMethodDeclaration(
                GetConstructorName(className),
                Avm1SourceMethodKind.Constructor,
                body,
                origin: new Avm1SourceMemberOrigin(cls.Constructor.Name)));
        }

        foreach (var method in cls.Methods)
        {
            var kind = Avm1SourceMethodKind.Method;
            var sourceName = method.Name;
            var setterName = GetSetterPropertyName(sourceName);
            if (sourceName.StartsWith("__get__", StringComparison.Ordinal))
            {
                kind = Avm1SourceMethodKind.Getter;
                sourceName = sourceName[7..];
            }
            else if (setterName is not null)
            {
                kind = Avm1SourceMethodKind.Setter;
                sourceName = setterName;
            }

            var body = Avm1Decompiler.DecompileMethod(
                GetMethodCore(cls, method, swfVersion),
                typeEnvironment,
                CreateDecompilationOptions(
                    setterName,
                    timelineLayout)).ProjectSource();
            var modifiers = cls.StaticMethodNames.Contains(method.Name)
                ? Avm1SourceDeclarationModifiers.Static
                : Avm1SourceDeclarationModifiers.None;
            members.Add(new Avm1SourceMethodDeclaration(
                sourceName,
                kind,
                body,
                modifiers,
                origin: new Avm1SourceMemberOrigin(method.Name)));
        }

        members = AttachMethodBytecodeOrigins(
            members,
            cls.InitializerBody,
            swfVersion);

        Avm1SourceQualifiedName? baseType = null;
        if (!string.IsNullOrEmpty(cls.BaseClassName))
        {
            baseType = new Avm1SourceQualifiedName(NormalizeClassName(cls.BaseClassName));
        }
        else if (cls.InitializerBody is not { Length: > 0 })
        {
            baseType = new Avm1SourceQualifiedName("MovieClip");
        }

        Avm1SourceClassInitializer? sourceInitializer = null;
        if (cls.InitializerBody is { Length: > 0 } initializer)
        {
            var decompilation = GetInitializerDecompilation(
                cls,
                initializer,
                swfVersion,
                typeEnvironment,
                timelineLayout);
            var analysis = cls.StaticFieldAnalysis ?? Avm1StaticFieldAnalysis.Build(
                decompilation,
                className,
                cls.Methods.Select(method => method.Name).ToHashSet(StringComparer.Ordinal));
            sourceInitializer = Avm1ClassInitializerProjector.Project(
                decompilation,
                className,
                members,
                analysis.RegisterAliases);
        }

        var classOrigin = new Avm1SourceClassOrigin(
            cls.SpriteId,
            cls.Name,
            cls.InitializerBody is { Length: > 0 });
        var sourceClass = new Avm1SourceClass(
            new Avm1SourceQualifiedName(className),
            baseType,
            cls.InterfaceNames.Select(name =>
                new Avm1SourceQualifiedName(NormalizeClassName(name))),
            members,
            origin: classOrigin,
            initializer: sourceInitializer);
        if (cls.InitializerBody is { Length: > 0 } originalInitializer)
        {
            classOrigin = classOrigin with
            {
                Bytecode = new Avm1SourceClassBytecodeOrigin(
                    swfVersion,
                    originalInitializer,
                    Avm1SourceFingerprint.ComputeClass(sourceClass),
                    Avm1SourceFingerprint.ComputeClassShape(sourceClass))
            };
            sourceClass = new Avm1SourceClass(
                sourceClass.Name,
                sourceClass.BaseType,
                sourceClass.Interfaces,
                sourceClass.Members,
                sourceClass.Modifiers,
                classOrigin,
                sourceClass.Initializer);
        }
        return new Avm1SourceFile([sourceClass]);
    }

    private static List<Avm1SourceClassMember> AttachMethodBytecodeOrigins(
        List<Avm1SourceClassMember> members,
        ReadOnlyMemory<byte>? initializerBody,
        byte swfVersion)
    {
        if (initializerBody is not { Length: > 0 } initializer)
            return members;

        try
        {
            var actions = Action.DecodeCollection(
                initializer,
                swfVersion,
                strict: true);
            var functions = actions
                .Select((action, index) => (action, index))
                .Where(item => item.action is ActionDefineFunction2)
                .Select(item => (
                    Function: (ActionDefineFunction2)item.action,
                    ActionIndex: item.index,
                    ConstantPool: FindActiveConstantPool(actions, item.index)))
                .ToArray();
            var methods = members
                .OfType<Avm1SourceMethodDeclaration>()
                .ToArray();
            if (functions.Length != methods.Length)
                return members;

            for (var i = 0; i < methods.Length; i++)
            {
                var sourceParameters = methods[i].Body.Parameters
                    .Select(parameter =>
                    {
                        var symbol = methods[i].Body.Arena[parameter];
                        return methods[i].Body.Arena[symbol.Name];
                    });
                if (!sourceParameters.SequenceEqual(
                        functions[i].Function.Parameters.Select(parameter =>
                            parameter.Name),
                        StringComparer.Ordinal))
                {
                    return members;
                }
            }

            var replacements = new Dictionary<
                Avm1SourceMethodDeclaration,
                Avm1SourceMethodDeclaration>();
            for (var i = 0; i < methods.Length; i++)
            {
                var method = methods[i];
                var function = functions[i];
                var bytecodeOrigin = new Avm1SourceMethodBytecodeOrigin(
                    swfVersion,
                    function.ActionIndex,
                    function.Function.Body,
                    function.Function.RegisterCount,
                    (ushort)function.Function.Flags,
                    function.Function.Parameters.Select(parameter =>
                        new Avm1SourceFunctionParameterOrigin(
                            parameter.Register,
                            parameter.Name)),
                    function.ConstantPool,
                    Avm1SourceFingerprint.ComputeMethod(method.Body));
                replacements.Add(
                    method,
                    new Avm1SourceMethodDeclaration(
                        method.Name,
                        method.Kind,
                        method.Body,
                        method.Modifiers,
                        method.DeclaredReturnType,
                        method.InferredReturnType,
                        new Avm1SourceMemberOrigin(
                            method.Origin.RuntimeName,
                            bytecodeOrigin)));
            }

            return members
                .Select(member => member is Avm1SourceMethodDeclaration method
                    ? replacements[method]
                    : member)
                .ToList();
        }
        catch (Exception exception) when (exception is not (
            OutOfMemoryException or
            StackOverflowException or
            OperationCanceledException))
        {
            return members;
        }
    }

    private static IEnumerable<string> GetSourceMemberNames(string runtimeName)
    {
        yield return runtimeName;
        if (runtimeName.StartsWith("__get__", StringComparison.Ordinal))
        {
            yield return runtimeName[7..];
            yield break;
        }

        var setterName = GetSetterPropertyName(runtimeName);
        if (setterName is not null)
            yield return setterName;
    }

    private static Avm1SourceField ProjectSourceField(
        RecoveredField field,
        bool isStatic) =>
        new(
            field.Name,
            isStatic
                ? Avm1SourceDeclarationModifiers.Static
                : Avm1SourceDeclarationModifiers.None,
            initializer: field.Initializer,
            origin: new Avm1SourceMemberOrigin(field.Name));

    private static FunctionContext BuildContext(ActionDefineFunction2 func)
    {
        return new FunctionContext(func.Flags, func.Parameters);
    }

    private static Avm1ClassTypeEnvironment BuildTypeEnvironment(
        SwfClass cls,
        string className,
        byte swfVersion)
    {
        var methods = new List<Avm1ClassMethodCoreInput>(cls.Methods.Count + 2);
        if (cls.InitializerBody is { } initializer && initializer.Length > 0)
        {
            methods.Add(new Avm1ClassMethodCoreInput(
                GetInitializerCore(cls, initializer, swfVersion),
                Avm1ClassBodyKind.Initializer));
        }

        if (cls.Constructor is { Body.Length: > 0 } constructor)
        {
            methods.Add(new Avm1ClassMethodCoreInput(
                GetMethodCore(cls, constructor, swfVersion),
                Avm1ClassBodyKind.Constructor));
        }

        foreach (var method in cls.Methods)
        {
            if (method.Body.Length == 0)
                continue;

            methods.Add(new Avm1ClassMethodCoreInput(
                GetMethodCore(cls, method, swfVersion),
                Avm1ClassBodyKind.Method));
        }

        return Avm1ClassTypeAnalysis.Build(className, methods);
    }

    private static void PopulateClassMetadata(
        SwfClass cls,
        string className,
        byte swfVersion)
    {
        cls.BaseClassName = null;
        cls.InterfaceNames.Clear();
        if (cls.InitializerBody is not { Length: > 0 } initializer)
            return;

        var metadata = Avm1ClassMetadataAnalysis.Build(
            GetInitializerCore(cls, initializer, swfVersion),
            className);
        cls.BaseClassName = metadata.BaseClassName;
        cls.InterfaceNames.AddRange(metadata.InterfaceNames);
    }

    private static void PopulateStaticFields(
        SwfClass cls,
        string className,
        byte swfVersion,
        Avm1ClassTypeEnvironment typeEnvironment)
    {
        cls.StaticFields.Clear();
        cls.StaticFieldAnalysis = null;

        var methodNames = new HashSet<string>(
            cls.Methods.Select(method => method.Name),
            StringComparer.Ordinal);
        var excludedMemberNames = new HashSet<string>(
            cls.StaticMethodNames,
            StringComparer.Ordinal)
        {
            "prototype",
            "__constructor__"
        };
        foreach (var methodName in cls.StaticMethodNames)
        {
            if (methodName.StartsWith("__get__", StringComparison.Ordinal) ||
                methodName.StartsWith("__set__", StringComparison.Ordinal))
            {
                excludedMemberNames.Add(methodName[7..]);
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (cls.InitializerBody is { Length: > 0 } initializer)
        {
            var decompilation = GetInitializerDecompilation(
                cls,
                initializer,
                swfVersion,
                typeEnvironment);
            var analysis = Avm1StaticFieldAnalysis.Build(
                decompilation,
                className,
                methodNames);
            cls.StaticFieldAnalysis = analysis;
            foreach (var binding in analysis.Bindings)
            {
                string? initializerText = null;
                Avm1SourceExpressionFragment? initializerExpression = null;
                if (binding.Initializer.IsValid)
                {
                    initializerText = Avm1StructuredAs2Emitter.GetExpressionText(
                        decompilation,
                        binding.Initializer,
                        analysis.RegisterAliases);
                    initializerExpression = Avm1SourceProjector.ProjectExpression(
                        decompilation,
                        binding.Initializer,
                        analysis.RegisterAliases);
                }

                if (seen.Add(binding.Name))
                {
                    cls.StaticFields.Add(new RecoveredField(
                        binding.Name,
                        initializerText,
                        initializerExpression));
                }
            }
        }

        var referencedFields = new HashSet<string>(StringComparer.Ordinal);
        if (cls.Constructor is { Body.Length: > 0 } constructor)
        {
            Avm1StaticFieldAnalysis.CollectReferencedFields(
                GetMethodCore(cls, constructor, swfVersion),
                className,
                excludedMemberNames,
                referencedFields);
        }

        foreach (var method in cls.Methods)
        {
            if (method.Body.Length == 0)
                continue;

            Avm1StaticFieldAnalysis.CollectReferencedFields(
                GetMethodCore(cls, method, swfVersion),
                className,
                excludedMemberNames,
                referencedFields);
        }

        foreach (var field in referencedFields.Order(StringComparer.Ordinal))
        {
            if (seen.Add(field))
                cls.StaticFields.Add(new RecoveredField(field));
        }
    }

    private static Avm1MethodCore GetInitializerCore(
        SwfClass cls,
        ReadOnlyMemory<byte> initializer,
        byte swfVersion)
    {
        return cls.InitializerCore ??= Avm1Decompiler.BuildMethodCore(initializer, swfVersion);
    }

    private static Avm1MethodDecompilation GetInitializerDecompilation(
        SwfClass cls,
        ReadOnlyMemory<byte> initializer,
        byte swfVersion,
        Avm1ClassTypeEnvironment typeEnvironment,
        Avm1TimelineLayout? timelineLayout = null)
    {
        if (timelineLayout is not null)
        {
            return Avm1Decompiler.DecompileMethod(
                GetInitializerCore(cls, initializer, swfVersion),
                typeEnvironment,
                CreateDecompilationOptions(
                    setterPropertyName: null,
                    timelineLayout));
        }

        return cls.InitializerDecompilation ??= Avm1Decompiler.DecompileMethod(
            GetInitializerCore(cls, initializer, swfVersion),
            typeEnvironment);
    }

    private static Avm1MethodDecompilationOptions CreateDecompilationOptions(
        string? setterPropertyName,
        Avm1TimelineLayout? timelineLayout) =>
        new(setterPropertyName)
        {
            TimelineLayout = timelineLayout
        };

    private static Avm1MethodCore GetMethodCore(
        SwfClass cls,
        ActionDefineFunction2 function,
        byte swfVersion)
    {
        if (!cls.MethodCores.TryGetValue(function, out var core))
        {
            core = Avm1Decompiler.BuildMethodCore(function.Body, swfVersion, BuildContext(function));
            cls.MethodCores.Add(function, core);
        }

        return core;
    }

    // Декомпиляция тела метода через НОВЫЙ конвейер Avm1Decompiler
    private static string DecompileBody(
        Avm1MethodCore core,
        Avm1ClassTypeEnvironment typeEnvironment,
        Avm1MethodDecompilationOptions options = default)
    {
        if (core.Instructions.Count == 0)
            return string.Empty;

        // Запуск нового SSA IR и AST конвейера с передачей контекста регистров
        var decompilation = Avm1Decompiler.DecompileMethod(core, typeEnvironment, options);

        using var writer = new StringWriter();
        var emitter = new Avm1StructuredAs2Emitter(writer, initialIndent: 2);
        emitter.Write(decompilation);

        var result = writer.ToString();
        return result.EndsWith('\n') ? result : result + "\n";
    }

    private static string? GetSetterPropertyName(string methodName) =>
        methodName.StartsWith("__set__", StringComparison.Ordinal)
            ? methodName[7..]
            : null;

    private sealed class RecoveredField
    {
        public RecoveredField(
            string name,
            string? legacyInitializerText = null,
            Avm1SourceExpressionFragment? initializer = null)
        {
            Name = name;
            LegacyInitializerText = legacyInitializerText;
            Initializer = initializer;
        }

        public string Name { get; }

        public string? LegacyInitializerText { get; set; }

        public Avm1SourceExpressionFragment? Initializer { get; set; }
    }

    private enum RecoveredFieldValueKind : byte
    {
        Undefined,
        Null,
        Boolean,
        Integer,
        Number,
        String,
        Name,
        Member,
        Array,
        New,
        Add,
        Function
    }

    private sealed class RecoveredFieldValue
    {
        private RecoveredFieldValue(
            RecoveredFieldValueKind kind,
            string? text = null,
            bool booleanValue = false,
            int integerValue = 0,
            double numberValue = 0,
            IReadOnlyList<RecoveredFieldValue>? children = null)
        {
            Kind = kind;
            Text = text;
            BooleanValue = booleanValue;
            IntegerValue = integerValue;
            NumberValue = numberValue;
            Children = children?.ToArray() ?? [];
        }

        public RecoveredFieldValueKind Kind { get; }

        public string? Text { get; }

        public bool BooleanValue { get; }

        public int IntegerValue { get; }

        public double NumberValue { get; }

        public RecoveredFieldValue[] Children { get; }

        public static RecoveredFieldValue Undefined() =>
            new(RecoveredFieldValueKind.Undefined);

        public static RecoveredFieldValue Null() =>
            new(RecoveredFieldValueKind.Null);

        public static RecoveredFieldValue Boolean(bool value) =>
            new(RecoveredFieldValueKind.Boolean, booleanValue: value);

        public static RecoveredFieldValue Integer(int value) =>
            new(RecoveredFieldValueKind.Integer, integerValue: value);

        public static RecoveredFieldValue Number(double value) =>
            new(RecoveredFieldValueKind.Number, numberValue: value);

        public static RecoveredFieldValue String(string value) =>
            new(RecoveredFieldValueKind.String, text: value);

        public static RecoveredFieldValue Name(string value) =>
            new(RecoveredFieldValueKind.Name, text: value);

        public static RecoveredFieldValue Member(
            RecoveredFieldValue receiver,
            string member) =>
            new(
                RecoveredFieldValueKind.Member,
                text: member,
                children: [receiver]);

        public static RecoveredFieldValue Array(IReadOnlyList<RecoveredFieldValue> values) =>
            new(RecoveredFieldValueKind.Array, children: values);

        public static RecoveredFieldValue New(
            RecoveredFieldValue constructor,
            IReadOnlyList<RecoveredFieldValue> arguments) =>
            new(
                RecoveredFieldValueKind.New,
                children: [constructor, .. arguments]);

        public static RecoveredFieldValue Add(
            RecoveredFieldValue left,
            RecoveredFieldValue right) =>
            new(RecoveredFieldValueKind.Add, children: [left, right]);

        public static RecoveredFieldValue Function(string name) =>
            new(RecoveredFieldValueKind.Function, text: name);
    }

    private sealed class SwfClass
    {
        public string Name { get; set; } = string.Empty;
        public ushort SpriteId { get; set; }
        public ReadOnlyMemory<byte>? InitializerBody { get; set; }
        public ActionDefineFunction2? Constructor { get; set; }
        public List<ActionDefineFunction2> Methods { get; } = [];
        public Avm1MethodCore? InitializerCore { get; set; }
        public Avm1MethodDecompilation? InitializerDecompilation { get; set; }
        public Avm1StaticFieldAnalysis? StaticFieldAnalysis { get; set; }
        public Dictionary<ActionDefineFunction2, Avm1MethodCore> MethodCores { get; } = [];
        public List<RecoveredField> Fields { get; } = [];
        public List<RecoveredField> StaticFields { get; } = [];
        public HashSet<string> StaticMethodNames { get; } = new(StringComparer.Ordinal);
        public string? BaseClassName { get; set; }
        public List<string> InterfaceNames { get; } = [];
    }

    // SwfClassReader использует исходный разбор тегов и поиск паттернов из вашего первого декомпилятора
    private static class SwfClassReader
    {
        public static List<SwfClass> Read(ShockwaveFlashFile swf, string? basePath = null)
        {
            if (swf.Tags.OfType<FileAttributesTag>().Any(tag => tag.IsActionScript3))
                return [];

            var exportNames = new Dictionary<ushort, string>();
            var sprites = new Dictionary<ushort, DefineSpriteTag>();
            var initActions = new Dictionary<ushort, ReadOnlyMemory<byte>>();
            var importMappings = new List<(ushort localId, string assetName, string url)>();

            foreach (var tag in swf.Tags)
            {
                switch (tag)
                {
                    case ExportAssetsTag export:
                        foreach (var asset in export.Assets)
                            exportNames[asset.Id] = asset.Name;
                        break;
                    case DefineSpriteTag sprite:
                        sprites[sprite.Id] = sprite;
                        break;
                    case DoInitActionTag init:
                        initActions[init.Id] = init.Data;
                        break;
                    case ImportAssets2Tag imp:
                        foreach (var asset in imp.Assets)
                            importMappings.Add((asset.Id, asset.Name, imp.Url));
                        break;
                    case ImportAssetsTag imp:
                        foreach (var asset in imp.Assets)
                            importMappings.Add((asset.Id, asset.Name, imp.Url));
                        break;
                }
            }

            var classes = new List<SwfClass>();

            foreach (var (id, name) in exportNames)
            {
                if (!sprites.TryGetValue(id, out var sprite))
                    continue;

                var cls = new SwfClass
                {
                    Name = name,
                    SpriteId = id
                };

                if (initActions.TryGetValue(id, out var initData))
                {
                    cls.Constructor = new ActionDefineFunction2(
                        name: name,
                        registerCount: 1,
                        flags: FunctionFlags.PreloadThis | FunctionFlags.PreloadSuper,
                        parameters: Array.Empty<FunctionParameter>(),
                        body: initData);
                    cls.InitializerBody = initData;
                }

                var frameActions = ExtractFrameActions(sprite.Tags, swf.Header.Version);

                foreach (var action in frameActions)
                {
                    if (action is ActionDefineFunction2 func && func.Name.Length > 0)
                    {
                        if (func.Name == name)
                            cls.Constructor = func;
                        else
                            cls.Methods.Add(func);
                    }
                }

                ExtractMethodsFromMemberAssignments(cls, swf.Header.Version);
                ExtractPrototypeMethodsFromConstructor(cls, swf.Header.Version);
                ExtractFields(cls, swf.Header.Version);

                classes.Add(cls);
            }

            if (basePath is not null)
            {
                foreach (var (localId, assetName, url) in importMappings)
                {
                    var clsIdx = classes.FindIndex(c => c.SpriteId == localId);
                    if (clsIdx < 0)
                        continue;

                    var externalSwf = LoadExternalSwf(url, basePath);
                    if (externalSwf is null)
                        continue;

                    MergeExternalSpriteActions(classes[clsIdx], externalSwf, swf.Header.Version);
                }
            }

            return classes;
        }

        private static ShockwaveFlashFile? LoadExternalSwf(string url, string basePath)
        {
            try
            {
                var resolved = Path.GetFullPath(Path.Combine(basePath, url));
                if (!File.Exists(resolved))
                    return null;
                var bytes = File.ReadAllBytes(resolved);
                var swf = ShockwaveFlashFile.Disassemble(bytes);
                return swf;
            }
            catch
            {
                return null;
            }
        }

        private static void MergeExternalSpriteActions(SwfClass cls, ShockwaveFlashFile externalSwf, byte swfVersion)
        {
            ushort? externalSpriteId = null;

            foreach (var tag in externalSwf.Tags)
            {
                if (tag is ExportAssetsTag export)
                {
                    foreach (var asset in export.Assets)
                    {
                        if (asset.Name == cls.Name)
                        {
                            externalSpriteId = asset.Id;
                            break;
                        }
                    }
                }
                if (externalSpriteId.HasValue)
                    break;
            }

            if (!externalSpriteId.HasValue)
                return;

            DefineSpriteTag? externalSprite = null;
            foreach (var tag in externalSwf.Tags)
            {
                if (tag is DefineSpriteTag sprite && sprite.Id == externalSpriteId.Value)
                {
                    externalSprite = sprite;
                    break;
                }
            }

            if (externalSprite is null)
                return;

            ReadOnlyMemory<byte>? externalInit = null;
            foreach (var tag in externalSwf.Tags)
            {
                if (tag is DoInitActionTag init && init.Id == externalSpriteId.Value)
                {
                    externalInit = init.Data;
                    break;
                }
            }

            if (externalInit.HasValue)
            {
                cls.Constructor = new ActionDefineFunction2(
                    name: cls.Name,
                    registerCount: 1,
                    flags: FunctionFlags.PreloadThis | FunctionFlags.PreloadSuper,
                    parameters: Array.Empty<FunctionParameter>(),
                    body: externalInit.Value);
                cls.InitializerBody = externalInit.Value;
            }

            var externalActions = ExtractFrameActions(externalSprite.Tags, swfVersion);
            var seenNames = new HashSet<string>(cls.Methods.Select(m => m.Name));

            foreach (var action in externalActions)
            {
                if (action is ActionDefineFunction2 func && func.Name.Length > 0 && !seenNames.Contains(func.Name))
                {
                    seenNames.Add(func.Name);
                    cls.Methods.Add(func);
                }
            }

            ExtractMethodsFromMemberAssignments(cls, swfVersion);
            ExtractPrototypeMethodsFromConstructor(cls, swfVersion);
            ExtractFields(cls, swfVersion);
        }

        private static void ExtractMethodsFromMemberAssignments(SwfClass cls, byte swfVersion)
        {
            var seenNames = new HashSet<string>(cls.Methods.Select(m => m.Name));

            if (cls.Constructor?.Body.Length > 0)
            {
                var bodyActions = Action.DecodeCollection(cls.Constructor.Body, swfVersion);
                WalkForMethodAssignments(bodyActions, swfVersion, cls, seenNames);
            }
        }

        private static void WalkForMethodAssignments(IReadOnlyList<Action> actions, byte swfVersion, SwfClass cls, HashSet<string> seenNames)
        {
            var stack = new List<object>();

            for (var i = 0; i < actions.Count; i++)
            {
                var action = actions[i];

                if (action is ActionDefineFunction2 func)
                {
                    stack.Add(func);
                    continue;
                }

                if (action is ActionDefineFunction f)
                {
                    stack.Add(f);
                    continue;
                }

                if (action is ActionPush push)
                {
                    foreach (var v in push.PushValues)
                        stack.Add(v);
                    continue;
                }

                if (action is ActionGetVariable)
                {
                    if (stack.Count > 0)
                        _ = Pop(stack);
                    stack.Add("this");
                    continue;
                }

                if (action is ActionGetMember && stack.Count >= 2)
                {
                    var name = Pop(stack);
                    var obj = Pop(stack);
                    if (name is PushValue.PushValueString nameStr && nameStr.Value == "prototype")
                        stack.Add(new PrototypeRef(obj));
                    else
                        stack.Add(obj);
                    continue;
                }

                if (action is ActionPushDuplicate && stack.Count > 0)
                {
                    stack.Add(stack[^1]);
                    continue;
                }

                if (action is ActionSetMember && stack.Count >= 3)
                {
                    var valuePop = Pop(stack);
                    var namePop = Pop(stack);
                    _ = Pop(stack); // obj

                    var nameStr = namePop is PushValue pv ? GetStringFromPushValue(pv, actions) : null;
                    if (nameStr is not null &&
                        !IsConstructorRuntimeName(nameStr, cls.Name) &&
                        valuePop is Action valueAction &&
                        IsAnonymousFunc(valueAction, out var funcVal) &&
                        !seenNames.Contains(nameStr))
                    {
                        seenNames.Add(nameStr);
                        var namedBody = PrependConstantPool(funcVal.Body, actions, FindActionIndex(actions, valueAction));
                        cls.Methods.Add(new ActionDefineFunction2(
                            name: nameStr,
                            registerCount: funcVal.RegisterCount,
                            flags: funcVal.Flags,
                            parameters: funcVal.Parameters,
                            body: namedBody ?? funcVal.Body));
                    }
                    continue;
                }

                if (action is ActionSetVariable && stack.Count >= 2)
                {
                    var valueSV = Pop(stack);
                    var nameSV = Pop(stack);

                    var nameStr = nameSV is PushValue pv2 ? GetStringFromPushValue(pv2, actions) : null;
                    if (nameStr is not null &&
                        !IsConstructorRuntimeName(nameStr, cls.Name) &&
                        valueSV is Action valueActionSV &&
                        IsAnonymousFunc(valueActionSV, out var funcVal) &&
                        !seenNames.Contains(nameStr))
                    {
                        seenNames.Add(nameStr);
                        var namedBody = PrependConstantPool(funcVal.Body, actions, FindActionIndex(actions, valueActionSV));
                        cls.Methods.Add(new ActionDefineFunction2(
                            name: nameStr,
                            registerCount: funcVal.RegisterCount,
                            flags: funcVal.Flags,
                            parameters: funcVal.Parameters,
                            body: namedBody ?? funcVal.Body));
                    }
                    continue;
                }

                if (action is ActionAdd or ActionAdd2 or ActionStringAdd or
                    ActionSubtract or ActionMultiply or ActionDivide or ActionModulo or
                    ActionEquals or ActionEquals2 or ActionStringEquals or
                    ActionLess or ActionLess2 or ActionStringLess or
                    ActionGreater or ActionStringGreater or
                    ActionAnd or ActionOr or
                    ActionBitAnd or ActionBitOr or ActionBitXor)
                {
                    if (stack.Count >= 2) { _ = Pop(stack); _ = Pop(stack); }
                    continue;
                }

                if (action is ActionNot or ActionIncrement or ActionDecrement)
                {
                    if (stack.Count >= 1) _ = Pop(stack);
                    continue;
                }

                if (action is ActionTrace or ActionPop)
                {
                    if (stack.Count >= 1) _ = Pop(stack);
                    continue;
                }

                if (action is ActionStoreRegister && stack.Count >= 1)
                    _ = Pop(stack);
            }
        }

        private sealed record PrototypeRef(object Constructor);

        private static bool IsConstructorRuntimeName(
            string memberName,
            string className)
        {
            var shortName = ShortName(className);
            if (memberName == className ||
                memberName == shortName ||
                memberName == "__Packages." + shortName)
            {
                return true;
            }

            return memberName.StartsWith("Class_", StringComparison.OrdinalIgnoreCase) &&
                NormalizeLegacyClassIdentifier(memberName)
                    .EndsWith(
                        NormalizeLegacyClassIdentifier(shortName),
                        StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeLegacyClassIdentifier(string value)
        {
            var builder = new StringBuilder(value.Length);
            foreach (var character in value)
            {
                if (char.IsLetterOrDigit(character))
                    builder.Append(character);
            }
            return builder.ToString();
        }

        private static object Pop(List<object> stack)
        {
            var item = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            return item;
        }

        private static List<Action> ExtractFrameActions(List<Tag> spriteTags, byte swfVersion)
        {
            var allActions = new List<Action>();

            foreach (var tag in spriteTags)
            {
                switch (tag)
                {
                    case DoActionTag doAction:
                        var decoded = Action.DecodeCollection(doAction.Data, swfVersion);
                        allActions.AddRange(decoded);
                        break;
                    case PlaceObject2Tag po2 when po2.ClipActions is not null:
                        foreach (var clip in po2.ClipActions)
                        {
                            var clipDecoded = Action.DecodeCollection(clip.Data, swfVersion);
                            allActions.AddRange(clipDecoded);
                        }
                        break;
                    case PlaceObject3Tag po3 when po3.ClipActions is not null:
                        foreach (var clip in po3.ClipActions)
                        {
                            var clipDecoded = Action.DecodeCollection(clip.Data, swfVersion);
                            allActions.AddRange(clipDecoded);
                        }
                        break;
                }
            }

            return allActions;
        }

        private static string? ResolveConstantPoolEntry(IReadOnlyList<Action> actions, int constantIndex)
        {
            foreach (var a in actions)
            {
                if (a is ActionConstantPool pool)
                {
                    if (constantIndex >= 0 && constantIndex < pool.Constants.Count)
                        return pool.Constants[constantIndex];
                    break;
                }
            }
            return null;
        }

        private static string? GetLastStringFromPush(ActionPush push, IReadOnlyList<Action> actions)
        {
            if (push.PushValues.Count == 0)
                return null;
            var last = push.PushValues[^1];
            if (last is PushValue.PushValueString s)
                return s.Value;
            if (last is PushValue.PushValueConstant8 c8)
                return ResolveConstantPoolEntry(actions, c8.ConstantIndex);
            if (last is PushValue.PushValueConstant16 c16)
                return ResolveConstantPoolEntry(actions, c16.ConstantIndex);
            return null;
        }

        private static string? GetStringFromPushValue(PushValue v, IReadOnlyList<Action> actions)
        {
            if (v is PushValue.PushValueString s)
                return s.Value;
            if (v is PushValue.PushValueConstant8 c8)
                return ResolveConstantPoolEntry(actions, c8.ConstantIndex);
            if (v is PushValue.PushValueConstant16 c16)
                return ResolveConstantPoolEntry(actions, c16.ConstantIndex);
            return null;
        }

        private static void ExtractPrototypeMethods(IReadOnlyList<Action> actions, SwfClass cls, HashSet<string> seenNames)
        {
            ActionDefineFunction2? lastAnonymousFunc = null;
            int lastAnonymousFuncIdx = -1;

            for (var i = 0; i < actions.Count; i++)
            {
                if (actions[i] is ActionDefineFunction2 func2 && func2.Name.Length == 0)
                {
                    lastAnonymousFunc = func2;
                    lastAnonymousFuncIdx = i;
                    continue;
                }

                if (actions[i] is ActionDefineFunction func && func.Name.Length == 0)
                {
                    lastAnonymousFunc = UpgradeFunction(func);
                    lastAnonymousFuncIdx = i;
                    continue;
                }

                if (actions[i] is ActionSetMember && lastAnonymousFunc is not null)
                {
                    if (lastAnonymousFuncIdx > 0 && actions[lastAnonymousFuncIdx - 1] is ActionPush namePush)
                    {
                        var lastName = GetLastStringFromPush(namePush, actions);
                        if (lastName is not null &&
                            IsConstructorRuntimeName(lastName, cls.Name) &&
                            !seenNames.Contains("$ctor"))
                        {
                            seenNames.Add("$ctor");
                            var constructorBody = PrependConstantPool(lastAnonymousFunc.Body, actions, lastAnonymousFuncIdx);
                            cls.Constructor = new ActionDefineFunction2(
                                name: cls.Name,
                                registerCount: lastAnonymousFunc.RegisterCount,
                                flags: lastAnonymousFunc.Flags,
                                parameters: lastAnonymousFunc.Parameters,
                                body: constructorBody ?? lastAnonymousFunc.Body);
                        }
                    }
                    lastAnonymousFunc = null;
                    continue;
                }

                if (actions[i] is ActionSetVariable && lastAnonymousFunc is not null)
                {
                    if (lastAnonymousFuncIdx > 0 && actions[lastAnonymousFuncIdx - 1] is ActionPush namePush)
                    {
                        var lastName = GetLastStringFromPush(namePush, actions);
                        if (lastName is not null &&
                            IsConstructorRuntimeName(lastName, cls.Name) &&
                            !seenNames.Contains("$ctor"))
                        {
                            seenNames.Add("$ctor");
                            var constructorBody = PrependConstantPool(lastAnonymousFunc.Body, actions, lastAnonymousFuncIdx);
                            cls.Constructor = new ActionDefineFunction2(
                                name: cls.Name,
                                registerCount: lastAnonymousFunc.RegisterCount,
                                flags: lastAnonymousFunc.Flags,
                                parameters: lastAnonymousFunc.Parameters,
                                body: constructorBody ?? lastAnonymousFunc.Body);
                        }
                    }
                    lastAnonymousFunc = null;
                    continue;
                }

                if (actions[i] is not (ActionPush or ActionPushDuplicate or ActionStoreRegister or
                    ActionGetMember or ActionGetVariable or ActionPop or ActionNot or ActionIf or
                    ActionJump or ActionNewObject or ActionCallMethod or ActionExtends))
                    lastAnonymousFunc = null;
            }

            for (var i = 0; i < actions.Count - 4; i++)
            {
                if (actions[i] is not ActionPush protoPush || protoPush.PushValues.Count < 2)
                    continue;

                var hasRegister = false;
                var hasPrototype = false;
                foreach (var pv in protoPush.PushValues)
                {
                    if (pv is PushValue.PushValueRegister)
                        hasRegister = true;
                    if (GetStringFromPushValue(pv, actions) == "prototype")
                        hasPrototype = true;
                }

                if (!hasRegister || !hasPrototype)
                    continue;

                if (i + 1 >= actions.Count || actions[i + 1] is not ActionGetMember)
                    continue;

                int m = i + 2;
                while (m < actions.Count && actions[m] is ActionStoreRegister or ActionPushDuplicate or ActionPop)
                    m++;

                if (m + 2 < actions.Count &&
                    actions[m] is ActionPush namePush && namePush.PushValues.Count >= 1)
                {
                    var methodNameStr = GetStringFromPushValue(namePush.PushValues[^1], actions);
                    if (methodNameStr is not null &&
                        IsAnonymousFunc(actions[m + 1], out var methodFunc) &&
                        actions[m + 2] is ActionSetMember &&
                        !seenNames.Contains(methodNameStr))
                    {
                        seenNames.Add(methodNameStr);
                        var namedFunc = methodFunc!;
                        var namedBody = PrependConstantPool(namedFunc.Body, actions, m + 1);
                        cls.Methods.Add(new ActionDefineFunction2(
                            name: methodNameStr,
                            registerCount: namedFunc.RegisterCount,
                            flags: namedFunc.Flags,
                            parameters: namedFunc.Parameters,
                            body: namedBody ?? namedFunc.Body));
                    }
                }
            }

            for (var i = 0; i < actions.Count - 3; i++)
            {
                if (IsAnonymousFunc(actions[i], out _) && i + 3 < actions.Count &&
                    actions[i + 1] is ActionPush midPush && midPush.PushValues.Count >= 1)
                {
                    var methodName2 = GetStringFromPushValue(midPush.PushValues[^1], actions);
                    if (methodName2 is not null &&
                        IsAnonymousFunc(actions[i + 2], out var methodFunc2) &&
                        actions[i + 3] is ActionSetMember &&
                        !seenNames.Contains(methodName2))
                    {
                        seenNames.Add(methodName2);
                        var namedBody2 = PrependConstantPool(methodFunc2!.Body, actions, i + 2);
                        cls.Methods.Add(new ActionDefineFunction2(
                            name: methodName2,
                            registerCount: methodFunc2.RegisterCount,
                            flags: methodFunc2.Flags,
                            parameters: methodFunc2.Parameters,
                            body: namedBody2 ?? methodFunc2.Body));
                    }
                }
            }

            for (var i = 0; i < actions.Count - 5; i++)
            {
                if (IsAnonymousFunc(actions[i], out _) && i + 2 < actions.Count &&
                    actions[i + 1] is ActionPush protoPush2 &&
                    HasStringValue(protoPush2, "prototype", actions) &&
                    actions[i + 2] is ActionGetMember)
                {
                    int m2 = i + 3;
                    while (m2 < actions.Count && actions[m2] is ActionStoreRegister or ActionPushDuplicate or ActionPop)
                        m2++;

                    if (m2 + 2 < actions.Count &&
                        actions[m2] is ActionPush namePush3 && namePush3.PushValues.Count >= 1)
                    {
                        var methodName3 = GetStringFromPushValue(namePush3.PushValues[^1], actions);
                        if (methodName3 is not null &&
                            IsAnonymousFunc(actions[m2 + 1], out var methodFunc3) &&
                            actions[m2 + 2] is ActionSetMember &&
                            !seenNames.Contains(methodName3))
                        {
                            seenNames.Add(methodName3);
                            var namedBody3 = PrependConstantPool(methodFunc3!.Body, actions, m2 + 1);
                            cls.Methods.Add(new ActionDefineFunction2(
                                name: methodName3,
                                registerCount: methodFunc3.RegisterCount,
                                flags: methodFunc3.Flags,
                                parameters: methodFunc3.Parameters,
                                body: namedBody3 ?? methodFunc3.Body));
                        }
                    }
                }
            }
        }

        private static int FindActionIndex(IReadOnlyList<Action> actions, Action action)
        {
            for (var i = 0; i < actions.Count; i++)
                if (ReferenceEquals(actions[i], action))
                    return i;

            return actions.Count - 1;
        }

        private static ReadOnlyMemory<byte>? PrependConstantPool(
            ReadOnlyMemory<byte> body,
            IReadOnlyList<Action> outerActions,
            int functionActionIndex)
        {
            if (body.Length == 0)
                return null;

            var bodyActions = Action.DecodeCollection(body, 0);
            if (bodyActions.Count > 0 && bodyActions[0] is ActionConstantPool)
                return null;

            byte version = 0;
            var pool = FindActiveConstantPool(outerActions, functionActionIndex);

            if (pool is null)
                return null;

            foreach (var a in outerActions)
            {
                if (a is ActionDefineFunction2) { version = 9; break; }
                if (a is ActionDefineFunction) { version = 7; break; }
            }

            var poolAction = new ActionConstantPool(pool);
            var actions = new List<Action> { poolAction };
            actions.AddRange(bodyActions);
            return Action.EncodeCollection(actions, version > 0 ? version : (byte)9);
        }

        private static bool IsAnonymousFunc(Action a, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ActionDefineFunction2? wrapped)
        {
            wrapped = null;
            if (a is ActionDefineFunction2 f2 && f2.Name.Length == 0)
            {
                wrapped = f2;
                return true;
            }
            if (a is ActionDefineFunction f && f.Name.Length == 0)
            {
                wrapped = UpgradeFunction(f);
                return true;
            }
            return false;
        }

        private static bool HasStringValue(ActionPush push, string match, IReadOnlyList<Action> actions)
        {
            foreach (var pv in push.PushValues)
            {
                var s = GetStringFromPushValue(pv, actions);
                if (s == match)
                    return true;
            }
            return false;
        }

        private static void ExtractPrototypeMethodsFromConstructor(SwfClass cls, byte swfVersion)
        {
            if (cls.Constructor?.Body.Length is null or 0)
                return;

            var bodyActions = Action.DecodeCollection(cls.Constructor.Body, swfVersion);
            var seenNames = new HashSet<string>(cls.Methods.Select(m => m.Name));

            ExtractPrototypeMethods(bodyActions, cls, seenNames);

            if (cls.Constructor is not null && cls.Constructor.Body.Length > 0)
            {
                var ctorActions = Action.DecodeCollection(cls.Constructor.Body, swfVersion);

                if (ctorActions.Count == 0 || ctorActions[0] is not ActionConstantPool)
                {
                    IReadOnlyList<string>? outerPool = null;
                    foreach (var a in bodyActions)
                    {
                        if (a is ActionConstantPool pool)
                        {
                            outerPool = pool.Constants;
                            break;
                        }
                    }

                    if (outerPool is not null)
                    {
                        var poolAction = new ActionConstantPool(outerPool);
                        var newActions = new List<Action> { poolAction };
                        newActions.AddRange(ctorActions);
                        var newBody = EncodeActions(newActions, swfVersion);
                        if (newBody is not null)
                        {
                            cls.Constructor = new ActionDefineFunction2(
                                name: cls.Name,
                                registerCount: cls.Constructor.RegisterCount,
                                flags: cls.Constructor.Flags,
                                parameters: cls.Constructor.Parameters,
                                body: newBody.Value);
                        }
                    }
                }
            }
        }

        private static ReadOnlyMemory<byte>? EncodeActions(IReadOnlyList<Action> actions, byte swfVersion)
        {
            try
            {
                return Action.EncodeCollection(actions, swfVersion);
            }
            catch
            {
                return null;
            }
        }

        private static void ExtractFields(SwfClass cls, byte swfVersion)
        {
            var seen = new HashSet<string>(
                cls.Fields.Select(field => field.Name),
                StringComparer.Ordinal);
            var seenStatic = new HashSet<string>(
                cls.StaticFields.Select(field => field.Name),
                StringComparer.Ordinal);
            var methodNames = new HashSet<string>(
                cls.Methods.Select(method => method.Name),
                StringComparer.Ordinal);

            void ScanActions(
                IReadOnlyList<Action> actions,
                FunctionContext? ctx = null,
                bool isClassInitializer = false)
            {
                ScanStackForFields(
                    actions,
                    cls,
                    seen,
                    seenStatic,
                    methodNames,
                    ctx,
                    isClassInitializer);

                foreach (var action in actions)
                {
                    if (action is ActionDefineFunction2 func && func.Body.Length > 0)
                    {
                        var bodyDecoded = Action.DecodeCollection(func.Body, swfVersion);
                        ScanActions(bodyDecoded, new FunctionContext(func.Flags, func.Parameters));
                    }

                    if (action is ActionDefineFunction f && f.Body.Length > 0)
                    {
                        var bodyDecoded = Action.DecodeCollection(f.Body, swfVersion);
                        ScanActions(bodyDecoded);
                    }
                }
            }

            if (cls.Constructor?.Body.Length > 0)
            {
                var bodyDecoded = Action.DecodeCollection(cls.Constructor.Body, swfVersion);
                ScanActions(bodyDecoded, new FunctionContext(cls.Constructor.Flags, cls.Constructor.Parameters));
            }

            if (cls.InitializerBody is { } initializer && initializer.Length > 0)
            {
                var initDecoded = Action.DecodeCollection(initializer, swfVersion);
                ScanActions(initDecoded, isClassInitializer: true);
            }

            foreach (var method in cls.Methods)
            {
                if (method.Body.Length is 0)
                    continue;

                var bodyActions = Action.DecodeCollection(method.Body, swfVersion);
                ScanActions(bodyActions, new FunctionContext(method.Flags, method.Parameters));
            }
        }

        private static void ScanStackForFields(
            IReadOnlyList<Action> actions,
            SwfClass cls,
            HashSet<string> seen,
            HashSet<string> seenStatic,
            HashSet<string> methodNames,
            FunctionContext? ctx = null,
            bool isClassInitializer = false)
        {
            var stack = new List<RecoveredFieldValue>();
            var registers = new Dictionary<int, RecoveredFieldValue>();
            if (ctx is not null)
            {
                var reg = 1;
                if (ctx.PreloadThis)
                    registers[reg++] = RecoveredFieldValue.Name("this");
                if (ctx.PreloadArguments)
                    registers[reg++] = RecoveredFieldValue.Name("arguments");
                if (ctx.PreloadSuper)
                    registers[reg++] = RecoveredFieldValue.Name("super");
                if (ctx.PreloadRoot)
                    registers[reg++] = RecoveredFieldValue.Name("_root");
                if (ctx.PreloadParent)
                    registers[reg++] = RecoveredFieldValue.Name("_parent");
                if (ctx.PreloadGlobal)
                    registers[reg++] = RecoveredFieldValue.Name("_global");

                foreach (var p in ctx.Parameters)
                    if (p.Register > 0 && p.Name.Length > 0)
                        registers[p.Register] = RecoveredFieldValue.Name(p.Name);
            }

            var classNames = new HashSet<string>(StringComparer.Ordinal)
            {
                cls.Name,
                ShortName(cls.Name),
                "__Packages." + ShortName(cls.Name)
            };

            for (var i = 0; i < actions.Count; i++)
            {
                switch (actions[i])
                {
                    case ActionConstantPool:
                        break;

                    case ActionPush push:
                        foreach (var v in push.PushValues)
                        {
                            if (v is PushValue.PushValueRegister reg)
                            {
                                stack.Add(registers.GetValueOrDefault(
                                    reg.RegisterIndex,
                                    RecoveredFieldValue.Name($"_loc{reg.RegisterIndex}_")));
                            }
                            else
                            {
                                stack.Add(GetFieldValue(v, actions));
                            }
                        }
                        break;

                    case ActionGetVariable:
                        {
                            var name = GetIdentifier(PopFieldValue(stack));
                            stack.Add(RecoveredFieldValue.Name(name));
                            break;
                        }

                    case ActionGetMember:
                        {
                            var name = GetIdentifier(PopFieldValue(stack));
                            var target = PopFieldValue(stack);
                            if (IsNamedValue(target, "this") && IsFieldName(name))
                                AddOrUpdateField(cls.Fields, seen, name);
                            stack.Add(RecoveredFieldValue.Member(target, name));
                            break;
                        }

                    case ActionSetMember:
                        {
                            var value = PopFieldValue(stack);
                            var name = GetIdentifier(PopFieldValue(stack));
                            var target = PopFieldValue(stack);

                            if (IsNamedValue(target, "this") && IsFieldName(name))
                                AddOrUpdateField(cls.Fields, seen, name);
                            else if (IsPrototypeTarget(target, classNames) &&
                                     IsFieldName(name) &&
                                     !IsAnonymousFunction(value))
                            {
                                AddOrUpdateField(cls.Fields, seen, name, value);
                            }
                            else if (TryGetReferenceName(target, out var targetName) &&
                                     classNames.Contains(targetName) &&
                                     IsIdentifier(name))
                            {
                                if (IsAnonymousFunction(value) && methodNames.Contains(name))
                                {
                                    cls.StaticMethodNames.Add(name);
                                }
                                else if (seenStatic.Add(name))
                                {
                                    cls.StaticFields.Add(CreateRecoveredField(name, value));
                                }
                            }
                            break;
                        }

                    case ActionSetVariable:
                        {
                            _ = PopFieldValue(stack);
                            _ = PopFieldValue(stack);
                            break;
                        }

                    case ActionStoreRegister store:
                        if (stack.Count > 0)
                        {
                            var value = stack[^1];
                            registers[store.RegisterNumber] = isClassInitializer &&
                                IsAnonymousFunction(value)
                                ? RecoveredFieldValue.Name(ShortName(cls.Name))
                                : value;
                        }
                        break;

                    case ActionPushDuplicate:
                        if (stack.Count > 0)
                            stack.Add(stack[^1]);
                        break;

                    case ActionInitArray:
                        {
                            var count = ParseCount(PopFieldValue(stack));
                            var items = new List<RecoveredFieldValue>();
                            for (var p = 0; p < count; p++)
                                items.Add(PopFieldValue(stack));
                            stack.Add(RecoveredFieldValue.Array(items));
                            break;
                        }

                    case ActionNewObject:
                        {
                            var name = GetIdentifier(PopFieldValue(stack));
                            var arguments = PopArguments(stack);
                            stack.Add(RecoveredFieldValue.New(
                                RecoveredFieldValue.Name(name),
                                arguments));
                            break;
                        }

                    case ActionDefineFunction2 func:
                        stack.Add(RecoveredFieldValue.Function(func.Name));
                        break;

                    case ActionDefineFunction func:
                        stack.Add(RecoveredFieldValue.Function(func.Name));
                        break;

                    case ActionAdd2:
                    case ActionStringAdd:
                    case ActionAdd:
                        {
                            var right = PopFieldValue(stack);
                            var left = PopFieldValue(stack);
                            stack.Add(RecoveredFieldValue.Add(left, right));
                            break;
                        }

                    default:
                        break;
                }
            }
        }

        private static string ShortName(string name)
        {
            name = name.Replace("::", ".", StringComparison.Ordinal);
            var separator = name.LastIndexOf('.');
            return separator >= 0 ? name[(separator + 1)..] : name;
        }

        private static string GetIdentifier(RecoveredFieldValue value) =>
            value.Kind is RecoveredFieldValueKind.String or RecoveredFieldValueKind.Name
                ? value.Text ?? string.Empty
                : FormatRecoveredFieldValue(value);

        private static bool IsIdentifier(string value)
        {
            if (value.Length == 0)
                return false;
            if (!char.IsLetter(value[0]) && value[0] != '_' && value[0] != '$')
                return false;
            for (var i = 1; i < value.Length; i++)
                if (!char.IsLetterOrDigit(value[i]) && value[i] != '_' && value[i] != '$')
                    return false;
            return true;
        }

        private static bool IsFieldName(string value)
        {
            if (!IsIdentifier(value))
                return false;
            if (value.Length > 1 && value[0] == 'c' && int.TryParse(value[1..], out _))
                return false;
            return value is not "_currentframe" and not "_parent" and not "_root" and not "_global";
        }

        private static bool IsPrototypeTarget(
            RecoveredFieldValue target,
            HashSet<string> classNames) =>
            target.Kind is RecoveredFieldValueKind.Member &&
            target.Text is "prototype" &&
            target.Children.Length == 1 &&
            TryGetReferenceName(target.Children[0], out var owner) &&
            classNames.Contains(owner);

        private static bool IsNamedValue(RecoveredFieldValue value, string name) =>
            TryGetReferenceName(value, out var actual) && actual == name;

        private static bool IsAnonymousFunction(RecoveredFieldValue value) =>
            value.Kind is RecoveredFieldValueKind.Function &&
            string.IsNullOrEmpty(value.Text);

        private static bool TryGetReferenceName(
            RecoveredFieldValue value,
            [NotNullWhen(true)] out string? name)
        {
            if (value.Kind is RecoveredFieldValueKind.Name && value.Text is not null)
            {
                name = value.Text;
                return true;
            }

            if (value.Kind is RecoveredFieldValueKind.Member &&
                value.Text is not null &&
                value.Children.Length == 1 &&
                TryGetReferenceName(value.Children[0], out var owner))
            {
                name = owner + "." + value.Text;
                return true;
            }

            name = null;
            return false;
        }

        private static void AddOrUpdateField(
            List<RecoveredField> fields,
            HashSet<string> seen,
            string name,
            RecoveredFieldValue? value = null)
        {
            if (value is null)
            {
                if (seen.Add(name))
                    fields.Add(new RecoveredField(name));
                return;
            }

            for (var i = 0; i < fields.Count; i++)
            {
                if (fields[i].Name == name)
                {
                    var replacement = CreateRecoveredField(name, value);
                    fields[i].LegacyInitializerText = replacement.LegacyInitializerText;
                    fields[i].Initializer = replacement.Initializer;
                    seen.Add(name);
                    return;
                }
            }

            seen.Add(name);
            fields.Add(CreateRecoveredField(name, value));
        }

        private static RecoveredField CreateRecoveredField(
            string name,
            RecoveredFieldValue value) =>
            new(
                name,
                FormatRecoveredFieldValue(value),
                CreateSourceExpression(value));

        private static RecoveredFieldValue PopFieldValue(List<RecoveredFieldValue> stack)
        {
            if (stack.Count == 0)
                return RecoveredFieldValue.Undefined();
            var value = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            return value;
        }

        private static int ParseCount(RecoveredFieldValue value) => value.Kind switch
        {
            RecoveredFieldValueKind.Integer => Math.Max(value.IntegerValue, 0),
            RecoveredFieldValueKind.Number when value.NumberValue is >= 0 and <= int.MaxValue =>
                (int)value.NumberValue,
            _ => 0
        };

        private static List<RecoveredFieldValue> PopArguments(
            List<RecoveredFieldValue> stack)
        {
            var count = ParseCount(PopFieldValue(stack));
            var arguments = new List<RecoveredFieldValue>(count);
            for (var i = 0; i < count; i++)
                arguments.Add(PopFieldValue(stack));
            return arguments;
        }

        private static RecoveredFieldValue GetFieldValue(
            PushValue value,
            IReadOnlyList<Action> actions)
        {
            return value switch
            {
                PushValue.PushValueString s => RecoveredFieldValue.String(s.Value),
                PushValue.PushValueInteger i => RecoveredFieldValue.Integer(i.Value),
                PushValue.PushValueFloat f => RecoveredFieldValue.Number(f.Value),
                PushValue.PushValueDouble d => RecoveredFieldValue.Number(d.Value),
                PushValue.PushValueBoolean b => RecoveredFieldValue.Boolean(b.Value),
                PushValue.PushValueNull => RecoveredFieldValue.Null(),
                PushValue.PushValueUndefined => RecoveredFieldValue.Undefined(),
                PushValue.PushValueConstant8 c => RecoveredFieldValue.String(
                    ResolveConstantPoolEntry(actions, c.ConstantIndex) ?? $"c{c.ConstantIndex}"),
                PushValue.PushValueConstant16 c => RecoveredFieldValue.String(
                    ResolveConstantPoolEntry(actions, c.ConstantIndex) ?? $"c{c.ConstantIndex}"),
                _ => RecoveredFieldValue.Undefined()
            };
        }

        private static Avm1SourceExpressionFragment CreateSourceExpression(
            RecoveredFieldValue value)
        {
            var builder = new Avm1SourceArena.Builder();
            var diagnostics = new List<Avm1SourceDiagnostic>();
            var expression = AddSourceExpression(builder, value, diagnostics);
            return new Avm1SourceExpressionFragment(
                builder.ToArena(),
                expression,
                diagnostics.ToArray());
        }

        private static SourceExpressionIndex AddSourceExpression(
            Avm1SourceArena.Builder builder,
            RecoveredFieldValue value,
            List<Avm1SourceDiagnostic> diagnostics)
        {
            switch (value.Kind)
            {
                case RecoveredFieldValueKind.Undefined:
                    return AddLiteral(builder, Avm1SourceLiteralKind.Undefined);
                case RecoveredFieldValueKind.Null:
                    return AddLiteral(builder, Avm1SourceLiteralKind.Null);
                case RecoveredFieldValueKind.Boolean:
                    return AddLiteral(
                        builder,
                        Avm1SourceLiteralKind.Boolean,
                        booleanValue: value.BooleanValue);
                case RecoveredFieldValueKind.Integer:
                    return AddLiteral(
                        builder,
                        Avm1SourceLiteralKind.Integer,
                        integerValue: value.IntegerValue);
                case RecoveredFieldValueKind.Number:
                    return AddLiteral(
                        builder,
                        Avm1SourceLiteralKind.Number,
                        numberValue: value.NumberValue);
                case RecoveredFieldValueKind.String:
                    return AddLiteral(
                        builder,
                        Avm1SourceLiteralKind.String,
                        stringValue: value.Text ?? string.Empty);
                case RecoveredFieldValueKind.Name:
                    return builder.AddExpression(
                        value.Text?.Contains('.', StringComparison.Ordinal) is true
                            ? Avm1SourceExpressionKind.QualifiedName
                            : Avm1SourceExpressionKind.DynamicName,
                        name: builder.InternString(value.Text ?? "undefined"));
                case RecoveredFieldValueKind.Member:
                    return builder.AddExpression(
                        Avm1SourceExpressionKind.MemberAccess,
                        children:
                        [
                            AddSourceExpression(builder, value.Children[0], diagnostics),
                            AddLiteral(
                                builder,
                                Avm1SourceLiteralKind.String,
                                stringValue: value.Text ?? string.Empty)
                        ]);
                case RecoveredFieldValueKind.Array:
                    return builder.AddExpression(
                        Avm1SourceExpressionKind.ArrayLiteral,
                        children: value.Children
                            .Select(child => AddSourceExpression(builder, child, diagnostics))
                            .ToArray());
                case RecoveredFieldValueKind.New:
                    return builder.AddExpression(
                        Avm1SourceExpressionKind.New,
                        children: value.Children
                            .Select(child => AddSourceExpression(builder, child, diagnostics))
                            .ToArray());
                case RecoveredFieldValueKind.Add:
                    return builder.AddExpression(
                        Avm1SourceExpressionKind.Binary,
                        Avm1SourceOperator.Add,
                        children:
                        [
                            AddSourceExpression(builder, value.Children[0], diagnostics),
                            AddSourceExpression(builder, value.Children[1], diagnostics)
                        ]);
                case RecoveredFieldValueKind.Function when !string.IsNullOrEmpty(value.Text):
                    return builder.AddExpression(
                        Avm1SourceExpressionKind.DynamicName,
                        name: builder.InternString(value.Text));
                case RecoveredFieldValueKind.Function:
                    const string anonymousFunctionMessage =
                        "Anonymous field function has no recoverable body.";
                    diagnostics.Add(new Avm1SourceDiagnostic(
                        "AVM1SRC100",
                        Avm1SourceDiagnosticSeverity.Error,
                        SourceOriginIndex.Invalid,
                        anonymousFunctionMessage));
                    var opaque = builder.AddOpaque(
                        Avm1SourceOpaqueKind.FieldExpression,
                        anonymousFunctionMessage,
                        SourceOriginIndex.Invalid);
                    return builder.AddExpression(
                        Avm1SourceExpressionKind.Opaque,
                        name: builder.InternString("anonymous function"),
                        opaque: opaque);
                default:
                    throw new InvalidOperationException(
                        $"Unknown recovered field value kind {value.Kind}.");
            }
        }

        private static SourceExpressionIndex AddLiteral(
            Avm1SourceArena.Builder builder,
            Avm1SourceLiteralKind kind,
            bool booleanValue = false,
            int integerValue = 0,
            double numberValue = 0,
            string? stringValue = null)
        {
            var literal = builder.AddLiteral(
                kind,
                booleanValue,
                integerValue,
                numberValue,
                stringValue is null ? null : builder.InternString(stringValue));
            return builder.AddExpression(
                Avm1SourceExpressionKind.Literal,
                literal: literal);
        }

        private static string FormatRecoveredFieldValue(RecoveredFieldValue value)
        {
            return value.Kind switch
            {
                RecoveredFieldValueKind.Undefined => "undefined",
                RecoveredFieldValueKind.Null => "null",
                RecoveredFieldValueKind.Boolean => value.BooleanValue ? "true" : "false",
                RecoveredFieldValueKind.Integer => value.IntegerValue.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                RecoveredFieldValueKind.Number => value.NumberValue.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                RecoveredFieldValueKind.String => Quote(value.Text ?? string.Empty),
                RecoveredFieldValueKind.Name => value.Text ?? "undefined",
                RecoveredFieldValueKind.Member =>
                    FormatRecoveredFieldValue(value.Children[0]) +
                    (IsIdentifier(value.Text ?? string.Empty)
                        ? "." + value.Text
                        : "[" + value.Text + "]"),
                RecoveredFieldValueKind.Array =>
                    "[" + string.Join(", ", value.Children.Select(FormatRecoveredFieldValue)) + "]",
                RecoveredFieldValueKind.New =>
                    "new " + FormatRecoveredFieldValue(value.Children[0]) + "(" +
                    string.Join(", ", value.Children.Skip(1).Select(FormatRecoveredFieldValue)) + ")",
                RecoveredFieldValueKind.Add =>
                    "(" + FormatRecoveredFieldValue(value.Children[0]) + " + " +
                    FormatRecoveredFieldValue(value.Children[1]) + ")",
                RecoveredFieldValueKind.Function =>
                    string.IsNullOrEmpty(value.Text) ? "function" : value.Text,
                _ => throw new InvalidOperationException(
                    $"Unknown recovered field value kind {value.Kind}.")
            };
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        }
    }

    private readonly record struct SourceProjectionMetrics(
        int IncompleteFileCount,
        int ErrorCount,
        int WarningCount,
        int OpaqueRegionCount);
}

public readonly record struct Avm1ClassDecompilationMetrics(
    int ClassCount,
    int MethodCount,
    TimeSpan ReadDuration,
    TimeSpan AnalysisDuration,
    TimeSpan EmitDuration,
    int UnknownActionCount,
    int StackDiagnosticCount,
    int StackDepthDiagnosticCount,
    IReadOnlyList<Avm1ActionOpcodeCount> UnsupportedActions)
{
    public TimeSpan TotalDuration => ReadDuration + AnalysisDuration + EmitDuration;

    public int IncompleteSourceFileCount { get; init; }

    public int SourceErrorCount { get; init; }

    public int SourceWarningCount { get; init; }

    public int SourceOpaqueRegionCount { get; init; }

    public int SourceScopeConflictCount { get; init; }

    public bool SourceSemanticallyComplete { get; init; }

    // Covers export-owned sequential work and parallel callback bodies.
    public long ManagedBytesAllocated { get; init; }
}

public readonly record struct Avm1ActionOpcodeCount(ActionOpcode Opcode, int Count);
