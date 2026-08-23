using ShockwaveFlash.Avm1.Decompilation.Ir;

namespace ShockwaveFlash.Avm1.Decompilation.Ast;

public sealed class Avm1AstArena
{
    internal const int SyntheticTrueLiteral = 1;

    private Avm1AstArena(AstIndex root, Avm1AstNode[] nodes, AstIndex[] children)
    {
        Root = root;
        Nodes = nodes;
        Children = children;
    }

    public AstIndex Root { get; }

    public IReadOnlyList<Avm1AstNode> Nodes { get; }

    public IReadOnlyList<AstIndex> Children { get; }

    public Avm1AstNode this[AstIndex index] => Nodes[index.Value];

    public static int GetChildCount(Avm1AstNode node) => node.Children.Count;

    public AstIndex GetChild(Avm1AstNode node, int childIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(childIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(childIndex, node.Children.Count);

        return Children[node.Children.Start + childIndex];
    }

    public IReadOnlyList<AstIndex> GetChildren(Avm1AstNode node)
    {
        if (node.Children.Count == 0)
            return [];

        var result = new AstIndex[node.Children.Count];
        for (var i = 0; i < result.Length; i++)
            result[i] = Children[node.Children.Start + i];

        return result;
    }

    internal sealed class Builder
    {
        private readonly List<Avm1AstNode> _nodes = [];
        private readonly List<AstIndex> _children = [];
        private readonly HashSet<int> _activeExpressionValues = [];

        public AstIndex AddNode(
            Avm1AstNodeKind kind,
            BlockIndex block = default,
            BlockIndex merge = default,
            ActionIndex startAction = default,
            ActionIndex endAction = default,
            IReadOnlyList<AstIndex>? children = null,
            int intOperand = 0)
        {
            var index = new AstIndex(_nodes.Count);
            var childList = AddChildren(children);
            _nodes.Add(new Avm1AstNode(
                index,
                kind,
                block,
                merge,
                startAction,
                endAction,
                intOperand,
                ActionIndex.Invalid,
                childList));
            return index;
        }

        public Avm1AstNode GetNode(AstIndex index) => _nodes[index.Value];

        public void UpdateNode(Avm1AstNode node) => _nodes[node.Index.Value] = node;

        public void SetOriginAction(AstIndex index, ActionIndex action)
        {
            var node = _nodes[index.Value];
            _nodes[index.Value] = node with { OriginAction = action };
        }

        public void AppendChild(AstIndex parent, AstIndex child)
        {
            var node = _nodes[parent.Value];
            var start = node.Children.Count == 0 ? _children.Count : node.Children.Start;
            if (node.Children.Count > 0 && node.Children.Start + node.Children.Count != _children.Count)
                throw new InvalidOperationException("AST children must be appended while the child span is at the arena tail.");

            _children.Add(child);
            _nodes[parent.Value] = node with
            {
                Children = new Avm1AstChildList(start, node.Children.Count + 1)
            };
        }

        public AstIndex GetChild(in Avm1AstNode node, int childIndex) => _children[node.Children.Start + childIndex];

        public bool TryEnterExpressionValue(int value) =>
            _activeExpressionValues.Add(value);

        public void ExitExpressionValue(int value) =>
            _activeExpressionValues.Remove(value);

        public Avm1AstArena ToArena(AstIndex root)
        {
            return new Avm1AstArena(root, _nodes.ToArray(), _children.ToArray());
        }

        private Avm1AstChildList AddChildren(IReadOnlyList<AstIndex>? children)
        {
            if (children is null || children.Count == 0)
                return new Avm1AstChildList(0, 0);

            var start = _children.Count;
            foreach (var child in children)
                _children.Add(child);

            return new Avm1AstChildList(start, children.Count);
        }
    }
}

public enum Avm1AstNodeKind
{
    Root,
    Block,
    If,
    IfFrameLoaded,
    While,
    DoWhile,
    For,
    ForIn,
    ForInitializerList,
    ForUpdateList,
    With,
    TargetControl,
    Try,
    Switch,
    SwitchCase,
    SwitchDefault,

    // --- STATEMENTS ---
    AssignRegister,
    AssignTemp,
    AssignVariable,
    AssignMember,
    CompoundAssignRegister,
    CompoundAssignVariable,
    CompoundAssignMember,
    DeclareVariable,
    ExpressionStatement,
    Return,
    Throw,
    Break,
    Continue,
    Opaque,

    // --- EXPRESSIONS ---
    Literal,
    Variable,
    Register,
    TempVar,
    Prefix,
    Postfix,
    Unary,
    Binary,
    Conditional,
    MemberAccess,
    Delete,
    Intrinsic,
    ArrayLiteral,
    ObjectLiteral,
    NewObject,
    NewMethod,
    CallFunction,
    CallMethod,
    FunctionLiteral
}

public readonly record struct Avm1AstNode(
    AstIndex Index,
    Avm1AstNodeKind Kind,
    BlockIndex Block,
    BlockIndex Merge,
    ActionIndex StartAction,
    ActionIndex EndAction,
    int IntOperand,
    ActionIndex OriginAction,
    Avm1AstChildList Children);

public readonly record struct Avm1AstChildList(int Start, int Count);
