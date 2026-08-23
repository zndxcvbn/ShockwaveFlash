using ShockwaveFlash.Avm1.Decompilation.Ir;
using ShockwaveFlash.Avm1.Decompilation.Analysis;
using ShockwaveFlash.Avm1.Decompilation.Ast;
using ShockwaveFlash.Avm1.Text;

namespace ShockwaveFlash.Avm1.Decompilation;

public static class Avm1Decompiler
{
    public static Avm1MethodDecompilation DecompileMethod(
        ReadOnlyMemory<byte> bytecode,
        byte swfVersion,
        FunctionContext? ctx = null,
        Avm1ClassTypeEnvironment? typeEnvironment = null,
        Avm1MethodDecompilationOptions options = default)
    {
        return DecompileMethod(
            BuildMethodCore(bytecode, swfVersion, ctx),
            typeEnvironment,
            options);
    }

    public static Avm1MethodDecompilation DecompileMethod(
        IReadOnlyList<Action> actions,
        byte swfVersion,
        FunctionContext? ctx = null,
        Avm1ClassTypeEnvironment? typeEnvironment = null,
        Avm1MethodDecompilationOptions options = default)
    {
        return DecompileMethod(
            BuildMethodCore(actions, swfVersion, ctx),
            typeEnvironment,
            options);
    }

    public static Avm1MethodCore BuildMethodCore(
        ReadOnlyMemory<byte> bytecode,
        byte swfVersion,
        FunctionContext? ctx = null)
    {
        var actions = DecodeActions(bytecode, swfVersion);
        return BuildMethodCore(actions, bytecode, swfVersion, ctx);
    }

    internal static IReadOnlyList<Action> DecodeActions(
        ReadOnlyMemory<byte> bytecode,
        byte swfVersion)
    {
        return Action.DecodeCollection(
            bytecode,
            swfVersion,
            Avm1ActionDecodeMode.RecoverMalformed);
    }

    public static Avm1MethodCore BuildMethodCore(
        IReadOnlyList<Action> actions,
        byte swfVersion,
        FunctionContext? ctx = null)
    {
        return BuildMethodCore(
            actions,
            Action.EncodeCollection(actions, swfVersion),
            swfVersion,
            ctx);
    }

    private static Avm1MethodCore BuildMethodCore(
        IReadOnlyList<Action> actions,
        ReadOnlyMemory<byte> rootBytecode,
        byte swfVersion,
        FunctionContext? ctx)
    {
        var instructions = Avm1InstructionTable.Build(actions, rootBytecode, swfVersion);
        var cfg = Avm1ControlFlowGraph.Build(instructions);
        var flowGraph = Avm1FlowGraph.Build(instructions, cfg);
        var reachability = Avm1ReachabilityAnalysis.Build(cfg, flowGraph);
        var irreducibleControlFlow = Avm1IrreducibleControlFlowAnalysis.Build(cfg);
        var dominatorTree = Avm1DominatorTree.Build(cfg);
        var postDominatorTree = Avm1PostDominatorTree.Build(cfg);
        var loopAnalysis = Avm1LoopAnalysis.Build(cfg, dominatorTree);
        var regionAnalysis = Avm1RegionAnalysis.Build(
            instructions,
            cfg,
            postDominatorTree,
            loopAnalysis);
        var stackDepthAnalysis = Avm1StackDepthAnalysis.Build(instructions, cfg, flowGraph);
        var stackIr = Avm1StackIr.Build(instructions, cfg, flowGraph, stackDepthAnalysis);
        var tacIr = Avm1TacIr.Build(instructions, stackIr);
        var completionSsa = Ssa.Avm1CompletionSsa.Build(cfg, tacIr, flowGraph);
        var catchPayloads = Avm1CatchPayloadAnalysis.Build(instructions, cfg, completionSsa);
        var registerSsa = Ssa.Avm1RegisterSsa.Build(
            tacIr,
            cfg,
            flowGraph,
            catchPayloads);
        var constantPools = Avm1ConstantPoolAnalysis.Build(instructions, cfg);
        var valueOrigins = Avm1ValueOriginAnalysis.Build(
            instructions,
            tacIr,
            registerSsa,
            catchPayloads,
            constantPools,
            ctx);

        return new Avm1MethodCore(
            instructions,
            cfg,
            flowGraph,
            reachability,
            irreducibleControlFlow,
            dominatorTree,
            postDominatorTree,
            regionAnalysis,
            loopAnalysis,
            stackDepthAnalysis,
            stackIr,
            tacIr,
            completionSsa,
            catchPayloads,
            registerSsa,
            constantPools,
            valueOrigins,
            ctx);
    }

    public static Avm1MethodDecompilation DecompileMethod(
        Avm1MethodCore core,
        Avm1ClassTypeEnvironment? typeEnvironment = null,
        Avm1MethodDecompilationOptions options = default)
    {
        var valueAnalysis = Avm1ValueAnalysis.Build(
            core.Instructions,
            core.TacIr,
            core.RegisterSsa,
            core.ValueOrigins,
            core.CatchPayloads,
            core.ConstantPools,
            typeEnvironment);
        var switchAnalysis = Avm1SwitchAnalysis.Build(
            core.ControlFlowGraph,
            core.TacIr,
            core.RegisterSsa,
            valueAnalysis,
            core.RegionAnalysis);

        var scopeAnalysis = Avm1ScopeAnalysis.Run(
            core.TacIr,
            core.RegisterSsa,
            valueAnalysis,
            core.ValueOrigins,
            core.Context);

        var structuredAst = Avm1AstBuilder.Build(
            core.ControlFlowGraph,
            core.Instructions,
            core.TacIr,
            valueAnalysis,
            core.RegisterSsa,
            core.RegionAnalysis,
            switchAnalysis,
            scopeAnalysis.SymbolTable,
            options,
            core.Reachability);

        return new Avm1MethodDecompilation(
            core,
            valueAnalysis,
            switchAnalysis,
            scopeAnalysis.SymbolTable,
            structuredAst,
            typeEnvironment,
            options.TimelineLayout);
    }
}
