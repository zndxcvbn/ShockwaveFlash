using System.Security.Cryptography;
using System.Text;
using ShockwaveFlash.Avm1.Compilation.Binding;
using ShockwaveFlash.Avm1.Compilation.Preprocessing;
using ShockwaveFlash.Avm1.Compilation.Syntax;
using ShockwaveFlash.Avm1.Source;

namespace ShockwaveFlash.Avm1.Compilation;

public sealed class Avm1CompilerService
{
    private readonly Avm1Compiler _compiler;
    private readonly Avm1CompilerServiceCacheOptions _cacheOptions;
    private readonly object _cacheGate = new();
    private readonly Dictionary<SyntaxCacheKey, Avm1SyntaxTree> _syntaxCache = [];
    private readonly Queue<SyntaxCacheKey> _syntaxOrder = [];
    private readonly Dictionary<string, FrontEnd> _frontEndCache =
        new(StringComparer.Ordinal);
    private readonly Queue<string> _frontEndOrder = [];
    private readonly Dictionary<string, Avm1ProgramArtifact> _compilationCache =
        new(StringComparer.Ordinal);
    private readonly Queue<string> _compilationOrder = [];

    public Avm1CompilerService(
        Avm1Compiler? compiler = null,
        Avm1CompilerServiceCacheOptions? cacheOptions = null)
    {
        _compiler = compiler ?? new Avm1Compiler();
        _cacheOptions = cacheOptions ?? new Avm1CompilerServiceCacheOptions();
        ValidateCacheOptions(_cacheOptions);
    }

    public Avm1CompilerServiceResult Compile(
        Avm1CompilationSource source,
        Avm1CompilerServiceOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Compile([source], options, cancellationToken);
    }

    public Avm1CompilerServiceResult CompileFiles(
        IEnumerable<string> paths,
        Avm1CompilerServiceOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var sources = paths.Select(path =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            return new Avm1CompilationSource(path, File.ReadAllText(path));
        }).ToArray();
        return Compile(sources, options, cancellationToken);
    }

    public Avm1CompilerServiceResult Compile(
        IEnumerable<Avm1CompilationSource> sources,
        Avm1CompilerServiceOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);
        var inputs = sources.ToArray();
        if (inputs.Any(source => source is null))
        {
            throw new ArgumentException(
                "A compilation cannot contain a null source.",
                nameof(sources));
        }
        cancellationToken.ThrowIfCancellationRequested();

        var parseOptions = options.ParseOptions ?? Avm1ParseOptions.Default;
        var preprocessorOptions = options.PreprocessorOptions ??
            Avm1PreprocessorOptions.Default;
        var referenceCatalog = SnapshotReferences(options.ReferenceProvider);
        var preprocessing = inputs.Select(source => Avm1Preprocessor.Expand(
            source.Path,
            source.Text,
            options.IncludeResolver,
            preprocessorOptions,
            cancellationToken)).ToArray();

        var statistics = new MutableCacheStatistics();
        var frontEndKey = ComputeFrontEndKey(
            preprocessing,
            parseOptions,
            options.TypeCheckingMode,
            referenceCatalog);
        FrontEnd frontEnd;
        if (TryGetFrontEnd(frontEndKey, out frontEnd!))
        {
            statistics.FrontEndHits++;
        }
        else
        {
            statistics.FrontEndMisses++;
            var sourceFiles = new Avm1SyntaxSourceFile[preprocessing.Length];
            for (var i = 0; i < preprocessing.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var expanded = preprocessing[i].Source;
                var tree = GetOrParse(
                    expanded.Text,
                    parseOptions,
                    statistics);
                sourceFiles[i] = new Avm1SyntaxSourceFile(expanded.Path, tree);
            }

            var syntaxProgram = Avm1SyntaxProgram.Create(sourceFiles);
            var lexicalBinding = Avm1SyntaxLexicalBinding.Bind(syntaxProgram);
            var projection = Avm1SyntaxSourceProjector.Project(
                syntaxProgram,
                lexicalBinding,
                referenceCatalog);
            var typeCheck = Avm1SourceTypeChecker.Check(
                projection.SourceProgram,
                options.TypeCheckingMode,
                cancellationToken);
            frontEnd = new FrontEnd(
                syntaxProgram,
                lexicalBinding,
                projection,
                typeCheck);
            AddFrontEnd(frontEndKey, frontEnd);
        }

        var diagnostics = BuildFrontEndDiagnostics(preprocessing, frontEnd);
        Avm1ProgramArtifact? programArtifact = null;
        if (!HasErrors(diagnostics))
        {
            var classOptions = options.CreateClassOptions();
            var compilationKey = ComputeCompilationKey(
                frontEndKey,
                classOptions);
            if (TryGetCompilation(compilationKey, out programArtifact!))
            {
                statistics.CompilationHits++;
            }
            else
            {
                statistics.CompilationMisses++;
                programArtifact = _compiler.CompileProgram(
                    frontEnd.Projection.SourceProgram,
                    classOptions,
                    cancellationToken);
                AddCompilation(compilationKey, programArtifact);
            }
            AddCompilationDiagnostics(
                diagnostics,
                preprocessing,
                programArtifact);
        }

        var debugMap = Avm1CompilerDebugMapBuilder.Build(
            preprocessing,
            programArtifact);

        return new Avm1CompilerServiceResult(
            preprocessing,
            frontEnd.SyntaxProgram,
            frontEnd.LexicalBinding,
            frontEnd.Projection,
            frontEnd.TypeCheck,
            programArtifact,
            debugMap,
            diagnostics.ToArray(),
            statistics.ToImmutable());
    }

    public void ClearCache()
    {
        lock (_cacheGate)
        {
            _syntaxCache.Clear();
            _syntaxOrder.Clear();
            _frontEndCache.Clear();
            _frontEndOrder.Clear();
            _compilationCache.Clear();
            _compilationOrder.Clear();
        }
    }

    private Avm1SyntaxTree GetOrParse(
        string text,
        Avm1ParseOptions options,
        MutableCacheStatistics statistics)
    {
        var key = new SyntaxCacheKey(
            ComputeFingerprint([text]),
            options.Dialect,
            options.Features);
        lock (_cacheGate)
        {
            if (_syntaxCache.TryGetValue(key, out var cached))
            {
                statistics.SyntaxTreeHits++;
                return cached;
            }
        }

        var parsed = Avm1SyntaxTree.Parse(text, options);
        lock (_cacheGate)
        {
            if (_syntaxCache.TryGetValue(key, out var raced))
            {
                statistics.SyntaxTreeHits++;
                return raced;
            }
            AddBounded(
                _syntaxCache,
                _syntaxOrder,
                key,
                parsed,
                _cacheOptions.MaximumSyntaxTrees);
            statistics.SyntaxTreeMisses++;
            return parsed;
        }
    }

    private bool TryGetFrontEnd(string key, out FrontEnd frontEnd)
    {
        lock (_cacheGate)
            return _frontEndCache.TryGetValue(key, out frontEnd!);
    }

    private void AddFrontEnd(string key, FrontEnd frontEnd)
    {
        lock (_cacheGate)
        {
            AddBounded(
                _frontEndCache,
                _frontEndOrder,
                key,
                frontEnd,
                _cacheOptions.MaximumFrontEnds);
        }
    }

    private bool TryGetCompilation(
        string key,
        out Avm1ProgramArtifact artifact)
    {
        lock (_cacheGate)
            return _compilationCache.TryGetValue(key, out artifact!);
    }

    private void AddCompilation(string key, Avm1ProgramArtifact artifact)
    {
        lock (_cacheGate)
        {
            AddBounded(
                _compilationCache,
                _compilationOrder,
                key,
                artifact,
                _cacheOptions.MaximumCompilations);
        }
    }

    private static void AddBounded<TKey, TValue>(
        Dictionary<TKey, TValue> cache,
        Queue<TKey> order,
        TKey key,
        TValue value,
        int capacity)
        where TKey : notnull
    {
        if (cache.ContainsKey(key))
            return;
        while (cache.Count >= capacity && order.TryDequeue(out var oldest))
            cache.Remove(oldest);
        cache.Add(key, value);
        order.Enqueue(key);
    }

    private static List<Avm1CompilerServiceDiagnostic> BuildFrontEndDiagnostics(
        Avm1PreprocessorResult[] preprocessing,
        FrontEnd frontEnd)
    {
        var result = new List<Avm1CompilerServiceDiagnostic>();
        foreach (var preprocessed in preprocessing)
        {
            foreach (var diagnostic in preprocessed.Diagnostics)
            {
                result.Add(new Avm1CompilerServiceDiagnostic(
                    diagnostic.Code,
                    diagnostic.Severity,
                    Avm1CompilerServiceStage.Preprocessing,
                    diagnostic.Location.Path,
                    diagnostic.Location.Span,
                    -1,
                    diagnostic.Message));
            }
        }

        for (var i = 0; i < frontEnd.SyntaxProgram.Sources.Count; i++)
        {
            foreach (var diagnostic in frontEnd.SyntaxProgram.Sources[i]
                         .SyntaxTree.Diagnostics)
            {
                var location = preprocessing[i].Source.MapLocation(diagnostic.Span);
                result.Add(new Avm1CompilerServiceDiagnostic(
                    diagnostic.Code,
                    MapSeverity(diagnostic.Severity),
                    Avm1CompilerServiceStage.Syntax,
                    location.Path,
                    location.Span,
                    -1,
                    diagnostic.Message));
            }
        }

        AddBindingDiagnostics(
            result,
            preprocessing,
            frontEnd.SyntaxProgram.Diagnostics,
            Avm1CompilerServiceStage.DeclarationBinding);
        AddBindingDiagnostics(
            result,
            preprocessing,
            frontEnd.LexicalBinding.Diagnostics,
            Avm1CompilerServiceStage.LexicalBinding);
        foreach (var diagnostic in frontEnd.Projection.Diagnostics)
        {
            var location = MapLocation(
                preprocessing,
                diagnostic.File,
                diagnostic.Span);
            result.Add(new Avm1CompilerServiceDiagnostic(
                diagnostic.Code,
                diagnostic.Severity,
                Avm1CompilerServiceStage.SourceProjection,
                location.Path,
                location.Span,
                -1,
                diagnostic.Message));
        }

        var symbolFiles = BuildSymbolFileMap(frontEnd.Projection.SourceProgram);
        foreach (var diagnostic in frontEnd.TypeCheck.Diagnostics)
        {
            var location = MapTypeDiagnostic(
                preprocessing,
                frontEnd.Projection.SourceProgram,
                symbolFiles,
                diagnostic);
            result.Add(new Avm1CompilerServiceDiagnostic(
                diagnostic.Code,
                MapSeverity(diagnostic.Severity),
                Avm1CompilerServiceStage.TypeChecking,
                location.Path,
                location.Span,
                -1,
                diagnostic.Message));
        }
        return result;
    }

    private static void AddBindingDiagnostics(
        List<Avm1CompilerServiceDiagnostic> destination,
        Avm1PreprocessorResult[] preprocessing,
        IReadOnlyList<Avm1SyntaxBindingDiagnostic> diagnostics,
        Avm1CompilerServiceStage stage)
    {
        foreach (var diagnostic in diagnostics)
        {
            var location = MapLocation(
                preprocessing,
                diagnostic.File,
                diagnostic.Span);
            destination.Add(new Avm1CompilerServiceDiagnostic(
                diagnostic.Code,
                diagnostic.Severity,
                stage,
                location.Path,
                location.Span,
                -1,
                diagnostic.Message));
        }
    }

    private static void AddCompilationDiagnostics(
        List<Avm1CompilerServiceDiagnostic> destination,
        Avm1PreprocessorResult[] preprocessing,
        Avm1ProgramArtifact artifact)
    {
        var classDiagnosticCount = artifact.Classes.Sum(sourceClass =>
            sourceClass.Diagnostics.Count);
        var programDiagnosticCount = artifact.Diagnostics.Count -
            classDiagnosticCount;
        for (var i = 0; i < programDiagnosticCount; i++)
        {
            var diagnostic = artifact.Diagnostics[i];
            destination.Add(new Avm1CompilerServiceDiagnostic(
                diagnostic.Code,
                diagnostic.Severity,
                Avm1CompilerServiceStage.Compilation,
                string.Empty,
                Avm1TextSpan.Invalid,
                diagnostic.ByteOffset,
                diagnostic.Message));
        }

        var sourceFiles = new Dictionary<Avm1SourceClass, int>(
            ReferenceEqualityComparer.Instance);
        for (var i = 0; i < artifact.Source.Files.Count; i++)
        {
            foreach (var sourceClass in artifact.Source.Files[i].Classes)
                sourceFiles.Add(sourceClass, i);
        }

        foreach (var sourceClass in artifact.Classes)
        {
            var file = sourceFiles[sourceClass.Source];
            var path = file < preprocessing.Length
                ? preprocessing[file].Source.Path
                : string.Empty;
            foreach (var diagnostic in sourceClass.Diagnostics)
            {
                destination.Add(new Avm1CompilerServiceDiagnostic(
                    diagnostic.Code,
                    diagnostic.Severity,
                    Avm1CompilerServiceStage.Compilation,
                    path,
                    Avm1TextSpan.Invalid,
                    diagnostic.ByteOffset,
                    diagnostic.Message));
            }
        }
    }

    private static Dictionary<int, int> BuildSymbolFileMap(
        Avm1SourceProgram program)
    {
        var result = new Dictionary<int, int>();
        for (var file = 0; file < program.Files.Count; file++)
        {
            foreach (var sourceClass in program.Files[file].Classes)
            {
                if (program.TryGetSymbol(sourceClass, out var symbol))
                    result[symbol.Value] = file;
                foreach (var member in sourceClass.Members)
                {
                    if (program.TryGetSymbol(member, out var memberSymbol))
                        result[memberSymbol.Value] = file;
                }
            }
        }
        return result;
    }

    private static Avm1SourceLocation MapTypeDiagnostic(
        Avm1PreprocessorResult[] preprocessing,
        Avm1SourceProgram program,
        Dictionary<int, int> symbolFiles,
        Avm1SourceTypeCheckDiagnostic diagnostic)
    {
        if (diagnostic.Arena is { } arena &&
            diagnostic.Origin.IsValid &&
            diagnostic.Origin.Value < arena.Origins.Count)
        {
            var origin = arena[diagnostic.Origin];
            if (origin.RecoveryUnit >= 0 &&
                origin.RecoveryUnit < preprocessing.Length &&
                origin.StartOffset >= 0 &&
                origin.EndOffset >= origin.StartOffset)
            {
                return preprocessing[origin.RecoveryUnit].Source.MapLocation(
                    Avm1TextSpan.FromBounds(
                        origin.StartOffset,
                        origin.EndOffset));
            }
        }

        var symbol = diagnostic.Symbol;
        if (symbol.IsValid && symbol.Value < program.Symbols.Count &&
            symbolFiles.TryGetValue(symbol.Value, out var file))
        {
            return new Avm1SourceLocation(
                preprocessing[file].Source.Path,
                Avm1TextSpan.Invalid);
        }
        return new Avm1SourceLocation(string.Empty, Avm1TextSpan.Invalid);
    }

    private static Avm1SourceLocation MapLocation(
        Avm1PreprocessorResult[] preprocessing,
        Avm1SyntaxFileIndex file,
        Avm1TextSpan span) =>
        file.IsValid && file.Value < preprocessing.Length
            ? preprocessing[file.Value].Source.MapLocation(span)
            : new Avm1SourceLocation(string.Empty, span);

    private static Avm1CompilationDiagnosticSeverity MapSeverity(
        Avm1SyntaxDiagnosticSeverity severity) => severity switch
        {
            Avm1SyntaxDiagnosticSeverity.Info =>
                Avm1CompilationDiagnosticSeverity.Info,
            Avm1SyntaxDiagnosticSeverity.Warning =>
                Avm1CompilationDiagnosticSeverity.Warning,
            Avm1SyntaxDiagnosticSeverity.Error =>
                Avm1CompilationDiagnosticSeverity.Error,
            _ => throw new ArgumentOutOfRangeException(nameof(severity))
        };

    private static Avm1CompilationDiagnosticSeverity MapSeverity(
        Avm1SourceDiagnosticSeverity severity) => severity switch
        {
            Avm1SourceDiagnosticSeverity.Info =>
                Avm1CompilationDiagnosticSeverity.Info,
            Avm1SourceDiagnosticSeverity.Warning =>
                Avm1CompilationDiagnosticSeverity.Warning,
            Avm1SourceDiagnosticSeverity.Error =>
                Avm1CompilationDiagnosticSeverity.Error,
            _ => throw new ArgumentOutOfRangeException(nameof(severity))
        };

    private static bool HasErrors(
        IReadOnlyList<Avm1CompilerServiceDiagnostic> diagnostics) =>
        diagnostics.Any(diagnostic =>
            diagnostic.Severity is Avm1CompilationDiagnosticSeverity.Error);

    private static Avm1ReferenceCatalog? SnapshotReferences(
        IAvm1ReferenceProvider? provider)
    {
        if (provider is null)
            return null;
        var classes = provider.GetClasses() ?? throw new InvalidOperationException(
            "The AVM1 reference provider returned a null class sequence.");
        return new Avm1ReferenceCatalog(classes);
    }

    private static string ComputeFrontEndKey(
        Avm1PreprocessorResult[] preprocessing,
        Avm1ParseOptions parseOptions,
        Avm1SourceTypeCheckingMode typeCheckingMode,
        Avm1ReferenceCatalog? references)
    {
        var parts = new List<string>(preprocessing.Length * 2 + 8)
        {
            "frontend-v1",
            ((int)parseOptions.Dialect).ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ((int)parseOptions.Features).ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ((int)typeCheckingMode).ToString(
                System.Globalization.CultureInfo.InvariantCulture)
        };
        foreach (var item in preprocessing)
        {
            parts.Add(item.Source.Path);
            parts.Add(item.Source.Text);
        }
        AddReferenceFingerprintParts(parts, references);
        return ComputeFingerprint(parts);
    }

    private static string ComputeCompilationKey(
        string frontEndKey,
        Avm1ClassCompilationOptions options) =>
        ComputeFingerprint([
            "compilation-v2",
            frontEndKey,
            options.SwfVersion.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ((int)options.AbiProfile).ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ((int)options.OptimizationLevel).ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            options.EmitPropertyFlags ? "1" : "0",
            options.EmitRecoveredLinkage ? "1" : "0",
            options.MaxDegreeOfParallelism.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
        ]);

    private static void AddReferenceFingerprintParts(
        List<string> parts,
        Avm1ReferenceCatalog? references)
    {
        parts.Add("references-v1");
        if (references is null)
            return;
        foreach (var sourceClass in references.Classes.OrderBy(
                     sourceClass => sourceClass.Name.Value,
                     StringComparer.Ordinal))
        {
            parts.Add(sourceClass.Name.Value);
            parts.Add(((int)sourceClass.Kind).ToString(
                System.Globalization.CultureInfo.InvariantCulture));
            parts.Add(((int)sourceClass.Modifiers).ToString(
                System.Globalization.CultureInfo.InvariantCulture));
            parts.Add(sourceClass.BaseType?.Name.Value ?? string.Empty);
            foreach (var interfaceType in sourceClass.Interfaces
                         .OrderBy(type => type.Name.Value, StringComparer.Ordinal))
            {
                parts.Add("i:" + interfaceType.Name.Value);
            }
            foreach (var member in sourceClass.Members.OrderBy(
                         member => member.Name,
                         StringComparer.Ordinal).ThenBy(member => member.Kind))
            {
                parts.Add("m:" + member.Name);
                parts.Add(((int)member.Kind).ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
                parts.Add(((int)member.Modifiers).ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
                parts.Add(member.Type?.Name.Value ?? string.Empty);
                foreach (var parameter in member.Parameters)
                {
                    parts.Add("p:" + parameter.Name);
                    parts.Add(parameter.Type?.Name.Value ?? string.Empty);
                    parts.Add(((int)parameter.Flags).ToString(
                        System.Globalization.CultureInfo.InvariantCulture));
                }
            }
        }
    }

    private static string ComputeFingerprint(IEnumerable<string> parts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var part in parts)
        {
            var bytes = Encoding.UTF8.GetBytes(part);
            hash.AppendData(BitConverter.GetBytes(bytes.Length));
            hash.AppendData(bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void ValidateCacheOptions(
        Avm1CompilerServiceCacheOptions options)
    {
        if (options.MaximumSyntaxTrees <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The syntax-tree cache capacity must be positive.");
        if (options.MaximumFrontEnds <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The front-end cache capacity must be positive.");
        if (options.MaximumCompilations <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The compilation cache capacity must be positive.");
    }

    private readonly record struct SyntaxCacheKey(
        string TextFingerprint,
        Avm1LanguageDialect Dialect,
        Avm1SyntaxFeatures Features);

    private sealed record FrontEnd(
        Avm1SyntaxProgram SyntaxProgram,
        Avm1SyntaxLexicalBinding LexicalBinding,
        Avm1SyntaxSourceProjection Projection,
        Avm1SourceTypeCheckResult TypeCheck);

    private sealed class MutableCacheStatistics
    {
        public int SyntaxTreeHits { get; set; }
        public int SyntaxTreeMisses { get; set; }
        public int FrontEndHits { get; set; }
        public int FrontEndMisses { get; set; }
        public int CompilationHits { get; set; }
        public int CompilationMisses { get; set; }

        public Avm1CompilerServiceCacheStatistics ToImmutable() =>
            new(
                SyntaxTreeHits,
                SyntaxTreeMisses,
                FrontEndHits,
                FrontEndMisses,
                CompilationHits,
                CompilationMisses);
    }
}
