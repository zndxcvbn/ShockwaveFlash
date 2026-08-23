using ShockwaveFlash.Avm1.Decompilation.Analysis;
using ShockwaveFlash.Avm1.Decompilation.Ir;
using ShockwaveFlash.Avm1.Decompilation.Ssa;
using ShockwaveFlash.Avm1.Text;

namespace ShockwaveFlash.Avm1.Decompilation;

public sealed class Avm1MethodCore
{
    internal Avm1MethodCore(
        Avm1InstructionTable instructions,
        Avm1ControlFlowGraph controlFlowGraph,
        Avm1FlowGraph flowGraph,
        Avm1ReachabilityAnalysis reachability,
        Avm1IrreducibleControlFlowAnalysis irreducibleControlFlow,
        Avm1DominatorTree dominatorTree,
        Avm1PostDominatorTree postDominatorTree,
        Avm1RegionAnalysis regionAnalysis,
        Avm1LoopAnalysis loopAnalysis,
        Avm1StackDepthAnalysis stackDepthAnalysis,
        Avm1StackIr stackIr,
        Avm1TacIr tacIr,
        Avm1CompletionSsa completionSsa,
        Avm1CatchPayloadAnalysis catchPayloads,
        Avm1RegisterSsa registerSsa,
        Avm1ConstantPoolAnalysis constantPools,
        Avm1ValueOriginAnalysis valueOrigins,
        FunctionContext? context)
    {
        Instructions = instructions;
        ControlFlowGraph = controlFlowGraph;
        FlowGraph = flowGraph;
        Reachability = reachability;
        IrreducibleControlFlow = irreducibleControlFlow;
        DominatorTree = dominatorTree;
        PostDominatorTree = postDominatorTree;
        RegionAnalysis = regionAnalysis;
        LoopAnalysis = loopAnalysis;
        StackDepthAnalysis = stackDepthAnalysis;
        StackIr = stackIr;
        TacIr = tacIr;
        CompletionSsa = completionSsa;
        CatchPayloads = catchPayloads;
        RegisterSsa = registerSsa;
        ConstantPools = constantPools;
        ValueOrigins = valueOrigins;
        Context = context;
    }

    public Avm1InstructionTable Instructions { get; }

    public Avm1ControlFlowGraph ControlFlowGraph { get; }

    public Avm1FlowGraph FlowGraph { get; }

    public Avm1ReachabilityAnalysis Reachability { get; }

    public Avm1IrreducibleControlFlowAnalysis IrreducibleControlFlow { get; }

    public Avm1DominatorTree DominatorTree { get; }

    public Avm1PostDominatorTree PostDominatorTree { get; }

    public Avm1RegionAnalysis RegionAnalysis { get; }

    public Avm1LoopAnalysis LoopAnalysis { get; }

    public Avm1StackDepthAnalysis StackDepthAnalysis { get; }

    public Avm1StackIr StackIr { get; }

    public Avm1TacIr TacIr { get; }

    public Avm1CompletionSsa CompletionSsa { get; }

    public Avm1CatchPayloadAnalysis CatchPayloads { get; }

    public Avm1RegisterSsa RegisterSsa { get; }

    public Avm1ConstantPoolAnalysis ConstantPools { get; }

    public Avm1ValueOriginAnalysis ValueOrigins { get; }

    public FunctionContext? Context { get; }
}
