using System.Globalization;

namespace ShockwaveFlash.Avm1.Decompilation.Ast;

public sealed class Avm1AstDebugEmitter
{
    private readonly TextWriter _writer;

    public Avm1AstDebugEmitter(TextWriter writer)
    {
        _writer = writer;
    }

    public void Write(Avm1AstArena ast)
    {
        if (!ast.Root.IsValid)
            return;

        WriteNode(ast, ast.Root, indent: 0);
    }

    private void WriteNode(Avm1AstArena ast, AstIndex index, int indent)
    {
        var node = ast[index];
        WriteIndent(indent);
        WriteNodeHeader(node);
        _writer.WriteLine();

        for (var i = 0; i < Avm1AstArena.GetChildCount(node); i++)
            WriteNode(ast, ast.GetChild(node, i), indent + 1);
    }

    private void WriteNodeHeader(Avm1AstNode node)
    {
        switch (node.Kind)
        {
            case Avm1AstNodeKind.Root:
                _writer.Write("root");
                break;
            case Avm1AstNodeKind.Block:
                _writer.Write("block ");
                WriteBlock(node);
                break;
            case Avm1AstNodeKind.If:
                _writer.Write("if ");
                WriteBlock(node);
                _writer.Write(" merge=");
                WriteBlockReference(node.Merge);
                break;
            case Avm1AstNodeKind.IfFrameLoaded:
                _writer.Write("if-frame-loaded ");
                WriteBlock(node);
                _writer.Write(" merge=");
                WriteBlockReference(node.Merge);
                break;
            case Avm1AstNodeKind.While:
                _writer.Write("while ");
                WriteBlock(node);
                _writer.Write(" exit=");
                WriteBlockReference(node.Merge);
                break;
            case Avm1AstNodeKind.DoWhile:
                _writer.Write("do-while ");
                WriteBlock(node);
                _writer.Write(" exit=");
                WriteBlockReference(node.Merge);
                break;
            case Avm1AstNodeKind.For:
                _writer.Write("for ");
                WriteBlock(node);
                _writer.Write(" exit=");
                WriteBlockReference(node.Merge);
                break;
            case Avm1AstNodeKind.ForIn:
                _writer.Write("for-in ");
                WriteBlock(node);
                _writer.Write(" exit=");
                WriteBlockReference(node.Merge);
                break;
            case Avm1AstNodeKind.With:
                _writer.Write("with ");
                WriteBlock(node);
                _writer.Write(" exit=");
                WriteBlockReference(node.Merge);
                break;
            case Avm1AstNodeKind.Try:
                _writer.Write("try ");
                WriteBlock(node);
                _writer.Write(" continuation=");
                WriteBlockReference(node.Merge);
                break;
            case Avm1AstNodeKind.Switch:
                _writer.Write("switch ");
                WriteBlock(node);
                _writer.Write(" merge=");
                WriteBlockReference(node.Merge);
                break;
            case Avm1AstNodeKind.SwitchCase:
                _writer.Write("switch-case ");
                WriteBlock(node);
                _writer.Write(" boundary=");
                WriteBlockReference(node.Merge);
                break;
            case Avm1AstNodeKind.SwitchDefault:
                _writer.Write("switch-default merge=");
                WriteBlockReference(node.Merge);
                break;
            case Avm1AstNodeKind.AssignRegister:
                _writer.Write("assign-register r");
                _writer.Write(node.Block.Value.ToString(CultureInfo.InvariantCulture));
                _writer.Write(" v");
                _writer.Write(node.Merge.Value.ToString(CultureInfo.InvariantCulture));
                break;
            case Avm1AstNodeKind.AssignTemp:
                _writer.Write("assign-temp v");
                _writer.Write(node.StartAction.Value.ToString(CultureInfo.InvariantCulture));
                break;
            case Avm1AstNodeKind.AssignVariable:
                _writer.Write("assign-variable");
                break;
            case Avm1AstNodeKind.AssignMember:
                _writer.Write(node.Merge.Value == 1 ? "assign-member-computed" : "assign-member");
                break;
            case Avm1AstNodeKind.CompoundAssignRegister:
                _writer.Write("compound-assign-register r");
                _writer.Write(node.Block.Value.ToString(CultureInfo.InvariantCulture));
                _writer.Write(" op=");
                _writer.Write(node.StartAction.Value.ToString(CultureInfo.InvariantCulture));
                break;
            case Avm1AstNodeKind.CompoundAssignVariable:
                _writer.Write("compound-assign-variable op=");
                _writer.Write(node.StartAction.Value.ToString(CultureInfo.InvariantCulture));
                break;
            case Avm1AstNodeKind.CompoundAssignMember:
                _writer.Write(node.Merge.Value == 1
                    ? "compound-assign-member-computed op="
                    : "compound-assign-member op=");
                _writer.Write(node.StartAction.Value.ToString(CultureInfo.InvariantCulture));
                break;
            case Avm1AstNodeKind.DeclareVariable:
                _writer.Write("declare-variable");
                break;
            case Avm1AstNodeKind.ExpressionStatement:
                _writer.Write("expression-statement");
                break;
            case Avm1AstNodeKind.Return:
                _writer.Write("return");
                break;
            case Avm1AstNodeKind.Throw:
                _writer.Write("throw");
                break;
            case Avm1AstNodeKind.TargetControl:
                _writer.Write("target-control ");
                _writer.Write(((ActionOpcode)node.StartAction.Value).ToString());
                break;
            case Avm1AstNodeKind.Break:
                _writer.Write("break");
                WriteControlTarget(node);
                break;
            case Avm1AstNodeKind.Continue:
                _writer.Write("continue");
                WriteControlTarget(node);
                break;
            case Avm1AstNodeKind.Literal:
                _writer.Write(node.StartAction.IsValid ? $"literal v{node.StartAction.Value.ToString(CultureInfo.InvariantCulture)}" : "literal undefined");
                break;
            case Avm1AstNodeKind.Variable:
                _writer.Write("variable");
                break;
            case Avm1AstNodeKind.Register:
                _writer.Write("register r");
                _writer.Write(node.Block.Value.ToString(CultureInfo.InvariantCulture));
                _writer.Write(" v");
                _writer.Write(node.Merge.Value.ToString(CultureInfo.InvariantCulture));
                break;
            case Avm1AstNodeKind.TempVar:
                _writer.Write("temp v");
                _writer.Write(node.StartAction.Value.ToString(CultureInfo.InvariantCulture));
                break;
            case Avm1AstNodeKind.Prefix:
                _writer.Write(node.StartAction.Value == (int)ActionOpcode.Increment
                    ? "prefix ++"
                    : "prefix --");
                break;
            case Avm1AstNodeKind.Postfix:
                _writer.Write(node.StartAction.Value == (int)ActionOpcode.Increment
                    ? "postfix ++"
                    : "postfix --");
                break;
            case Avm1AstNodeKind.Unary:
                _writer.Write(node.StartAction.Value switch
                {
                    1 => "unary !",
                    (int)ActionOpcode.Increment => "unary increment",
                    (int)ActionOpcode.Decrement => "unary decrement",
                    (int)ActionOpcode.TypeOf => "unary typeof",
                    _ => "unary ~"
                });
                break;
            case Avm1AstNodeKind.Binary:
                _writer.Write(node.Merge.Value == 1 ? "binary-inverted " : "binary ");
                _writer.Write(node.StartAction.Value.ToString(CultureInfo.InvariantCulture));
                break;
            case Avm1AstNodeKind.Conditional:
                _writer.Write("conditional");
                break;
            case Avm1AstNodeKind.MemberAccess:
                _writer.Write(node.Merge.Value == 1 ? "member-computed" : "member");
                break;
            case Avm1AstNodeKind.Delete:
                _writer.Write("delete");
                break;
            case Avm1AstNodeKind.Intrinsic:
                _writer.Write("intrinsic ");
                _writer.Write(node.StartAction.Value.ToString(CultureInfo.InvariantCulture));
                break;
            case Avm1AstNodeKind.ArrayLiteral:
                _writer.Write("array-literal");
                break;
            case Avm1AstNodeKind.ObjectLiteral:
                _writer.Write("object-literal");
                break;
            case Avm1AstNodeKind.NewObject:
                _writer.Write("new-object");
                break;
            case Avm1AstNodeKind.NewMethod:
                _writer.Write("new-method");
                break;
            case Avm1AstNodeKind.CallFunction:
                _writer.Write("call-function");
                break;
            case Avm1AstNodeKind.CallMethod:
                _writer.Write("call-method");
                break;
            case Avm1AstNodeKind.FunctionLiteral:
                _writer.Write("function-literal a");
                _writer.Write(node.StartAction.Value.ToString(CultureInfo.InvariantCulture));
                break;
        }
    }

    private void WriteControlTarget(in Avm1AstNode node)
    {
        if (!node.Block.IsValid)
            return;

        _writer.Write(" target=b");
        _writer.Write(node.Block.Value.ToString(CultureInfo.InvariantCulture));
    }

    private void WriteBlock(Avm1AstNode node)
    {
        _writer.Write("b");
        _writer.Write(node.Block.Value.ToString(CultureInfo.InvariantCulture));
        _writer.Write(" actions=");
        _writer.Write(node.StartAction.Value.ToString(CultureInfo.InvariantCulture));
        _writer.Write("..");
        _writer.Write(node.EndAction.Value.ToString(CultureInfo.InvariantCulture));
    }

    private void WriteBlockReference(BlockIndex block)
    {
        if (!block.IsValid)
        {
            _writer.Write("end");
            return;
        }

        _writer.Write(block.Value.ToString(CultureInfo.InvariantCulture));
    }

    private void WriteIndent(int indent)
    {
        for (var i = 0; i < indent; i++)
            _writer.Write("  ");
    }
}
