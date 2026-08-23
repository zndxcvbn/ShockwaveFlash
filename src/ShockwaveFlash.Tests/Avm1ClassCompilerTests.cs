using ShockwaveFlash.Avm1;
using ShockwaveFlash.Avm1.Compilation;
using ShockwaveFlash.Avm1.Decompilation;
using ShockwaveFlash.Avm1.Decompilation.Analysis;
using ShockwaveFlash.Avm1.Source;
using ShockwaveFlash.Avm1.Special;
using ShockwaveFlash.Avm1.Swf4;
using ShockwaveFlash.Avm1.Swf5;
using ShockwaveFlash.Avm1.Swf7;
using ShockwaveFlash.Avm1.Text;
using ShockwaveFlash.Avm1.Types;
using ShockwaveFlash.Tags;
using ShockwaveFlash.Tags.Action;
using ShockwaveFlash.Tags.Control;
using ShockwaveFlash.Tags.DisplayList;
using ShockwaveFlash.Tags.Metadata;
using ShockwaveFlash.Tags.Sprite;
using ShockwaveFlash.Types;
using ShockwaveFlash.Types.Control;
using Shouldly;

namespace ShockwaveFlash.Tests;

public sealed class Avm1ClassCompilerTests
{
    [Fact]
    public void Canonical_class_lowering_emits_a_verified_complete_initializer()
    {
        var constructor = CreateMethod(
            "Widget",
            Avm1SourceMethodKind.Constructor,
            returnValue: null,
            parameterNames: ["value"]);
        var instanceMethod = CreateMethod(
            "measure",
            Avm1SourceMethodKind.Method,
            returnValue: 42);
        var getter = CreateMethod(
            "status",
            Avm1SourceMethodKind.Getter,
            returnValue: 7,
            runtimeName: "__get__status");
        var setter = CreateMethod(
            "status",
            Avm1SourceMethodKind.Setter,
            returnValue: null,
            parameterNames: ["value"],
            runtimeName: "__set__status");
        var sourceClass = new Avm1SourceClass(
            new Avm1SourceQualifiedName("demo.widgets.Widget"),
            new Avm1SourceQualifiedName("demo.BaseWidget"),
            [
                new Avm1SourceQualifiedName("demo.IFirst"),
                new Avm1SourceQualifiedName("ISecond")
            ],
            [
                CreateConditionalField("enabled"),
                CreateLiteralField(
                    "version",
                    3,
                    Avm1SourceDeclarationModifiers.Static),
                constructor,
                instanceMethod,
                getter,
                setter
            ]);

        var artifact = new Avm1Compiler().CompileClass(
            sourceClass,
            new Avm1ClassCompilationOptions(7));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Methods.Count.ShouldBe(4);
        artifact.Methods.ShouldAllBe(method => method.Method.Succeeded);
        artifact.InitializerBody.ShouldNotBeNull().Actions[^1]
            .ShouldBeOfType<ActionEnd>();
        artifact.InitializerBody.Actions.OfType<ActionDefineFunction2>()
            .Count().ShouldBe(4);
        artifact.InitializerBody.Actions.OfType<ActionExtends>()
            .ShouldHaveSingleItem();
        artifact.InitializerBody.Actions.OfType<ActionImplementsOp>()
            .ShouldHaveSingleItem();
        artifact.InitializerBody.Actions.OfType<ActionCallMethod>()
            .ShouldHaveSingleItem();
        artifact.InitializerBody.Actions.OfType<ActionCallFunction>()
            .Count().ShouldBe(2);
        artifact.InitializerBody.Actions.OfType<ActionIf>()
            .Count().ShouldBeGreaterThanOrEqualTo(3);

        var metadata = Avm1ClassMetadataAnalysis.Build(
            Avm1Decompiler.BuildMethodCore(artifact.Bytecode, swfVersion: 7),
            sourceClass.Name.Value);
        metadata.BaseClassName.ShouldBe("demo.BaseWidget");
        metadata.InterfaceNames.ShouldBe(["demo.IFirst", "ISecond"]);

        var constructorAction = artifact.InitializerBody.Actions
            .OfType<ActionDefineFunction2>()
            .First();
        constructorAction.Parameters.ShouldHaveSingleItem()
            .ShouldBe(new ShockwaveFlash.Avm1.Types.FunctionParameter(0, "value"));
        ShockwaveFlash.Avm1.Action.DecodeCollection(
                constructorAction.Body,
                swfVersion: 7,
                strict: true)
            .ShouldNotBeNull();
    }

    [Fact]
    public void Class_source_map_rebases_method_ranges_into_initializer()
    {
        var method = CreateMappedMethod("readValue", 42, out var literalOrigin);
        var sourceClass = new Avm1SourceClass(
            new Avm1SourceQualifiedName("demo.MappedClass"),
            baseType: null,
            interfaces: null,
            members: [method]);

        var artifact = new Avm1Compiler().CompileClass(
            sourceClass,
            new Avm1ClassCompilationOptions(7));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var methodBody = artifact.SourceMap.MethodBodies.ShouldHaveSingleItem();
        methodBody.MethodIndex.ShouldBe(0);
        methodBody.Kind.ShouldBe(Avm1CompiledClassMethodBodyKind.Compiled);
        methodBody.IsMapped.ShouldBeTrue();
        var initializer = artifact.InitializerBody.ShouldNotBeNull();
        var function = initializer.Actions[methodBody.InitializerActionIndex]
            .ShouldBeOfType<ActionDefineFunction2>();
        var functionLocation = initializer.ActionLocations[
            methodBody.InitializerActionIndex];
        methodBody.ByteOffset.ShouldBe(
            functionLocation.ByteOffset +
            functionLocation.ByteLength -
            function.Body.Length);
        methodBody.ByteLength.ShouldBe(function.Body.Length);

        var methodMap = artifact.Methods.ShouldHaveSingleItem().Method.SourceMap;
        artifact.SourceMap.Entries.Count.ShouldBe(methodMap.Entries.Count);
        foreach (var entry in artifact.SourceMap.Entries)
        {
            entry.MethodIndex.ShouldBe(0);
            entry.ByteOffset.ShouldBe(
                methodBody.ByteOffset + entry.MethodEntry.ByteOffset);
            initializer.Bytes.Span.Slice(entry.ByteOffset, entry.ByteLength)
                .SequenceEqual(function.Body.Span.Slice(
                    entry.MethodEntry.ByteOffset,
                    entry.ByteLength))
                .ShouldBeTrue();
        }

        var literalEntry = artifact.SourceMap.GetEntries(0, literalOrigin)
            .ShouldHaveSingleItem();
        artifact.SourceMap.TryGetEntry(literalEntry.ByteOffset, out var resolved)
            .ShouldBeTrue();
        resolved.ShouldBe(literalEntry);
        artifact.SourceMap.TryGetOrigin(
                literalEntry.ByteOffset,
                out var resolvedOrigin)
            .ShouldBeTrue();
        resolvedOrigin.ShouldBe(method.Body.Arena[literalOrigin]);
    }

    [Fact]
    public void Origin_reuse_does_not_map_a_nonidentical_method_body()
    {
        var method = CreateMappedMethod("readValue", 7, out _);
        var className = new Avm1SourceQualifiedName("demo.OriginMappedClass");
        var canonicalClass = new Avm1SourceClass(
            className,
            baseType: null,
            interfaces: null,
            members: [method]);
        var compiler = new Avm1Compiler();
        var canonical = compiler.CompileClass(
            canonicalClass,
            new Avm1ClassCompilationOptions(7));
        canonical.Succeeded.ShouldBeTrue(Describe(canonical));

        var canonicalPlacement = canonical.SourceMap.MethodBodies
            .ShouldHaveSingleItem();
        var originalFunction = canonical.InitializerBody.ShouldNotBeNull()
            .Actions[canonicalPlacement.InitializerActionIndex]
            .ShouldBeOfType<ActionDefineFunction2>();
        var alternateBody = ShockwaveFlash.Avm1.Action.EncodeCollection(
            [
                new ActionPush([PushValue.Float(7)]),
                new ActionReturn()
            ],
            swfVersion: 7);
        alternateBody.Length.ShouldBe(originalFunction.Body.Length);
        alternateBody.Span.SequenceEqual(originalFunction.Body.Span)
            .ShouldBeFalse();

        var initializerActions = canonical.InitializerBody.Actions.ToArray();
        initializerActions[canonicalPlacement.InitializerActionIndex] =
            new ActionDefineFunction2(
                originalFunction.Name,
                originalFunction.RegisterCount,
                originalFunction.Flags,
                originalFunction.Parameters,
                alternateBody);
        var originInitializer = ShockwaveFlash.Avm1.Action.EncodeCollection(
            initializerActions,
            swfVersion: 7);
        var methodOrigin = new Avm1SourceMethodBytecodeOrigin(
            swfVersion: 7,
            canonicalPlacement.InitializerActionIndex,
            alternateBody,
            originalFunction.RegisterCount,
            (ushort)originalFunction.Flags,
            originalFunction.Parameters.Select(parameter =>
                new Avm1SourceFunctionParameterOrigin(
                    parameter.Register,
                    parameter.Name)),
            initialConstantPool: null,
            Avm1SourceFingerprint.ComputeMethod(method.Body));
        var originMethod = new Avm1SourceMethodDeclaration(
            method.Name,
            method.Kind,
            method.Body,
            method.Modifiers,
            method.DeclaredReturnType,
            method.InferredReturnType,
            new Avm1SourceMemberOrigin(method.Origin.RuntimeName, methodOrigin),
            method.HasBody);
        var originTemplate = new Avm1SourceClass(
            className,
            baseType: null,
            interfaces: null,
            members: [originMethod],
            origin: new Avm1SourceClassOrigin(
                SpriteId: 17,
                RuntimeName: className.Value,
                HasInitializer: true));
        var sourceClass = new Avm1SourceClass(
            className,
            baseType: null,
            interfaces: null,
            members: [originMethod],
            origin: originTemplate.Origin with
            {
                Bytecode = new Avm1SourceClassBytecodeOrigin(
                    swfVersion: 7,
                    originInitializer,
                    Avm1SourceFingerprint.ComputeClass(originTemplate),
                    Avm1SourceFingerprint.ComputeClassShape(originTemplate))
            });

        var artifact = compiler.CompileClass(
            sourceClass,
            new Avm1ClassCompilationOptions(7)
            {
                AbiProfile = Avm1ClassAbiProfile.OriginPreserving
            });

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.OriginReuseKind.ShouldBe(
            Avm1OriginReuseKind.CompleteInitializer);
        artifact.Bytecode.ToArray().ShouldBe(originInitializer.ToArray());
        artifact.Methods.ShouldHaveSingleItem().Method.SourceMap.Entries
            .ShouldNotBeEmpty();
        var reused = artifact.SourceMap.MethodBodies.ShouldHaveSingleItem();
        reused.Kind.ShouldBe(Avm1CompiledClassMethodBodyKind.ReusedUnmapped);
        reused.IsMapped.ShouldBeFalse();
        artifact.SourceMap.Entries.ShouldBeEmpty();
        artifact.SourceMap.TryGetEntry(reused.ByteOffset, out _).ShouldBeFalse();
    }

    [Fact]
    public void Origin_reuse_marks_a_method_without_action_origin_unplaced()
    {
        var method = CreateMappedMethod("readValue", 11, out _);
        var className = new Avm1SourceQualifiedName("demo.UnplacedMethodClass");
        var originTemplate = new Avm1SourceClass(
            className,
            baseType: null,
            interfaces: null,
            members: [method],
            origin: new Avm1SourceClassOrigin(
                SpriteId: 23,
                RuntimeName: className.Value,
                HasInitializer: true));
        var compiler = new Avm1Compiler();
        var canonical = compiler.CompileClass(
            originTemplate,
            new Avm1ClassCompilationOptions(7));
        canonical.Succeeded.ShouldBeTrue(Describe(canonical));
        var sourceClass = new Avm1SourceClass(
            className,
            baseType: null,
            interfaces: null,
            members: [method],
            origin: originTemplate.Origin with
            {
                Bytecode = new Avm1SourceClassBytecodeOrigin(
                    swfVersion: 7,
                    canonical.Bytecode,
                    Avm1SourceFingerprint.ComputeClass(originTemplate),
                    Avm1SourceFingerprint.ComputeClassShape(originTemplate))
            });

        var artifact = compiler.CompileClass(
            sourceClass,
            new Avm1ClassCompilationOptions(7)
            {
                AbiProfile = Avm1ClassAbiProfile.OriginPreserving
            });

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.OriginReuseKind.ShouldBe(
            Avm1OriginReuseKind.CompleteInitializer);
        var placement = artifact.SourceMap.MethodBodies.ShouldHaveSingleItem();
        placement.Kind.ShouldBe(Avm1CompiledClassMethodBodyKind.Unplaced);
        placement.IsPlaced.ShouldBeFalse();
        placement.IsMapped.ShouldBeFalse();
        placement.InitializerActionIndex.ShouldBe(-1);
        artifact.SourceMap.Entries.ShouldBeEmpty();
    }

    [Fact]
    public void Linked_source_map_rebases_class_ranges_into_the_tag_stream()
    {
        var method = CreateMappedMethod("readValue", 19, out var literalOrigin);
        var sourceClass = new Avm1SourceClass(
            new Avm1SourceQualifiedName("demo.LinkedMappedClass"),
            baseType: null,
            interfaces: null,
            members: [method],
            origin: new Avm1SourceClassOrigin(
                SpriteId: 31,
                RuntimeName: "demo.LinkedMappedClass",
                HasInitializer: true));
        var artifact = new Avm1Compiler().CompileClass(
            sourceClass,
            new Avm1ClassCompilationOptions(7));
        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var swf = CreateSwf(
            new DefineSpriteTag(
                new TagMetadata(TagCode.DefineSprite, 0, 0),
                id: 31,
                numFrames: 1,
                tags: [new EndTag(new TagMetadata(TagCode.End, 0, 0))]),
            new ShowFrameTag(new TagMetadata(TagCode.ShowFrame, 0, 0)),
            new EndTag(new TagMetadata(TagCode.End, 0, 0)));
        var plan = new Avm1SwfPlacementPlan([
            new Avm1SwfPlacement(artifact, Avm1SwfPlacementKind.DoInitAction)
        ]);
        var options = new Avm1SwfLinkOptions { EnsureExportAsset = false };

        var linked = Avm1SwfLinker.Link(swf, plan, options);

        linked.Succeeded.ShouldBeTrue(Describe(linked));
        var placement = linked.SourceMap.Placements.ShouldHaveSingleItem();
        placement.PlacementIndex.ShouldBe(0);
        placement.Kind.ShouldBe(Avm1SwfPlacementKind.DoInitAction);
        placement.TagCode.ShouldBe(TagCode.DoInitAction);
        placement.SpriteId.ShouldBe((ushort)31);
        placement.ActionDataOffset.ShouldBe(2);
        placement.ActionLength.ShouldBe(artifact.Bytecode.Length);
        placement.ActionStreamOffset.ShouldBe(
            placement.TagStreamOffset +
            placement.TagHeaderLength +
            placement.ActionDataOffset);
        linked.SourceMap.Entries.Count.ShouldBe(artifact.SourceMap.Entries.Count);
        foreach (var entry in linked.SourceMap.Entries)
        {
            entry.PlacementIndex.ShouldBe(0);
            entry.TagStreamOffset.ShouldBe(
                placement.ActionStreamOffset + entry.ClassEntry.ByteOffset);
        }

        var literalEntry = linked.SourceMap.GetEntries(0, 0, literalOrigin)
            .ShouldHaveSingleItem();
        linked.SourceMap.TryGetEntry(
                literalEntry.TagStreamOffset,
                out var resolved)
            .ShouldBeTrue();
        resolved.ShouldBe(literalEntry);
        linked.SourceMap.TryGetOrigin(
                literalEntry.TagStreamOffset,
                out var resolvedOrigin)
            .ShouldBeTrue();
        resolvedOrigin.ShouldBe(method.Body.Arena[literalOrigin]);
        ReferenceEquals(
                linked.SourceMap.GetClassSourceMap(0),
                artifact.SourceMap)
            .ShouldBeTrue();

        var noOp = Avm1SwfLinker.Link(swf, plan, options);
        noOp.Succeeded.ShouldBeTrue(Describe(noOp));
        noOp.Changes.ShouldBeEmpty();
        noOp.SourceMap.Placements.ShouldHaveSingleItem();
        noOp.SourceMap.Entries.Count.ShouldBe(linked.SourceMap.Entries.Count);
    }

    [Fact]
    public void Adobe_class_lowering_uses_cs6_package_and_function_abi()
    {
        var constructor = CreateMethod(
            "Widget",
            Avm1SourceMethodKind.Constructor,
            returnValue: null,
            parameterNames: ["value"]);
        var method = CreateMethod(
            "measure",
            Avm1SourceMethodKind.Method,
            returnValue: 42);
        var getter = CreateMethod(
            "status",
            Avm1SourceMethodKind.Getter,
            returnValue: 7,
            runtimeName: "__get__status");
        var sourceClass = new Avm1SourceClass(
            new Avm1SourceQualifiedName("demo.widgets.Widget"),
            new Avm1SourceQualifiedName("demo.BaseWidget"),
            interfaces: null,
            members: [constructor, method, getter]);

        var artifact = new Avm1Compiler().CompileClass(
            sourceClass,
            new Avm1ClassCompilationOptions(7)
            {
                AbiProfile = Avm1ClassAbiProfile.AdobeFlashCs6Compatible
            });

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.InitializerBody.ShouldNotBeNull().Actions
            .OfType<ActionNewObject>()
            .Count().ShouldBe(2);
        artifact.InitializerBody.Actions.ShouldNotContain(action =>
            action is ActionInitObject);
        artifact.InitializerBody.Actions.OfType<ActionCallFunction>()
            .ShouldHaveSingleItem();

        var functions = artifact.InitializerBody.Actions
            .OfType<ActionDefineFunction2>()
            .ToArray();
        functions.Length.ShouldBe(3);
        functions[0].Flags.ShouldBe(
            FunctionFlags.PreloadThis |
            FunctionFlags.SuppressArguments |
            FunctionFlags.PreloadSuper);
        functions.Skip(1).ShouldAllBe(function =>
            function.Flags == (
                FunctionFlags.PreloadThis |
                FunctionFlags.SuppressArguments |
                FunctionFlags.SuppressSuper));
        functions.ShouldAllBe(function => function.RegisterCount >= 2);
        functions[0].Parameters.ShouldHaveSingleItem()
            .ShouldBe(new FunctionParameter(0, "value"));
    }

    [Fact]
    public void Adobe_class_method_uses_the_wrapper_arguments_preload_register()
    {
        var builder = new Avm1SourceArena.Builder();
        var arguments = builder.AddSymbol(
            Avm1SourceSymbolKind.Arguments,
            "arguments",
            SourceTypeIndex.Invalid,
            builder.GetBuiltInType(Avm1SourceTypeKind.Unknown));
        var body = builder.AddStatement(
            Avm1SourceStatementKind.Block,
            children:
            [
                builder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: builder.AddExpression(
                        Avm1SourceExpressionKind.SymbolReference,
                        symbol: arguments))
            ]);
        var method = new Avm1SourceMethodDeclaration(
            "readArguments",
            Avm1SourceMethodKind.Method,
            new Avm1SourceMethod(builder.ToArena(), body, [], []));
        var sourceClass = new Avm1SourceClass(
            new Avm1SourceQualifiedName("demo.ArgumentsProbe"),
            baseType: null,
            interfaces: null,
            members: [method]);

        var artifact = new Avm1Compiler().CompileClass(
            sourceClass,
            new Avm1ClassCompilationOptions(7)
            {
                AbiProfile = Avm1ClassAbiProfile.AdobeFlashCs6Compatible
            });

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var compiled = artifact.Methods.ShouldHaveSingleItem();
        compiled.FunctionFlags.ShouldBe(
            FunctionFlags.PreloadThis |
            FunctionFlags.PreloadArguments |
            FunctionFlags.SuppressSuper);
        compiled.Method.ActionBody.ShouldNotBeNull().Actions
            .OfType<ActionPush>()
            .SelectMany(action => action.PushValues)
            .OfType<PushValue.PushValueRegister>()
            .Select(value => value.RegisterIndex)
            .ShouldContain((byte)2);
    }

    [Theory]
    [InlineData(Avm1ClassAbiProfile.Canonical)]
    [InlineData(Avm1ClassAbiProfile.AdobeFlashCs6Compatible)]
    public void Interface_lowering_emits_an_empty_runtime_class(
        Avm1ClassAbiProfile abiProfile)
    {
        var signature = CreateMethod(
            "apply",
            Avm1SourceMethodKind.Method,
            returnValue: null,
            parameterNames: ["value"]);
        signature = new Avm1SourceMethodDeclaration(
            signature.Name,
            signature.Kind,
            signature.Body,
            signature.Modifiers,
            signature.DeclaredReturnType,
            signature.InferredReturnType,
            signature.Origin,
            hasBody: false);
        var sourceInterface = new Avm1SourceClass(
            new Avm1SourceQualifiedName("demo.contracts.IWidget"),
            baseType: null,
            interfaces: [new Avm1SourceQualifiedName("demo.contracts.IBase")],
            members: [signature],
            kind: Avm1SourceTypeDeclarationKind.Interface);

        var artifact = new Avm1Compiler().CompileClass(
            sourceInterface,
            new Avm1ClassCompilationOptions(7) { AbiProfile = abiProfile });

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Methods.ShouldBeEmpty();
        artifact.InitializerBody.ShouldNotBeNull().Actions[^1]
            .ShouldBeOfType<ActionEnd>();
        var runtimeType = artifact.InitializerBody.Actions
            .OfType<ActionDefineFunction2>()
            .ShouldHaveSingleItem();
        runtimeType.Parameters.ShouldBeEmpty();
        runtimeType.Body.Length.ShouldBe(0);
        artifact.InitializerBody.Actions.OfType<ActionImplementsOp>()
            .ShouldHaveSingleItem();
        ShockwaveFlash.Avm1.Action.DecodeCollection(
                artifact.Bytecode,
                swfVersion: 7,
                strict: true)
            .ShouldNotBeNull();
    }

    [Fact]
    public void Interface_lowering_rejects_non_signature_members()
    {
        var fieldInterface = new Avm1SourceClass(
            new Avm1SourceQualifiedName("demo.IField"),
            baseType: null,
            interfaces: null,
            members: [new Avm1SourceField("value")],
            kind: Avm1SourceTypeDeclarationKind.Interface);
        var bodyInterface = new Avm1SourceClass(
            new Avm1SourceQualifiedName("demo.IBody"),
            baseType: null,
            interfaces: null,
            members:
            [
                CreateMethod(
                    "run",
                    Avm1SourceMethodKind.Method,
                    returnValue: 1)
            ],
            kind: Avm1SourceTypeDeclarationKind.Interface);
        var staticSignature = CreateMethod(
            "run",
            Avm1SourceMethodKind.Method,
            returnValue: null);
        staticSignature = new Avm1SourceMethodDeclaration(
            staticSignature.Name,
            staticSignature.Kind,
            staticSignature.Body,
            Avm1SourceDeclarationModifiers.Static,
            hasBody: false);
        var staticInterface = new Avm1SourceClass(
            new Avm1SourceQualifiedName("demo.IStatic"),
            baseType: null,
            interfaces: null,
            members: [staticSignature],
            kind: Avm1SourceTypeDeclarationKind.Interface);
        var compiler = new Avm1Compiler();
        var options = new Avm1ClassCompilationOptions(7);

        compiler.CompileClass(fieldInterface, options).Diagnostics
            .ShouldContain(diagnostic => diagnostic.Code == "AVM1CLS004");
        compiler.CompileClass(bodyInterface, options).Diagnostics
            .ShouldContain(diagnostic => diagnostic.Code == "AVM1CLS005");
        compiler.CompileClass(staticInterface, options).Diagnostics
            .ShouldContain(diagnostic => diagnostic.Code == "AVM1CLS006");
    }

    [Fact]
    public void Program_lowering_orders_type_dependencies_and_rejects_cycles()
    {
        var contract = new Avm1SourceClass(
            new Avm1SourceQualifiedName("demo.IContract"),
            baseType: null,
            interfaces: null,
            members: [],
            kind: Avm1SourceTypeDeclarationKind.Interface);
        var baseClass = new Avm1SourceClass(
            new Avm1SourceQualifiedName("demo.Base"),
            baseType: null,
            interfaces: null,
            members: []);
        var derived = new Avm1SourceClass(
            new Avm1SourceQualifiedName("demo.Derived"),
            new Avm1SourceQualifiedName("demo.Base"),
            [new Avm1SourceQualifiedName("demo.IContract")],
            members: []);
        var program = Avm1SourceProgram.Create([
            new Avm1SourceFile([derived, contract, baseClass])
        ]);

        var artifact = new Avm1Compiler().CompileProgram(
            program,
            new Avm1ClassCompilationOptions(7));

        artifact.Succeeded.ShouldBeTrue();
        artifact.Classes.Select(item => item.Source.Name.Value).ShouldBe([
            "demo.Base",
            "demo.IContract",
            "demo.Derived"
        ]);

        var first = new Avm1SourceClass(
            new Avm1SourceQualifiedName("demo.IFirst"),
            baseType: null,
            interfaces: [new Avm1SourceQualifiedName("demo.ISecond")],
            members: [],
            kind: Avm1SourceTypeDeclarationKind.Interface);
        var second = new Avm1SourceClass(
            new Avm1SourceQualifiedName("demo.ISecond"),
            baseType: null,
            interfaces: [new Avm1SourceQualifiedName("demo.IFirst")],
            members: [],
            kind: Avm1SourceTypeDeclarationKind.Interface);
        var cyclic = new Avm1Compiler().CompileProgram(
            Avm1SourceProgram.Create([new Avm1SourceFile([first, second])]),
            new Avm1ClassCompilationOptions(7));

        cyclic.Succeeded.ShouldBeFalse();
        cyclic.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1CLS100");
    }

    [Fact]
    public void Synthetic_multi_class_swf_compiles_links_and_decompiles_completely()
    {
        var signature = CreateMethod(
            "measure",
            Avm1SourceMethodKind.Method,
            returnValue: null);
        signature = new Avm1SourceMethodDeclaration(
            signature.Name,
            signature.Kind,
            signature.Body,
            signature.Modifiers,
            signature.DeclaredReturnType,
            signature.InferredReturnType,
            signature.Origin,
            hasBody: false);
        var contract = new Avm1SourceClass(
            new Avm1SourceQualifiedName("audit.IWidget"),
            baseType: null,
            interfaces: null,
            members:
            [
                signature
            ],
            origin: new Avm1SourceClassOrigin(7, "audit.IWidget", HasInitializer: true),
            kind: Avm1SourceTypeDeclarationKind.Interface);
        var baseClass = new Avm1SourceClass(
            new Avm1SourceQualifiedName("audit.BaseWidget"),
            baseType: null,
            interfaces: null,
            members:
            [
                CreateMethod(
                    "BaseWidget",
                    Avm1SourceMethodKind.Constructor,
                    returnValue: null)
            ],
            origin: new Avm1SourceClassOrigin(8, "audit.BaseWidget", HasInitializer: true));
        var derived = new Avm1SourceClass(
            new Avm1SourceQualifiedName("audit.Widget"),
            new Avm1SourceQualifiedName("audit.BaseWidget"),
            [new Avm1SourceQualifiedName("audit.IWidget")],
            [
                CreateLiteralField("version", 3, Avm1SourceDeclarationModifiers.Static),
                CreateMethod("Widget", Avm1SourceMethodKind.Constructor, returnValue: null),
                CreateMethod("measure", Avm1SourceMethodKind.Method, returnValue: 42),
                CreateMethod(
                    "status",
                    Avm1SourceMethodKind.Getter,
                    returnValue: 7,
                    runtimeName: "__get__status"),
                CreateMethod(
                    "status",
                    Avm1SourceMethodKind.Setter,
                    returnValue: null,
                    parameterNames: ["value"],
                    runtimeName: "__set__status")
            ],
            origin: new Avm1SourceClassOrigin(9, "audit.Widget", HasInitializer: true));
        var program = Avm1SourceProgram.Create([
            new Avm1SourceFile([derived, contract, baseClass])
        ]);
        var compiled = new Avm1Compiler().CompileProgram(
            program,
            new Avm1ClassCompilationOptions(7));

        compiled.Succeeded.ShouldBeTrue(string.Join(
            Environment.NewLine,
            compiled.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var swf = CreateSwf(
            new FileAttributesTag(
                new TagMetadata(TagCode.FileAttributes, 0, 0),
                (FileAttributesFlags)0),
            CreateSprite(7),
            CreateSprite(8),
            CreateSprite(9),
            new ShowFrameTag(new TagMetadata(TagCode.ShowFrame, 0, 0)),
            new EndTag(new TagMetadata(TagCode.End, 0, 0)));
        var linked = Avm1SwfLinker.Link(
            swf,
            Avm1SwfPlacementPlan.FromProgram(compiled));

        linked.Succeeded.ShouldBeTrue(Describe(linked));
        var reparsed = ShockwaveFlashFile.Disassemble(swf.Assemble());
        reparsed.Tags.OfType<DoInitActionTag>().Count().ShouldBe(3);
        foreach (var className in new[]
                 {
                     "audit.IWidget",
                     "audit.BaseWidget",
                     "audit.Widget"
                 })
        {
            Avm1ClassDecompiler.TryDecompileSourceFile(
                    reparsed,
                    className,
                    out var sourceFile)
                .ShouldBeTrue(className);
            sourceFile.ShouldNotBeNull().IsComplete.ShouldBeTrue(className);
        }

        Avm1ClassDecompiler.TryDecompileSourceFile(
                reparsed,
                "audit.Widget",
                out var widgetFile)
            .ShouldBeTrue();
        var widget = widgetFile.ShouldNotBeNull().Classes.ShouldHaveSingleItem();
        widget.BaseType.ShouldBe(new Avm1SourceQualifiedName("audit.BaseWidget"));
        widget.Interfaces.ShouldBe([new Avm1SourceQualifiedName("audit.IWidget")]);
        widget.Constructor.ShouldNotBeNull();
        widget.Methods.Select(method => (method.Name, method.Kind)).ShouldContain(
            ("status", Avm1SourceMethodKind.Getter));
        widget.Methods.Select(method => (method.Name, method.Kind)).ShouldContain(
            ("status", Avm1SourceMethodKind.Setter));
    }

    [Fact]
    public void Parallel_class_and_program_compilation_is_byte_deterministic()
    {
        var methods = Enumerable.Range(0, 12)
            .Select(index => CreateMethod(
                "method" + index.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                Avm1SourceMethodKind.Method,
                returnValue: index))
            .ToArray();
        var sourceClass = new Avm1SourceClass(
            new Avm1SourceQualifiedName("demo.ParallelMethods"),
            baseType: null,
            interfaces: null,
            members: methods);
        var compiler = new Avm1Compiler();
        var sequentialOptions = new Avm1ClassCompilationOptions(7)
        {
            MaxDegreeOfParallelism = 1
        };
        var parallelOptions = sequentialOptions with
        {
            MaxDegreeOfParallelism = 4
        };

        var sequentialClass = compiler.CompileClass(
            sourceClass,
            sequentialOptions);
        var parallelClass = compiler.CompileClass(
            sourceClass,
            parallelOptions);

        AssertEquivalent(sequentialClass, parallelClass);

        var classes = Enumerable.Range(0, 8)
            .Select(index => new Avm1SourceClass(
                new Avm1SourceQualifiedName(
                    "demo.ProgramClass" + index.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)),
                baseType: null,
                interfaces: null,
                members:
                [
                    CreateMethod(
                        "value",
                        Avm1SourceMethodKind.Method,
                        returnValue: index)
                ]))
            .ToArray();
        var program = Avm1SourceProgram.Create(
            [new Avm1SourceFile(classes)]);
        var sequentialProgram = compiler.CompileProgram(
            program,
            sequentialOptions);

        for (var iteration = 0; iteration < 3; iteration++)
        {
            var parallelProgram = compiler.CompileProgram(
                program,
                parallelOptions);
            parallelProgram.Diagnostics.ShouldBe(
                sequentialProgram.Diagnostics);
            parallelProgram.Classes.Count.ShouldBe(
                sequentialProgram.Classes.Count);
            for (var i = 0; i < sequentialProgram.Classes.Count; i++)
            {
                AssertEquivalent(
                    sequentialProgram.Classes[i],
                    parallelProgram.Classes[i]);
            }
        }
    }

    [Fact]
    public void Class_compilation_rejects_nonpositive_parallelism()
    {
        var sourceClass = new Avm1SourceClass(
            new Avm1SourceQualifiedName("demo.InvalidParallelism"),
            baseType: null,
            interfaces: null,
            members: []);

        var artifact = new Avm1Compiler().CompileClass(
            sourceClass,
            new Avm1ClassCompilationOptions(7)
            {
                MaxDegreeOfParallelism = 0
            });

        artifact.Succeeded.ShouldBeFalse();
        artifact.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1CLS007");
    }

    [Fact]
    public void Parallel_program_compilation_observes_cancellation()
    {
        var classes = Enumerable.Range(0, 8)
            .Select(index => new Avm1SourceClass(
                new Avm1SourceQualifiedName(
                    "demo.CancelledClass" + index.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)),
                baseType: null,
                interfaces: null,
                members:
                [
                    CreateMethod(
                        "value",
                        Avm1SourceMethodKind.Method,
                        returnValue: index)
                ]))
            .ToArray();
        var program = Avm1SourceProgram.Create(
            [new Avm1SourceFile(classes)]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Should.Throw<OperationCanceledException>(() =>
            new Avm1Compiler().CompileProgram(
                program,
                new Avm1ClassCompilationOptions(7)
                {
                    MaxDegreeOfParallelism = 4
                },
                cancellation.Token));
    }

    [Fact]
    public void Canonical_class_lowering_rejects_pre_function2_targets()
    {
        var sourceClass = new Avm1SourceClass(
            new Avm1SourceQualifiedName("Widget"),
            baseType: null,
            interfaces: null,
            members: []);

        var artifact = new Avm1Compiler().CompileClass(
            sourceClass,
            new Avm1ClassCompilationOptions(6));

        artifact.Succeeded.ShouldBeFalse();
        artifact.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1CLS001");
    }

    [Fact]
    public void Swf_linker_dry_run_and_apply_are_transactional()
    {
        var artifact = CompileEmptyClass(spriteId: 7, "demo.Widget");
        var swf = CreateSwf(
            new FileAttributesTag(
                new TagMetadata(TagCode.FileAttributes, 0, 0),
                (FileAttributesFlags)0),
            new DefineSpriteTag(
                new TagMetadata(TagCode.DefineSprite, 0, 0),
                id: 7,
                numFrames: 1,
                tags:
                [
                    new EndTag(new TagMetadata(TagCode.End, 0, 0))
                ]),
            new ShowFrameTag(new TagMetadata(TagCode.ShowFrame, 0, 0)),
            new EndTag(new TagMetadata(TagCode.End, 0, 0)));
        var originalTags = swf.Tags.ToArray();
        var plan = new Avm1SwfPlacementPlan([
            new Avm1SwfPlacement(artifact, Avm1SwfPlacementKind.DoInitAction)
        ]);

        var dryRun = Avm1SwfLinker.Link(
            swf,
            plan,
            new Avm1SwfLinkOptions { DryRun = true });

        dryRun.Succeeded.ShouldBeTrue(Describe(dryRun));
        dryRun.Applied.ShouldBeFalse();
        dryRun.Changes.Select(change => change.TagCode).ShouldBe([
            TagCode.ExportAssets,
            TagCode.DoInitAction
        ]);
        var dryRunPatch = dryRun.PatchReport.ShouldNotBeNull();
        dryRunPatch.IsByteIdentical.ShouldBeFalse();
        dryRunPatch.Ranges.Count.ShouldBe(2);
        dryRunPatch.Ranges.ShouldAllBe(range => !range.Original.HasValue);
        swf.Tags.ShouldBe(originalTags);

        var linked = Avm1SwfLinker.Link(swf, plan);

        linked.Succeeded.ShouldBeTrue(Describe(linked));
        linked.Applied.ShouldBeTrue();
        swf.Tags.OfType<ExportAssetsTag>().ShouldHaveSingleItem()
            .Assets.ShouldHaveSingleItem()
            .Name.ShouldBe("demo.Widget");
        swf.Tags.OfType<DoInitActionTag>().ShouldHaveSingleItem()
            .Data.ShouldBe(artifact.Bytecode);
        var reparsed = ShockwaveFlashFile.Disassemble(swf.Assemble());
        var init = reparsed.Tags.OfType<DoInitActionTag>().ShouldHaveSingleItem();
        ShockwaveFlash.Avm1.Action.DecodeCollection(
                init.Data,
                swfVersion: 7,
                strict: true)[^1]
            .ShouldBeOfType<ActionEnd>();
        Avm1ClassDecompiler.TryDecompileSourceFile(
            reparsed,
            "demo.Widget",
            out var roundTrip).ShouldBeTrue();
        roundTrip!.Classes.ShouldHaveSingleItem().Methods.ShouldNotContain(method =>
            method.Name == "Widget");

        var replacement = Avm1SwfLinker.Link(swf, plan);
        replacement.Succeeded.ShouldBeTrue(Describe(replacement));
        replacement.Changes.ShouldBeEmpty();
        var replacementPatch = replacement.PatchReport.ShouldNotBeNull();
        replacementPatch.IsByteIdentical.ShouldBeTrue();
        replacementPatch.Ranges.ShouldBeEmpty();
        swf.Tags.OfType<DoInitActionTag>().Count().ShouldBe(1);
    }

    [Fact]
    public void Swf_linker_places_frame_actions_and_rejects_as3_transactionally()
    {
        var artifact = CompileEmptyClass(spriteId: 0, "FrameScript");
        var swf = CreateSwf(
            new ShowFrameTag(new TagMetadata(TagCode.ShowFrame, 0, 0)),
            new EndTag(new TagMetadata(TagCode.End, 0, 0)));
        var insertion = new Avm1SwfPlacementPlan([
            new Avm1SwfPlacement(artifact, Avm1SwfPlacementKind.DoAction)
            {
                FrameIndex = 0
            }
        ]);

        var linked = Avm1SwfLinker.Link(swf, insertion);

        linked.Succeeded.ShouldBeTrue(Describe(linked));
        swf.Tags[0].ShouldBeOfType<DoActionTag>();
        var replacement = new Avm1SwfPlacementPlan([
            new Avm1SwfPlacement(artifact, Avm1SwfPlacementKind.DoAction)
            {
                ReplaceTagIndex = 0
            }
        ]);
        var noOp = Avm1SwfLinker.Link(swf, replacement);
        noOp.Succeeded.ShouldBeTrue();
        noOp.Changes.ShouldBeEmpty();
        noOp.PatchReport.ShouldNotBeNull().IsByteIdentical.ShouldBeTrue();
        swf.Tags.OfType<DoActionTag>().Count().ShouldBe(1);

        var as3 = CreateSwf(
            new FileAttributesTag(
                new TagMetadata(TagCode.FileAttributes, 0, 0),
                FileAttributesFlags.IsActionScript3),
            new ShowFrameTag(new TagMetadata(TagCode.ShowFrame, 0, 0)),
            new EndTag(new TagMetadata(TagCode.End, 0, 0)));
        var original = as3.Tags.ToArray();

        var rejected = Avm1SwfLinker.Link(as3, insertion);

        rejected.Succeeded.ShouldBeFalse();
        rejected.Applied.ShouldBeFalse();
        rejected.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1LNK001");
        as3.Tags.ShouldBe(original);
    }

    [Fact]
    public void Swf_linker_manages_debugger_metadata_transactionally()
    {
        var artifact = CompileEmptyClass(spriteId: 0, "FrameScript");
        var debugId = new Guid("00112233-4455-6677-8899-aabbccddeeff");
        var swf = CreateSwf(
            new ShowFrameTag(new TagMetadata(TagCode.ShowFrame, 0, 0)),
            new EndTag(new TagMetadata(TagCode.End, 0, 0)));
        var originalTags = swf.Tags.ToArray();
        var insertion = new Avm1SwfPlacementPlan([
            new Avm1SwfPlacement(artifact, Avm1SwfPlacementKind.DoAction)
        ]);
        var dryRunOptions = new Avm1SwfLinkOptions
        {
            DryRun = true,
            Debugger = new Avm1SwfDebuggerOptions(debugId)
        };

        var dryRun = Avm1SwfLinker.Link(swf, insertion, dryRunOptions);

        dryRun.Succeeded.ShouldBeTrue(Describe(dryRun));
        dryRun.Applied.ShouldBeFalse();
        dryRun.Debugger.ShouldBe(new Avm1SwfDebuggerInfo(debugId, string.Empty));
        dryRun.Changes.Select(change => change.TagCode).ShouldBe([
            TagCode.DoAction,
            TagCode.EnableDebugger2,
            TagCode.DebugId
        ]);
        dryRun.PatchReport.ShouldNotBeNull().Ranges.Count.ShouldBe(3);
        swf.Tags.ShouldBe(originalTags);

        var options = dryRunOptions with { DryRun = false };
        var linked = Avm1SwfLinker.Link(swf, insertion, options);

        linked.Succeeded.ShouldBeTrue(Describe(linked));
        linked.Applied.ShouldBeTrue();
        swf.Tags.OfType<EnableDebugger2Tag>().ShouldHaveSingleItem()
            .Password.ShouldBeEmpty();
        swf.Tags.OfType<DebugIdTag>().ShouldHaveSingleItem()
            .Id.ShouldBe(debugId);
        swf.Tags.OfType<DoActionTag>().ShouldHaveSingleItem();
        var reparsed = ShockwaveFlashFile.Disassemble(swf.Assemble());
        reparsed.Tags.OfType<EnableDebugger2Tag>().ShouldHaveSingleItem()
            .Password.ShouldBeEmpty();
        reparsed.Tags.OfType<DebugIdTag>().ShouldHaveSingleItem()
            .Id.ShouldBe(debugId);

        var actionIndex = swf.Tags.FindIndex(tag => tag is DoActionTag);
        var replacement = new Avm1SwfPlacementPlan([
            new Avm1SwfPlacement(artifact, Avm1SwfPlacementKind.DoAction)
            {
                ReplaceTagIndex = actionIndex
            }
        ]);
        var noOp = Avm1SwfLinker.Link(swf, replacement, options);

        noOp.Succeeded.ShouldBeTrue(Describe(noOp));
        noOp.Changes.ShouldBeEmpty();
        noOp.PatchReport.ShouldNotBeNull().IsByteIdentical.ShouldBeTrue();
        swf.Tags.OfType<EnableDebugger2Tag>().Count().ShouldBe(1);
        swf.Tags.OfType<DebugIdTag>().Count().ShouldBe(1);
    }

    [Fact]
    public void Swf_linker_rejects_incompatible_debugger_metadata_transactionally()
    {
        var debugId = new Guid("00112233-4455-6677-8899-aabbccddeeff");
        var emptyPlan = new Avm1SwfPlacementPlan([]);
        var versionFive = new ShockwaveFlashFile(
            new ShockwaveFlashHeader(
                ShockwaveFlashCompression.None,
                Version: 5,
                FileLength: 0,
                FrameSize: new Rectangle(0, 0, 100, 100),
                FrameRate: Fixed8.Zero,
                FrameCount: 1),
            [new EndTag(new TagMetadata(TagCode.End, 0, 0))]);
        var versionFiveTags = versionFive.Tags.ToArray();

        var unsupported = Avm1SwfLinker.Link(
            versionFive,
            emptyPlan,
            new Avm1SwfLinkOptions
            {
                Debugger = new Avm1SwfDebuggerOptions(debugId)
            });

        unsupported.Succeeded.ShouldBeFalse();
        unsupported.Applied.ShouldBeFalse();
        unsupported.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1LNK015");
        versionFive.Tags.ShouldBe(versionFiveTags);

        var duplicates = CreateSwf(
            new EnableDebuggerTag(
                new TagMetadata(TagCode.EnableDebugger, 0, 0),
                string.Empty),
            new EnableDebugger2Tag(
                new TagMetadata(TagCode.EnableDebugger2, 0, 0),
                string.Empty),
            new EndTag(new TagMetadata(TagCode.End, 0, 0)));
        var duplicateTags = duplicates.Tags.ToArray();

        var rejected = Avm1SwfLinker.Link(
            duplicates,
            emptyPlan,
            new Avm1SwfLinkOptions
            {
                Debugger = new Avm1SwfDebuggerOptions(debugId)
            });

        rejected.Succeeded.ShouldBeFalse();
        rejected.Applied.ShouldBeFalse();
        rejected.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1LNK018");
        duplicates.Tags.ShouldBe(duplicateTags);
    }

    [Fact]
    public void Swf_patch_report_collapses_repeated_export_tag_updates()
    {
        var first = CompileEmptyClass(spriteId: 7, "demo.First");
        var second = CompileEmptyClass(spriteId: 8, "demo.Second");
        var swf = CreateSwf(
            new DefineSpriteTag(
                new TagMetadata(TagCode.DefineSprite, 0, 0),
                id: 7,
                numFrames: 1,
                tags: [new EndTag(new TagMetadata(TagCode.End, 0, 0))]),
            new DefineSpriteTag(
                new TagMetadata(TagCode.DefineSprite, 0, 0),
                id: 8,
                numFrames: 1,
                tags: [new EndTag(new TagMetadata(TagCode.End, 0, 0))]),
            new ShowFrameTag(new TagMetadata(TagCode.ShowFrame, 0, 0)),
            new EndTag(new TagMetadata(TagCode.End, 0, 0)));
        var plan = new Avm1SwfPlacementPlan([
            new Avm1SwfPlacement(first, Avm1SwfPlacementKind.DoInitAction),
            new Avm1SwfPlacement(second, Avm1SwfPlacementKind.DoInitAction)
        ]);

        var result = Avm1SwfLinker.Link(
            swf,
            plan,
            new Avm1SwfLinkOptions { DryRun = true });

        result.Succeeded.ShouldBeTrue(Describe(result));
        result.Changes.Count.ShouldBe(4);
        result.Changes.Select(change => change.TagIndex).ShouldAllBe(index =>
            index >= 0);
        var patch = result.PatchReport.ShouldNotBeNull();
        patch.Ranges.Count.ShouldBe(3);
        var exportRange = patch.Ranges
            .Where(range => range.TagCode == TagCode.ExportAssets)
            .ShouldHaveSingleItem();
        exportRange.Kind.ShouldBe(Avm1SwfChangeKind.InsertTag);
        exportRange.SpriteId.ShouldBe((ushort)0);
        exportRange.Original.ShouldBeNull();
    }

    private static Avm1SourceField CreateConditionalField(string name)
    {
        var builder = new Avm1SourceArena.Builder();
        var condition = builder.AddExpression(
            Avm1SourceExpressionKind.DynamicName,
            name: builder.InternString("featureEnabled"));
        var whenTrue = AddInteger(builder, 1);
        var whenFalse = AddInteger(builder, 0);
        var expression = builder.AddExpression(
            Avm1SourceExpressionKind.Conditional,
            children: [condition, whenTrue, whenFalse]);
        var fragment = new Avm1SourceExpressionFragment(
            builder.ToArena(),
            expression,
            []);
        return new Avm1SourceField(name, initializer: fragment);
    }

    private static Avm1SourceField CreateLiteralField(
        string name,
        int value,
        Avm1SourceDeclarationModifiers modifiers)
    {
        var builder = new Avm1SourceArena.Builder();
        var expression = AddInteger(builder, value);
        return new Avm1SourceField(
            name,
            modifiers,
            initializer: new Avm1SourceExpressionFragment(
                builder.ToArena(),
                expression,
                []));
    }

    private static Avm1SourceMethodDeclaration CreateMethod(
        string name,
        Avm1SourceMethodKind kind,
        int? returnValue,
        IReadOnlyList<string>? parameterNames = null,
        string? runtimeName = null)
    {
        var builder = new Avm1SourceArena.Builder();
        var unknown = builder.GetBuiltInType(Avm1SourceTypeKind.Unknown);
        var parameters = (parameterNames ?? [])
            .Select(parameter => builder.AddSymbol(
                Avm1SourceSymbolKind.Parameter,
                parameter,
                SourceTypeIndex.Invalid,
                unknown))
            .ToArray();
        SourceStatementIndex[] statements = returnValue.HasValue
            ? [
                builder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: AddInteger(builder, returnValue.Value))
            ]
            : [];
        var body = builder.AddStatement(
            Avm1SourceStatementKind.Block,
            children: statements);
        return new Avm1SourceMethodDeclaration(
            name,
            kind,
            new Avm1SourceMethod(builder.ToArena(), body, parameters, []),
            origin: new Avm1SourceMemberOrigin(runtimeName));
    }

    private static Avm1SourceMethodDeclaration CreateMappedMethod(
        string name,
        int returnValue,
        out SourceOriginIndex literalOrigin)
    {
        var builder = new Avm1SourceArena.Builder();
        literalOrigin = builder.AddOrigin(0, 1, 8, 9);
        var returnOrigin = builder.AddOrigin(0, 1, 1, 10);
        var literal = builder.AddExpression(
            Avm1SourceExpressionKind.Literal,
            literal: builder.AddLiteral(
                Avm1SourceLiteralKind.Integer,
                integerValue: returnValue),
            origin: literalOrigin);
        var returnStatement = builder.AddStatement(
            Avm1SourceStatementKind.Return,
            expression: literal,
            origin: returnOrigin);
        var body = builder.AddStatement(
            Avm1SourceStatementKind.Block,
            children: [returnStatement]);
        return new Avm1SourceMethodDeclaration(
            name,
            Avm1SourceMethodKind.Method,
            new Avm1SourceMethod(builder.ToArena(), body, [], []));
    }

    private static SourceExpressionIndex AddInteger(
        Avm1SourceArena.Builder builder,
        int value) =>
        builder.AddExpression(
            Avm1SourceExpressionKind.Literal,
            literal: builder.AddLiteral(
                Avm1SourceLiteralKind.Integer,
                integerValue: value));

    private static void AssertEquivalent(
        Avm1ClassArtifact expected,
        Avm1ClassArtifact actual)
    {
        actual.Succeeded.ShouldBe(expected.Succeeded);
        actual.Diagnostics.ShouldBe(expected.Diagnostics);
        actual.Source.Name.ShouldBe(expected.Source.Name);
        actual.Bytecode.ToArray().ShouldBe(expected.Bytecode.ToArray());
        actual.Methods.Select(method => method.Declaration.Name).ShouldBe(
            expected.Methods.Select(method => method.Declaration.Name));
        actual.Methods.Count.ShouldBe(expected.Methods.Count);
        for (var i = 0; i < expected.Methods.Count; i++)
        {
            actual.Methods[i].FunctionFlags.ShouldBe(
                expected.Methods[i].FunctionFlags);
            actual.Methods[i].RegisterCount.ShouldBe(
                expected.Methods[i].RegisterCount);
            actual.Methods[i].Method.Bytecode.ToArray().ShouldBe(
                expected.Methods[i].Method.Bytecode.ToArray());
        }
    }

    private static Avm1ClassArtifact CompileEmptyClass(
        ushort spriteId,
        string name)
    {
        var sourceClass = new Avm1SourceClass(
            new Avm1SourceQualifiedName(name),
            baseType: null,
            interfaces: null,
            members: [],
            origin: new Avm1SourceClassOrigin(
                spriteId,
                name,
                HasInitializer: true));
        var artifact = new Avm1Compiler().CompileClass(
            sourceClass,
            new Avm1ClassCompilationOptions(7));
        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        return artifact;
    }

    private static ShockwaveFlashFile CreateSwf(params Tag[] tags) =>
        new(
            new ShockwaveFlashHeader(
                ShockwaveFlashCompression.None,
                Version: 7,
                FileLength: 0,
                FrameSize: new Rectangle(0, 0, 100, 100),
                FrameRate: Fixed8.Zero,
                FrameCount: 1),
            tags.ToList());

    private static DefineSpriteTag CreateSprite(ushort id) =>
        new(
            new TagMetadata(TagCode.DefineSprite, 0, 0),
            id,
            numFrames: 1,
            tags:
            [
                new EndTag(new TagMetadata(TagCode.End, 0, 0))
            ]);

    private static string Describe(Avm1ClassArtifact artifact) =>
        string.Join(
            Environment.NewLine,
            artifact.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Code}: {diagnostic.Message}"));

    private static string Describe(Avm1SwfLinkResult result) =>
        string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Code}: {diagnostic.Message}"));
}
