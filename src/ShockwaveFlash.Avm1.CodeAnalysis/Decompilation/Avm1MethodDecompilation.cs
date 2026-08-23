using ShockwaveFlash.Avm1.Decompilation.Analysis;
using ShockwaveFlash.Avm1.Decompilation.Ast;
using ShockwaveFlash.Avm1.Decompilation.Emit;
using ShockwaveFlash.Avm1.Decompilation.Ir;
using ShockwaveFlash.Avm1.Decompilation.Ssa;
using ShockwaveFlash.Avm1.Source;
using System.Globalization;

namespace ShockwaveFlash.Avm1.Decompilation;

public sealed class Avm1MethodDecompilation
{
    internal Avm1MethodDecompilation(
        Avm1MethodCore core,
        Avm1ValueAnalysis valueAnalysis,
        Avm1SwitchAnalysis switchAnalysis,
        Avm1SymbolTable symbolTable,
        Avm1AstArena structuredAst,
        Avm1ClassTypeEnvironment? typeEnvironment,
        Avm1TimelineLayout? timelineLayout)
    {
        Core = core;
        ValueAnalysis = valueAnalysis;
        SwitchAnalysis = switchAnalysis;
        SymbolTable = symbolTable;
        StructuredAst = structuredAst;
        TypeEnvironment = typeEnvironment;
        TimelineLayout = timelineLayout;
    }

    public Avm1MethodCore Core { get; }

    public Avm1InstructionTable Instructions => Core.Instructions;

    public Avm1ControlFlowGraph ControlFlowGraph => Core.ControlFlowGraph;

    public Avm1FlowGraph FlowGraph => Core.FlowGraph;

    public Avm1ReachabilityAnalysis Reachability => Core.Reachability;

    public Avm1IrreducibleControlFlowAnalysis IrreducibleControlFlow =>
        Core.IrreducibleControlFlow;

    public Avm1DominatorTree DominatorTree => Core.DominatorTree;

    public Avm1PostDominatorTree PostDominatorTree => Core.PostDominatorTree;

    public Avm1RegionAnalysis RegionAnalysis => Core.RegionAnalysis;

    public Avm1LoopAnalysis LoopAnalysis => Core.LoopAnalysis;

    public Avm1StackDepthAnalysis StackDepthAnalysis => Core.StackDepthAnalysis;

    public Avm1StackIr StackIr => Core.StackIr;

    public Avm1TacIr TacIr => Core.TacIr;

    public Avm1CompletionSsa CompletionSsa => Core.CompletionSsa;

    public Avm1CatchPayloadAnalysis CatchPayloads => Core.CatchPayloads;

    public Avm1RegisterSsa RegisterSsa => Core.RegisterSsa;

    public Avm1ConstantPoolAnalysis ConstantPools => Core.ConstantPools;

    public Avm1ValueOriginAnalysis ValueOrigins => Core.ValueOrigins;

    public Avm1ValueAnalysis ValueAnalysis { get; }

    public Avm1SwitchAnalysis SwitchAnalysis { get; }

    public Avm1AstArena StructuredAst { get; }

    public Avm1SymbolTable SymbolTable { get; }

    public Avm1ClassTypeEnvironment? TypeEnvironment { get; }

    public Avm1TimelineLayout? TimelineLayout { get; }

    public void WriteStructuredAstDebug(TextWriter writer)
    {
        new Avm1AstDebugEmitter(writer).Write(StructuredAst);
    }

    public string GetStructuredAstDebugText()
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        WriteStructuredAstDebug(writer);
        return writer.ToString();
    }

    public void WriteStructuredAs2(TextWriter writer)
    {
        if (IrreducibleControlFlow.IsIrreducible ||
            StructuredAst.Nodes.Any(node =>
            node.Kind is Avm1AstNodeKind.TargetControl))
        {
            ProjectSource().WriteAs2(writer);
            return;
        }

        new Avm1StructuredAs2Emitter(writer).Write(this);
    }

    public string GetStructuredAs2Text()
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        WriteStructuredAs2(writer);
        return writer.ToString();
    }

    public Avm1SourceMethod ProjectSource()
    {
        return Avm1SourceProjector.ProjectMethod(this);
    }
}
