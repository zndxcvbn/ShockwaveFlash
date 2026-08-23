using System.Buffers;
using System.Globalization;
using System.Text;
using ShockwaveFlash.Avm1.Decompilation.Analysis;
using ShockwaveFlash.Avm1.Decompilation.Ast;
using ShockwaveFlash.Avm1.Decompilation.Ir;
using ShockwaveFlash.Avm1.Decompilation.Ssa;
using ShockwaveFlash.Avm1.Swf1;
using ShockwaveFlash.Avm1.Swf3;
using ShockwaveFlash.Avm1.Swf4;
using ShockwaveFlash.Avm1.Swf5;
using ShockwaveFlash.Avm1.Swf7;
using ShockwaveFlash.Avm1.Text;
using ShockwaveFlash.Avm1.Types;

namespace ShockwaveFlash.Avm1.Decompilation.Emit;

public sealed class Avm1StructuredAs2Emitter
{
    private readonly TextWriter _writer;
    private Avm1MethodDecompilation? _method;
    private Avm1AstArena? _ast;
    private byte[]? _controlLabelMetadata;
    private int _controlLabelMetadataLength;
    private byte[]? _symbolDeclarationMetadata;
    private int _symbolDeclarationMetadataLength;
    private int _indent;
    private readonly Dictionary<int, string> _registerAliases = [];
    private readonly Dictionary<(int Register, int Version), string> _registerVersionAliases = [];

    public Avm1StructuredAs2Emitter(TextWriter writer, int initialIndent = 0)
    {
        _writer = writer;
        _indent = initialIndent;
    }

    public void Write(Avm1MethodDecompilation method)
    {
        _method = method;
        _ast = method.StructuredAst;
        _registerAliases.Clear();
        _registerVersionAliases.Clear();
        try
        {
            PrepareControlLabels(method.ControlFlowGraph.Count);
            PrepareSymbolDeclarations(method);
            WriteNode(_ast.Root);
        }
        finally
        {
            if (_controlLabelMetadata is not null)
                ArrayPool<byte>.Shared.Return(_controlLabelMetadata, clearArray: true);
            if (_symbolDeclarationMetadata is not null)
                ArrayPool<byte>.Shared.Return(_symbolDeclarationMetadata, clearArray: true);
            _controlLabelMetadata = null;
            _controlLabelMetadataLength = 0;
            _symbolDeclarationMetadata = null;
            _symbolDeclarationMetadataLength = 0;
            _registerAliases.Clear();
            _registerVersionAliases.Clear();
        }
    }

    internal static string GetExpressionText(
        Avm1MethodDecompilation method,
        AstIndex expression,
        IReadOnlyDictionary<(int Register, int Version), string>? registerAliases = null)
    {
        ArgumentNullException.ThrowIfNull(method);
        if (!expression.IsValid)
            throw new ArgumentOutOfRangeException(nameof(expression));

        var emitter = new Avm1StructuredAs2Emitter(TextWriter.Null)
        {
            _method = method,
            _ast = method.StructuredAst
        };
        try
        {
            emitter.PrepareSymbolDeclarations(method);
            if (registerAliases is not null)
            {
                foreach (var alias in registerAliases)
                    emitter._registerVersionAliases.Add(alias.Key, alias.Value);
            }
            return emitter.FormatExpression(expression);
        }
        finally
        {
            if (emitter._symbolDeclarationMetadata is not null)
            {
                ArrayPool<byte>.Shared.Return(
                    emitter._symbolDeclarationMetadata,
                    clearArray: true);
            }

            emitter._symbolDeclarationMetadata = null;
            emitter._symbolDeclarationMetadataLength = 0;
            emitter._registerVersionAliases.Clear();
        }
    }

    private void PrepareSymbolDeclarations(Avm1MethodDecompilation method)
    {
        var symbolCapacity = method.SymbolTable.Symbols.Count + method.StructuredAst.Nodes.Count;
        if (symbolCapacity == 0)
            return;

        _symbolDeclarationMetadata = ArrayPool<byte>.Shared.Rent(symbolCapacity);
        _symbolDeclarationMetadataLength = symbolCapacity;
        Array.Clear(_symbolDeclarationMetadata, 0, symbolCapacity);
        foreach (var symbol in method.SymbolTable.Symbols)
        {
            if (symbol.IsDeclared)
                _symbolDeclarationMetadata[symbol.Id] = 1;
        }
    }

    private bool IsSymbolDeclared(Avm1Symbol symbol) =>
        _symbolDeclarationMetadata is not null &&
        (uint)symbol.Id < (uint)_symbolDeclarationMetadataLength
            ? _symbolDeclarationMetadata[symbol.Id] != 0
            : symbol.IsDeclared;

    private void DeclareSymbol(Avm1Symbol symbol)
    {
        if (_symbolDeclarationMetadata is not null &&
            (uint)symbol.Id < (uint)_symbolDeclarationMetadataLength)
        {
            _symbolDeclarationMetadata[symbol.Id] = 1;
        }
    }

    private void PrepareControlLabels(int blockCount)
    {
        if (blockCount == 0)
            return;

        const byte labelRequired = 1 << 0;
        const byte scopeExists = 1 << 1;
        const byte switchScope = 1 << 2;
        _controlLabelMetadata = ArrayPool<byte>.Shared.Rent(blockCount);
        _controlLabelMetadataLength = blockCount;
        Array.Clear(_controlLabelMetadata, 0, blockCount);

        foreach (var node in _ast!.Nodes)
        {
            if (!node.Block.IsValid || node.Block.Value >= blockCount)
                continue;

            if (node.Kind is Avm1AstNodeKind.Break or Avm1AstNodeKind.Continue)
                _controlLabelMetadata[node.Block.Value] |= labelRequired;
            else if (IsControlScope(node.Kind))
                _controlLabelMetadata[node.Block.Value] |= (byte)(scopeExists |
                    (node.Kind is Avm1AstNodeKind.Switch ? switchScope : 0));
        }
    }

    private void WriteNode(AstIndex index)
    {
        var node = _ast![index];
        switch (node.Kind)
        {
            case Avm1AstNodeKind.Root:
                WriteChildren(node);
                break;
            case Avm1AstNodeKind.Block:
                WriteBasicBlock(node);
                break;
            case Avm1AstNodeKind.If:
                WriteIf(node);
                break;
            case Avm1AstNodeKind.IfFrameLoaded:
                WriteIfFrameLoaded(node);
                break;
            case Avm1AstNodeKind.While:
                WriteWhile(node);
                break;
            case Avm1AstNodeKind.DoWhile: // Добавлено
                WriteDoWhile(node);
                break;
            case Avm1AstNodeKind.For:
                WriteFor(node);
                break;
            case Avm1AstNodeKind.ForIn:
                WriteForIn(node);
                break;
            case Avm1AstNodeKind.With: // Добавлено
                WriteWith(node);
                break;
            case Avm1AstNodeKind.Try: // Добавлено
                WriteTry(node);
                break;
            case Avm1AstNodeKind.Switch: // Добавлено
                WriteSwitch(node);
                break;
            case Avm1AstNodeKind.Break:
                WriteControlJump("break", node);
                break;
            case Avm1AstNodeKind.Continue:
                WriteControlJump("continue", node);
                break;
        }
    }

    private void WriteDoWhile(Avm1AstNode node)
    {
        var condIndex = _ast!.GetChild(node, 0);

        WriteControlLabelIfNeeded(node);
        WriteIndent();
        _writer.WriteLine("do");
        WriteIndent();
        _writer.WriteLine("{");
        _indent++;
        for (int i = 1; i < Avm1AstArena.GetChildCount(node); i++)
        {
            WriteNode(_ast.GetChild(node, i));
        }
        _indent--;
        WriteIndent();
        _writer.Write("} while (");
        _writer.Write(FormatExpression(condIndex));
        _writer.WriteLine(");");
    }

    private void WriteWith(Avm1AstNode node)
    {
        var scopeIndex = _ast!.GetChild(node, 0);
        var bodyBlock = _ast.GetChild(node, 1);

        WriteIndent();
        _writer.Write("with (");
        _writer.Write(FormatExpression(scopeIndex));
        _writer.WriteLine(")");
        WriteBlock(bodyBlock);
    }

    private void WriteTry(Avm1AstNode node)
    {
        var tryBlock = _ast!.GetChild(node, 0);
        var tryAction = GetMethod().Instructions[node.StartAction].Action as ActionTry;
        if (tryAction is null)
            return;
        var hasCatch = tryAction.Flags.HasFlag(TryFlags.CatchBlock) || !tryAction.CatchBody.IsEmpty;
        var hasFinally = tryAction.Flags.HasFlag(TryFlags.FinallyBlock) || !tryAction.FinallyBody.IsEmpty;

        WriteIndent();
        _writer.WriteLine("try");
        WriteBlock(tryBlock);

        int currentChild = 1;
        if (hasCatch && currentChild < Avm1AstArena.GetChildCount(node))
        {
            var catchBlock = _ast.GetChild(node, currentChild++);
            var catchInRegister = tryAction.Flags.HasFlag(TryFlags.CatchInRegister);
            var catchRegister = catchInRegister ? tryAction.CatchRegister : -1;
            var catchVarName = catchInRegister
                ? GetCatchRegisterName(node.StartAction, catchRegister)
                : IsIdentifier(tryAction.CatchVariable)
                    ? tryAction.CatchVariable
                    : "_catch_";
            var hadAlias = false;
            string? previousAlias = null;
            if (catchInRegister)
            {
                hadAlias = _registerAliases.TryGetValue(catchRegister, out previousAlias);
                _registerAliases[catchRegister] = catchVarName;
            }

            WriteIndent();
            _writer.Write("catch (");
            _writer.Write(catchVarName);
            _writer.WriteLine(")");
            WriteBlock(catchBlock);

            if (catchInRegister)
            {
                if (hadAlias)
                    _registerAliases[catchRegister] = previousAlias!;
                else
                    _registerAliases.Remove(catchRegister);
            }
        }

        if (hasFinally && currentChild < Avm1AstArena.GetChildCount(node))
        {
            var finallyBlock = _ast.GetChild(node, currentChild);
            WriteIndent();
            _writer.WriteLine("finally");
            WriteBlock(finallyBlock);
        }
    }

    private string GetCatchRegisterName(ActionIndex tryAction, int catchRegister)
    {
        var method = GetMethod();
        foreach (var region in method.Instructions.TryRegions)
        {
            if (region.EnterAction != tryAction)
                continue;

            foreach (var access in method.RegisterSsa.Accesses)
            {
                if (access.Action != region.CatchEnterAction ||
                    access.Register != catchRegister ||
                    access.Kind is not Avm1RegisterAccessKind.Write)
                {
                    continue;
                }

                if (method.SymbolTable.TryGetRegisterSymbol(
                    access.Register,
                    access.Version,
                    out var symbol))
                {
                    return symbol.Name;
                }
            }

            break;
        }

        return $"_loc{catchRegister.ToString(CultureInfo.InvariantCulture)}_";
    }

    private void WriteSwitch(Avm1AstNode node)
    {
        var valueIndex = _ast!.GetChild(node, 0);
        WriteControlLabelIfNeeded(node);
        WriteIndent();
        _writer.Write("switch (");
        _writer.Write(FormatExpression(valueIndex));
        _writer.WriteLine(")");

        WriteIndent();
        _writer.WriteLine("{");
        _indent++;

        var childCount = Avm1AstArena.GetChildCount(node);
        for (var i = 1; i < childCount; i++)
        {
            var labelIndex = _ast.GetChild(node, i);
            var label = _ast[labelIndex];
            if (label.Kind is Avm1AstNodeKind.SwitchCase)
            {
                var caseValue = _ast.GetChild(label, 0);
                WriteIndent();
                _writer.Write("case ");
                _writer.Write(FormatExpression(caseValue));
                _writer.WriteLine(":");

                if (Avm1AstArena.GetChildCount(label) > 1)
                {
                    _indent++;
                    WriteNode(_ast.GetChild(label, 1));
                    _indent--;
                }
                continue;
            }

            if (label.Kind is Avm1AstNodeKind.SwitchDefault)
            {
                WriteIndent();
                _writer.WriteLine("default:");
                if (Avm1AstArena.GetChildCount(label) > 0)
                {
                    _indent++;
                    WriteNode(_ast.GetChild(label, 0));
                    _indent--;
                }
            }
        }

        _indent--;
        WriteIndent();
        _writer.WriteLine("}");
    }

    private void WriteChildren(Avm1AstNode node)
    {
        for (var i = 0; i < Avm1AstArena.GetChildCount(node); i++)
            WriteNode(_ast!.GetChild(node, i));
    }

    private void WriteControlLabelIfNeeded(in Avm1AstNode node)
    {
        if (!node.Block.IsValid || !NeedsControlLabel(node.Block))
            return;

        WriteIndent();
        WriteControlLabel(node.Kind, node.Block);
        _writer.WriteLine(":");
    }

    private bool NeedsControlLabel(BlockIndex target)
    {
        const byte labelRequired = 1 << 0;
        return target.IsValid &&
            target.Value < _controlLabelMetadataLength &&
            (_controlLabelMetadata![target.Value] & labelRequired) != 0;
    }

    private void WriteControlJump(string keyword, in Avm1AstNode node)
    {
        WriteIndent();
        _writer.Write(keyword);
        if (node.Block.IsValid)
        {
            _writer.Write(' ');
            WriteControlLabel(FindControlTargetKind(node.Block), node.Block);
        }
        _writer.WriteLine(";");
    }

    private Avm1AstNodeKind FindControlTargetKind(BlockIndex target)
    {
        const byte scopeExists = 1 << 1;
        const byte switchScope = 1 << 2;
        if (target.IsValid && target.Value < _controlLabelMetadataLength)
        {
            var metadata = _controlLabelMetadata![target.Value];
            if ((metadata & scopeExists) != 0)
                return (metadata & switchScope) != 0
                    ? Avm1AstNodeKind.Switch
                    : Avm1AstNodeKind.While;
        }

        throw new InvalidOperationException($"AST control target b{target.Value} was not found.");
    }

    private static bool IsControlScope(Avm1AstNodeKind kind) => kind is
        Avm1AstNodeKind.While or
        Avm1AstNodeKind.DoWhile or
        Avm1AstNodeKind.For or
        Avm1AstNodeKind.ForIn or
        Avm1AstNodeKind.Switch;

    private void WriteControlLabel(Avm1AstNodeKind kind, BlockIndex header)
    {
        _writer.Write(kind is Avm1AstNodeKind.Switch ? "switch_b" : "loop_b");
        _writer.Write(header.Value);
    }

    private void WriteIf(Avm1AstNode node, bool writeIndent = true)
    {
        var condIndex = _ast!.GetChild(node, 0);
        var firstChild = _ast.GetChild(node, 1);

        if (writeIndent)
            WriteIndent();
        _writer.Write("if (");
        _writer.Write(FormatExpression(condIndex));
        _writer.WriteLine(")");
        WriteBlock(firstChild);

        if (Avm1AstArena.GetChildCount(node) <= 2)
            return;

        var secondChild = _ast.GetChild(node, 2);

        WriteIndent();
        if (TryGetSingleIf(secondChild, out var nestedIf))
        {
            _writer.Write("else ");
            WriteIf(nestedIf, writeIndent: false);
            return;
        }

        _writer.WriteLine("else");
        WriteBlock(secondChild);
    }

    private void WriteIfFrameLoaded(Avm1AstNode node)
    {
        if (!node.OriginAction.IsValid ||
            node.OriginAction.Value >= GetMethod().Instructions.Count)
        {
            return;
        }

        string frame;
        AstIndex body;
        switch (GetMethod().Instructions[node.OriginAction].Action)
        {
            case ActionWaitForFrame wait when node.Children.Count == 1:
                frame = (wait.Frame + 1).ToString(CultureInfo.InvariantCulture);
                body = _ast!.GetChild(node, 0);
                break;
            case ActionWaitForFrame2 when node.Children.Count == 2:
                frame = FormatExpression(_ast!.GetChild(node, 0));
                body = _ast.GetChild(node, 1);
                break;
            default:
                return;
        }

        WriteIndent();
        _writer.Write("ifFrameLoaded(");
        _writer.Write(frame);
        _writer.WriteLine(")");
        WriteBlock(body);
    }

    private bool TryGetSingleIf(AstIndex branch, out Avm1AstNode nestedIf)
    {
        nestedIf = default;
        if (!branch.IsValid)
            return false;

        var branchNode = _ast![branch];
        if (branchNode.Kind is Avm1AstNodeKind.If)
        {
            nestedIf = branchNode;
            return true;
        }

        if (branchNode.Kind is not Avm1AstNodeKind.Block || Avm1AstArena.GetChildCount(branchNode) != 1)
            return false;

        var child = _ast.GetChild(branchNode, 0);
        var childNode = _ast[child];
        if (childNode.Kind is not Avm1AstNodeKind.If)
            return false;

        nestedIf = childNode;
        return true;
    }

    private void WriteWhile(Avm1AstNode node)
    {
        var condIndex = _ast!.GetChild(node, 0);

        WriteControlLabelIfNeeded(node);
        WriteIndent();
        _writer.Write("while (");
        _writer.Write(FormatExpression(condIndex));
        _writer.WriteLine(")");

        WriteIndent();
        _writer.WriteLine("{");
        _indent++;
        for (int i = 1; i < Avm1AstArena.GetChildCount(node); i++)
        {
            WriteNode(_ast.GetChild(node, i));
        }
        _indent--;
        WriteIndent();
        _writer.WriteLine("}");
    }

    private void WriteFor(Avm1AstNode node) // Добавлено
    {
        var initializerClause = _ast!.GetChild(node, 0);
        var condIndex = _ast.GetChild(node, 1);
        var updateClause = _ast.GetChild(node, 2);
        var aliasRegisters = GetForCounterRegisters(initializerClause, updateClause);
        var previousAliases = new Dictionary<int, string?>();

        foreach (var aliasRegister in aliasRegisters)
        {
            previousAliases[aliasRegister] = _registerAliases.TryGetValue(
                aliasRegister,
                out var previousAlias)
                ? previousAlias
                : null;
            _registerAliases[aliasRegister] = $"_loc{aliasRegister.ToString(CultureInfo.InvariantCulture)}_";
        }

        WriteControlLabelIfNeeded(node);
        WriteIndent();
        _writer.Write("for (");
        _writer.Write(FormatForInitializerClause(initializerClause));
        _writer.Write("; ");
        _writer.Write(FormatExpression(condIndex));
        _writer.Write("; ");
        _writer.Write(FormatForUpdateClause(updateClause));
        _writer.WriteLine(")");

        WriteIndent();
        _writer.WriteLine("{");
        _indent++;
        for (int i = 3; i < Avm1AstArena.GetChildCount(node); i++)
        {
            WriteNode(_ast.GetChild(node, i));
        }
        _indent--;
        WriteIndent();
        _writer.WriteLine("}");

        foreach (var aliasRegister in aliasRegisters)
        {
            if (previousAliases[aliasRegister] is { } previousAlias)
                _registerAliases[aliasRegister] = previousAlias;
            else
                _registerAliases.Remove(aliasRegister);
        }
    }

    private void WriteForIn(Avm1AstNode node)
    {
        var keyIndex = _ast!.GetChild(node, 0);
        var collectionIndex = _ast.GetChild(node, 1);
        var keyNode = _ast[keyIndex];
        var aliasRegister = keyNode.Kind is Avm1AstNodeKind.Register ? keyNode.Block.Value : -1;
        var hadAlias = false;
        string? previousAlias = null;

        if (aliasRegister >= 0)
        {
            hadAlias = _registerAliases.TryGetValue(aliasRegister, out previousAlias);
            _registerAliases[aliasRegister] = $"_loc{aliasRegister.ToString(CultureInfo.InvariantCulture)}_";
            if (keyNode.Merge.IsValid &&
                GetMethod().SymbolTable.TryGetRegisterSymbol(
                    aliasRegister,
                    keyNode.Merge.Value,
                    out var symbol))
            {
                DeclareSymbol(symbol);
            }
        }

        WriteControlLabelIfNeeded(node);
        WriteIndent();
        _writer.Write("for (");
        if (aliasRegister >= 0)
        {
            _writer.Write("var ");
            _writer.Write(_registerAliases[aliasRegister]);
        }
        else
        {
            _writer.Write(FormatExpression(keyIndex));
        }
        _writer.Write(" in ");
        _writer.Write(FormatExpression(collectionIndex));
        _writer.WriteLine(")");

        WriteIndent();
        _writer.WriteLine("{");
        _indent++;
        for (var i = 2; i < Avm1AstArena.GetChildCount(node); i++)
            WriteNode(_ast.GetChild(node, i));
        _indent--;
        WriteIndent();
        _writer.WriteLine("}");

        if (aliasRegister >= 0)
        {
            if (hadAlias)
                _registerAliases[aliasRegister] = previousAlias!;
            else
                _registerAliases.Remove(aliasRegister);
        }
    }

    private int[] GetForCounterRegisters(AstIndex initializerClauseIndex, AstIndex updateClauseIndex)
    {
        if (!initializerClauseIndex.IsValid || !updateClauseIndex.IsValid)
            return [];

        var initializerClause = _ast![initializerClauseIndex];
        var updateClause = _ast[updateClauseIndex];
        if (initializerClause.Kind is not Avm1AstNodeKind.ForInitializerList ||
            updateClause.Kind is not Avm1AstNodeKind.ForUpdateList)
        {
            return [];
        }

        var initializedRegisters = new HashSet<int>();
        for (var i = 0; i < initializerClause.Children.Count; i++)
        {
            var initializer = _ast[_ast.GetChild(initializerClause, i)];
            if (initializer.Kind is Avm1AstNodeKind.AssignRegister)
                initializedRegisters.Add(initializer.Block.Value);
        }

        var result = new List<int>();
        for (var i = 0; i < updateClause.Children.Count; i++)
        {
            var update = _ast[_ast.GetChild(updateClause, i)];
            if ((update.Kind is Avm1AstNodeKind.AssignRegister or
                    Avm1AstNodeKind.CompoundAssignRegister) &&
                initializedRegisters.Contains(update.Block.Value) &&
                !result.Contains(update.Block.Value))
            {
                result.Add(update.Block.Value);
            }
        }
        result.Sort();
        return result.ToArray();
    }

    private string FormatForInitializerClause(AstIndex clauseIndex)
    {
        var clause = _ast![clauseIndex];
        if (clause.Kind is not Avm1AstNodeKind.ForInitializerList)
            throw new InvalidOperationException("For initializer clause has an invalid AST node kind.");
        if (clause.Children.Count == 0)
            return string.Empty;

        var parts = new string[clause.Children.Count];
        var declarationCount = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            parts[i] = FormatForPart(_ast.GetChild(clause, i), declareRegisterAlias: true);
            if (parts[i].StartsWith("var ", StringComparison.Ordinal))
                declarationCount++;
        }

        if (declarationCount == parts.Length)
        {
            for (var i = 0; i < parts.Length; i++)
                parts[i] = parts[i][4..];
            return "var " + string.Join(", ", parts);
        }
        if (declarationCount != 0)
            throw new InvalidOperationException("For initializer clause mixes declarations and expressions.");
        return string.Join(", ", parts);
    }

    private string FormatForUpdateClause(AstIndex clauseIndex)
    {
        var clause = _ast![clauseIndex];
        if (clause.Kind is not Avm1AstNodeKind.ForUpdateList)
            throw new InvalidOperationException("For update clause has an invalid AST node kind.");

        var parts = new string[clause.Children.Count];
        for (var i = 0; i < parts.Length; i++)
            parts[i] = FormatForPart(_ast.GetChild(clause, i), declareRegisterAlias: false);
        return string.Join(", ", parts);
    }

    private bool TryGetSelfRegisterStep(
        Avm1AstNode assignment,
        AstIndex valueIndex,
        out string stepOperator)
    {
        stepOperator = string.Empty;
        var value = _ast![valueIndex];
        var opcode = (ActionOpcode)value.StartAction.Value;
        if (value.Kind is not Avm1AstNodeKind.Unary ||
            opcode is not (ActionOpcode.Increment or ActionOpcode.Decrement) ||
            Avm1AstArena.GetChildCount(value) != 1)
        {
            return false;
        }

        var operand = _ast[_ast.GetChild(value, 0)];
        if (operand.Kind is not Avm1AstNodeKind.Register || operand.Block != assignment.Block)
            return false;

        stepOperator = opcode is ActionOpcode.Increment ? "++" : "--";
        return true;
    }

    private string FormatForPart(AstIndex index, bool declareRegisterAlias)
    {
        if (!index.IsValid)
            return string.Empty;

        var node = _ast![index];
        switch (node.Kind)
        {
            case Avm1AstNodeKind.AssignRegister:
                {
                    int reg = node.Block.Value;
                    int version = node.Merge.Value;
                    var valIndex = _ast.GetChild(node, 0);
                    string varName;

                    if (_registerAliases.TryGetValue(reg, out var alias))
                    {
                        if (declareRegisterAlias &&
                            GetMethod().SymbolTable.TryGetRegisterSymbol(reg, version, out var aliasSymbol))
                        {
                            DeclareSymbol(aliasSymbol);
                        }

                        if (!declareRegisterAlias &&
                            TryGetSelfRegisterStep(node, valIndex, out var stepOperator))
                        {
                            return $"{alias}{stepOperator}";
                        }

                        return $"{(declareRegisterAlias ? "var " : string.Empty)}{alias} = {FormatExpression(valIndex)}";
                    }
                    else if (GetMethod().SymbolTable.TryGetRegisterSymbol(reg, version, out var symbol))
                    {
                        if (!IsSymbolDeclared(symbol))
                        {
                            DeclareSymbol(symbol);
                            return $"var {symbol.Name} = {FormatExpression(valIndex)}";
                        }
                        varName = symbol.Name;
                    }
                    else
                    {
                        varName = $"_loc{reg.ToString(CultureInfo.InvariantCulture)}_v{version}";
                    }
                    return $"{varName} = {FormatExpression(valIndex)}";
                }
            case Avm1AstNodeKind.AssignVariable:
                {
                    var nameIndex = _ast.GetChild(node, 0);
                    var valIndex = _ast.GetChild(node, 1);
                    return $"{FormatVariableTarget(nameIndex)} = {FormatExpression(valIndex)}";
                }
            case Avm1AstNodeKind.CompoundAssignRegister:
                {
                    var reg = node.Block.Value;
                    var version = node.Merge.Value;
                    var value = FormatExpression(_ast.GetChild(node, 0));
                    var op = GetCompoundAssignmentSymbol((ActionOpcode)node.StartAction.Value);
                    if (_registerAliases.TryGetValue(reg, out var alias))
                    {
                        if (declareRegisterAlias &&
                            GetMethod().SymbolTable.TryGetRegisterSymbol(reg, version, out var aliasSymbol))
                        {
                            DeclareSymbol(aliasSymbol);
                        }

                        return declareRegisterAlias
                            ? $"var {alias} = {alias} {op[..^1]} {value}"
                            : $"{alias} {op} {value}";
                    }

                    if (GetMethod().SymbolTable.TryGetRegisterSymbol(reg, version, out var symbol))
                    {
                        if (!IsSymbolDeclared(symbol))
                        {
                            DeclareSymbol(symbol);
                            return $"var {symbol.Name} = {symbol.Name} {op[..^1]} {value}";
                        }

                        return $"{symbol.Name} {op} {value}";
                    }

                    var registerName = $"_loc{reg.ToString(CultureInfo.InvariantCulture)}_v{version}";
                    return $"{registerName} {op} {value}";
                }
            case Avm1AstNodeKind.CompoundAssignVariable:
                {
                    var nameIndex = _ast.GetChild(node, 0);
                    var valIndex = _ast.GetChild(node, 1);
                    var op = GetCompoundAssignmentSymbol((ActionOpcode)node.StartAction.Value);
                    return $"{FormatVariableTarget(nameIndex)} {op} {FormatExpression(valIndex)}";
                }
            case Avm1AstNodeKind.DeclareVariable:
                {
                    var name = FormatVariableTarget(_ast.GetChild(node, 0));
                    return node.Children.Count > 1
                        ? $"var {name} = {FormatExpression(_ast.GetChild(node, 1))}"
                        : $"var {name}";
                }
            default:
                return FormatExpression(index);
        }
    }

    private void WriteBlock(AstIndex child)
    {
        WriteIndent();
        _writer.WriteLine("{");
        _indent++;
        WriteNode(child);
        _indent--;
        WriteIndent();
        _writer.WriteLine("}");
    }

    private void WriteBasicBlock(Avm1AstNode node)
    {
        for (int i = 0; i < Avm1AstArena.GetChildCount(node); i++)
        {
            var stmtIndex = _ast!.GetChild(node, i);
            var stmt = _ast[stmtIndex];
            WriteStatement(stmt);
        }
    }

    private void WriteStatement(in Avm1AstNode stmt)
    {
        if (stmt.Kind >= Avm1AstNodeKind.AssignRegister)
            WriteNestedRegisterDeclarations(stmt);
        switch (stmt.Kind)
        {
            case Avm1AstNodeKind.Block:
                WriteBasicBlock(stmt);
                break;
            case Avm1AstNodeKind.If:
                WriteIf(stmt);
                break;
            case Avm1AstNodeKind.IfFrameLoaded:
                WriteIfFrameLoaded(stmt);
                break;
            case Avm1AstNodeKind.While:
                WriteWhile(stmt);
                break;
            case Avm1AstNodeKind.DoWhile:
                WriteDoWhile(stmt);
                break;
            case Avm1AstNodeKind.For:
                WriteFor(stmt);
                break;
            case Avm1AstNodeKind.ForIn:
                WriteForIn(stmt);
                break;
            case Avm1AstNodeKind.With:
                WriteWith(stmt);
                break;
            case Avm1AstNodeKind.Try:
                WriteTry(stmt);
                break;
            case Avm1AstNodeKind.Switch:
                WriteSwitch(stmt);
                break;
            case Avm1AstNodeKind.Break:
                WriteControlJump("break", stmt);
                break;
            case Avm1AstNodeKind.Continue:
                WriteControlJump("continue", stmt);
                break;
            case Avm1AstNodeKind.AssignRegister:
                {
                    WriteIndent();
                    int reg = stmt.Block.Value;
                    int version = stmt.Merge.Value;
                    var valIndex = _ast!.GetChild(stmt, 0);

                    if (_registerAliases.TryGetValue(reg, out var alias))
                    {
                        if (TryGetSelfRegisterStep(stmt, valIndex, out var stepOperator))
                        {
                            _writer.Write(alias);
                            _writer.Write(stepOperator);
                        }
                        else
                        {
                            _writer.Write(alias);
                            _writer.Write(" = ");
                            _writer.Write(FormatExpression(valIndex));
                        }
                        _writer.WriteLine(";");
                        break;
                    }

                    string varName;
                    if (GetMethod().SymbolTable.TryGetRegisterSymbol(reg, version, out var symbol))
                    {
                        if (!IsSymbolDeclared(symbol))
                        {
                            DeclareSymbol(symbol);
                            if (IsUndefinedExpression(valIndex))
                            {
                                _writer.Write("var ");
                                _writer.Write(symbol.Name);
                                _writer.WriteLine(";");
                                break;
                            }

                            _writer.Write("var ");
                        }
                        varName = symbol.Name;
                    }
                    else
                    {
                        varName = $"_loc{reg.ToString(CultureInfo.InvariantCulture)}_v{version}";
                    }

                    _writer.Write(varName);
                    _writer.Write(" = ");
                    _writer.Write(FormatExpression(valIndex));
                    _writer.WriteLine(";");
                    break;
                }
            case Avm1AstNodeKind.AssignTemp:
                {
                    WriteIndent();
                    var value = new ValueIndex(stmt.StartAction.Value);
                    var valIndex = _ast!.GetChild(stmt, 0);
                    var symbol = GetMethod().SymbolTable.GetOrCreateTempSymbol(value, GetMethod().ValueAnalysis[value].Type);
                    if (!IsSymbolDeclared(symbol))
                    {
                        _writer.Write("var ");
                        DeclareSymbol(symbol);
                    }

                    _writer.Write(symbol.Name);
                    _writer.Write(" = ");
                    _writer.Write(FormatExpression(valIndex));
                    _writer.WriteLine(";");
                    break;
                }
            case Avm1AstNodeKind.CompoundAssignRegister:
                {
                    WriteIndent();
                    var reg = stmt.Block.Value;
                    var version = stmt.Merge.Value;
                    var valIndex = _ast!.GetChild(stmt, 0);
                    var op = GetCompoundAssignmentSymbol((ActionOpcode)stmt.StartAction.Value);
                    if (_registerAliases.TryGetValue(reg, out var alias))
                    {
                        _writer.Write(alias);
                        _writer.Write(' ');
                        _writer.Write(op);
                        _writer.Write(' ');
                        _writer.Write(FormatExpression(valIndex));
                        _writer.WriteLine(";");
                        break;
                    }

                    string varName;
                    var declare = false;
                    if (GetMethod().SymbolTable.TryGetRegisterSymbol(reg, version, out var symbol))
                    {
                        declare = !IsSymbolDeclared(symbol);
                        DeclareSymbol(symbol);
                        varName = symbol.Name;
                    }
                    else
                    {
                        varName = $"_loc{reg.ToString(CultureInfo.InvariantCulture)}_v{version}";
                    }

                    if (declare)
                    {
                        _writer.Write("var ");
                        _writer.Write(varName);
                        _writer.Write(" = ");
                        _writer.Write(varName);
                        _writer.Write(' ');
                        _writer.Write(op[..^1]);
                    }
                    else
                    {
                        _writer.Write(varName);
                        _writer.Write(' ');
                        _writer.Write(op);
                    }
                    _writer.Write(' ');
                    _writer.Write(FormatExpression(valIndex));
                    _writer.WriteLine(";");
                    break;
                }
            case Avm1AstNodeKind.AssignVariable:
                {
                    WriteIndent();
                    var nameIndex = _ast!.GetChild(stmt, 0);
                    var valIndex = _ast.GetChild(stmt, 1);
                    _writer.Write(FormatVariableTarget(nameIndex));
                    _writer.Write(" = ");
                    _writer.Write(FormatExpression(valIndex));
                    _writer.WriteLine(";");
                    break;
                }
            case Avm1AstNodeKind.CompoundAssignVariable:
                {
                    WriteIndent();
                    var nameIndex = _ast!.GetChild(stmt, 0);
                    var valIndex = _ast.GetChild(stmt, 1);
                    _writer.Write(FormatVariableTarget(nameIndex));
                    _writer.Write(' ');
                    _writer.Write(GetCompoundAssignmentSymbol((ActionOpcode)stmt.StartAction.Value));
                    _writer.Write(' ');
                    _writer.Write(FormatExpression(valIndex));
                    _writer.WriteLine(";");
                    break;
                }
            case Avm1AstNodeKind.AssignMember:
                {
                    WriteIndent();
                    var targetIndex = _ast!.GetChild(stmt, 0);
                    var memberIndex = _ast.GetChild(stmt, 1);
                    var valIndex = _ast.GetChild(stmt, 2);
                    _writer.Write(FormatMemberTarget(targetIndex, memberIndex, stmt.Merge.Value == 1));
                    _writer.Write(" = ");
                    _writer.Write(FormatExpression(valIndex));
                    _writer.WriteLine(";");
                    break;
                }
            case Avm1AstNodeKind.CompoundAssignMember:
                {
                    WriteIndent();
                    var targetIndex = _ast!.GetChild(stmt, 0);
                    var memberIndex = _ast.GetChild(stmt, 1);
                    var valIndex = _ast.GetChild(stmt, 2);
                    _writer.Write(FormatMemberTarget(targetIndex, memberIndex, stmt.Merge.Value == 1));
                    _writer.Write(' ');
                    _writer.Write(GetCompoundAssignmentSymbol((ActionOpcode)stmt.StartAction.Value));
                    _writer.Write(' ');
                    _writer.Write(FormatExpression(valIndex));
                    _writer.WriteLine(";");
                    break;
                }
            case Avm1AstNodeKind.DeclareVariable:
                {
                    WriteIndent();
                    var nameIndex = _ast!.GetChild(stmt, 0);
                    _writer.Write("var ");
                    _writer.Write(FormatVariableTarget(nameIndex));
                    if (Avm1AstArena.GetChildCount(stmt) > 1)
                    {
                        _writer.Write(" = ");
                        _writer.Write(FormatExpression(_ast.GetChild(stmt, 1)));
                    }
                    _writer.WriteLine(";");
                    break;
                }
            case Avm1AstNodeKind.ExpressionStatement:
                {
                    WriteIndent();
                    var exprIndex = _ast!.GetChild(stmt, 0);
                    _writer.Write(FormatExpression(exprIndex));
                    _writer.WriteLine(";");
                    break;
                }
            case Avm1AstNodeKind.Return:
                {
                    WriteIndent();
                    _writer.Write("return");
                    if (Avm1AstArena.GetChildCount(stmt) > 0)
                    {
                        var valIndex = _ast!.GetChild(stmt, 0);
                        _writer.Write(' ');
                        _writer.Write(FormatExpression(valIndex));
                    }
                    _writer.WriteLine(";");
                    break;
                }
            case Avm1AstNodeKind.Throw:
                {
                    WriteIndent();
                    var valIndex = _ast!.GetChild(stmt, 0);
                    _writer.Write("throw ");
                    _writer.Write(FormatExpression(valIndex));
                    _writer.WriteLine(";");
                    break;
                }
            case Avm1AstNodeKind.Opaque:
                {
                    WriteIndent();
                    var action = stmt.OriginAction.IsValid
                        ? GetMethod().Instructions[stmt.OriginAction].Action
                        : null;
                    _writer.Write("/* unsupported AVM1 action");
                    if (action is not null)
                    {
                        _writer.Write(": ");
                        _writer.Write(action.Opcode);
                    }
                    _writer.WriteLine(" */");
                    break;
                }
        }
    }

    private void WriteNestedRegisterDeclarations(in Avm1AstNode statement)
    {
        for (var i = 0; i < Avm1AstArena.GetChildCount(statement); i++)
            Visit(_ast!.GetChild(statement, i));

        void Visit(AstIndex index)
        {
            var node = _ast![index];
            if (node.Kind is Avm1AstNodeKind.FunctionLiteral ||
                node.Kind < Avm1AstNodeKind.AssignRegister)
                return;

            if ((node.Kind is Avm1AstNodeKind.AssignRegister or
                    Avm1AstNodeKind.CompoundAssignRegister) &&
                GetMethod().SymbolTable.TryGetRegisterSymbol(
                    node.Block.Value,
                    node.Merge.Value,
                    out var symbol) &&
                !IsSymbolDeclared(symbol))
            {
                WriteIndent();
                _writer.Write("var ");
                _writer.Write(symbol.Name);
                _writer.WriteLine(";");
                DeclareSymbol(symbol);
            }

            for (var child = 0; child < Avm1AstArena.GetChildCount(node); child++)
                Visit(_ast.GetChild(node, child));
        }
    }

    private string FormatMemberTarget(AstIndex targetIndex, AstIndex memberIndex, bool isComputed)
    {
        var target = FormatExpression(targetIndex, 12);
        if (isComputed)
            return $"{target}[{FormatExpression(memberIndex)}]";

        var member = FormatExpression(memberIndex, 12);
        if (member.StartsWith('"') && member.EndsWith('"'))
            member = member[1..^1];
        return $"{target}.{member}";
    }

    private bool IsUndefinedExpression(AstIndex index)
    {
        if (!index.IsValid)
            return true;

        var node = _ast![index];
        return node.Kind is Avm1AstNodeKind.Literal &&
            node.StartAction.Value >= 0 &&
            GetMethod().ValueAnalysis[new ValueIndex(node.StartAction.Value)].ConstantKind is Avm1ConstantKind.Undefined;
    }

    private string FormatExpression(AstIndex index, int parentPrecedence = 0)
    {
        if (!index.IsValid)
            return "undefined";

        var node = _ast![index];
        switch (node.Kind)
        {
            case Avm1AstNodeKind.Literal:
                {
                    int valIdx = node.StartAction.Value;
                    if (valIdx == -1)
                    {
                        return node.IntOperand is Avm1AstArena.SyntheticTrueLiteral
                            ? "true"
                            : "undefined";
                    }

                    var fact = GetMethod().ValueAnalysis[new ValueIndex(valIdx)];
                    return FormatConstant(fact);
                }

            case Avm1AstNodeKind.Register:
                {
                    int reg = node.Block.Value;
                    int version = node.Merge.Value;
                    return FormatRegisterName(reg, version);
                }

            case Avm1AstNodeKind.TempVar:
                {
                    int id = node.StartAction.Value;
                    var value = new ValueIndex(id);
                    return GetMethod().SymbolTable.TryGetTempSymbol(value, out var symbol)
                        ? symbol.Name
                        : $"v{id.ToString(CultureInfo.InvariantCulture)}";
                }

            case Avm1AstNodeKind.Variable:
                {
                    var nameIndex = _ast.GetChild(node, 0);
                    var nameStr = FormatExpression(nameIndex);
                    if (nameStr.StartsWith('"') && nameStr.EndsWith('"'))
                    {
                        var rawName = nameStr[1..^1];
                        if (IsIdentifier(rawName))
                            return rawName;
                    }
                    return $"eval({nameStr})";
                }

            case Avm1AstNodeKind.AssignMember:
                {
                    var targetIndex = _ast.GetChild(node, 0);
                    var memberIndex = _ast.GetChild(node, 1);
                    var valueIndex = _ast.GetChild(node, 2);
                    var result =
                        $"{FormatMemberTarget(targetIndex, memberIndex, node.Merge.Value == 1)} = " +
                        FormatExpression(valueIndex);
                    return parentPrecedence > 0 ? $"({result})" : result;
                }

            case Avm1AstNodeKind.AssignVariable:
                {
                    var nameIndex = _ast.GetChild(node, 0);
                    var valueIndex = _ast.GetChild(node, 1);
                    var result =
                        $"{FormatVariableTarget(nameIndex)} = {FormatExpression(valueIndex)}";
                    return parentPrecedence > 0 ? $"({result})" : result;
                }

            case Avm1AstNodeKind.AssignRegister:
                {
                    var valueIndex = _ast.GetChild(node, 0);
                    var result =
                        $"{FormatRegisterName(node.Block.Value, node.Merge.Value)} = " +
                        FormatExpression(valueIndex);
                    return parentPrecedence > 0 ? $"({result})" : result;
                }

            case Avm1AstNodeKind.CompoundAssignRegister:
                {
                    var valueIndex = _ast.GetChild(node, 0);
                    var op = GetCompoundAssignmentSymbol((ActionOpcode)node.StartAction.Value);
                    var result =
                        $"{FormatRegisterName(node.Block.Value, node.Merge.Value)} {op} " +
                        FormatExpression(valueIndex);
                    return parentPrecedence > 0 ? $"({result})" : result;
                }

            case Avm1AstNodeKind.Prefix:
                {
                    const int prefixPrecedence = 11;
                    var operand = FormatExpression(_ast.GetChild(node, 0), prefixPrecedence);
                    var prefix = node.StartAction.Value == (int)ActionOpcode.Increment ? "++" : "--";
                    var result = $"{prefix}{operand}";
                    return parentPrecedence > prefixPrecedence ? $"({result})" : result;
                }

            case Avm1AstNodeKind.Postfix:
                {
                    const int postfixPrecedence = 12;
                    var operand = FormatExpression(_ast.GetChild(node, 0), postfixPrecedence);
                    var suffix = node.StartAction.Value == (int)ActionOpcode.Increment ? "++" : "--";
                    var result = $"{operand}{suffix}";
                    return parentPrecedence > postfixPrecedence ? $"({result})" : result;
                }

            case Avm1AstNodeKind.Unary:
                {
                    var operandIndex = _ast.GetChild(node, 0);
                    if (node.StartAction.Value == 1)
                    {
                        var condition = FormatExpression(operandIndex, 11);
                        return $"!{condition}";
                    }

                    var opcode = (ActionOpcode)node.StartAction.Value;
                    if (opcode is ActionOpcode.TypeOf)
                        return $"typeof {FormatExpression(operandIndex, 11)}";

                    if (opcode is ActionOpcode.Increment or ActionOpcode.Decrement)
                    {
                        const int additivePrecedence = 9;
                        var operand = FormatExpression(operandIndex, additivePrecedence);
                        var result = $"{operand} {(opcode is ActionOpcode.Increment ? "+" : "-")} 1";
                        return parentPrecedence > additivePrecedence ? $"({result})" : result;
                    }

                    return $"~{FormatExpression(operandIndex, 11)}";
                }

            case Avm1AstNodeKind.Binary:
                {
                    var leftIndex = _ast.GetChild(node, 0);
                    var rightIndex = _ast.GetChild(node, 1);
                    var opcode = (ActionOpcode)node.StartAction.Value;
                    var opSymbol = GetBinaryOpSymbol(opcode, node.Merge.Value == 1);

                    int prec = GetPrecedence(opSymbol);
                    var leftStr = FormatExpression(leftIndex, prec);
                    var rightStr = FormatExpression(rightIndex, prec + 1);

                    var result = $"{leftStr} {opSymbol} {rightStr}";
                    return parentPrecedence > prec ? $"({result})" : result;
                }

            case Avm1AstNodeKind.Conditional:
                {
                    var condition = FormatExpression(_ast.GetChild(node, 0), 1);
                    var whenTrue = FormatExpression(_ast.GetChild(node, 1), 1);
                    var whenFalse = FormatExpression(_ast.GetChild(node, 2), 0);
                    var result = $"{condition} ? {whenTrue} : {whenFalse}";
                    return parentPrecedence > 0 ? $"({result})" : result;
                }

            case Avm1AstNodeKind.MemberAccess:
                {
                    var targetIndex = _ast.GetChild(node, 0);
                    var memberIndex = _ast.GetChild(node, 1);
                    bool isComputed = node.Merge.Value == 1;

                    var targetStr = FormatExpression(targetIndex, 12);
                    if (isComputed)
                    {
                        return $"{targetStr}[{FormatExpression(memberIndex, 0)}]";
                    }

                    var memberStr = FormatExpression(memberIndex, 12);
                    if (memberStr.StartsWith('"') && memberStr.EndsWith('"'))
                    {
                        memberStr = memberStr[1..^1];
                    }
                    return $"{targetStr}.{memberStr}";
                }

            case Avm1AstNodeKind.Delete:
                return $"delete {FormatExpression(_ast.GetChild(node, 0), 11)}";

            case Avm1AstNodeKind.Intrinsic:
                {
                    var opcode = (ActionOpcode)node.StartAction.Value;
                    if (opcode is ActionOpcode.GetURL)
                        return FormatImmediateGetUrl(node);
                    if (opcode is ActionOpcode.GetURL2)
                        return FormatStackGetUrl(node);
                    if (opcode is ActionOpcode.GotoFrame or ActionOpcode.GoToLabel)
                        return FormatImmediateTimelineGoto(node);
                    if (opcode is ActionOpcode.GotoFrame2)
                        return FormatStackTimelineGoto(node);

                    var name = opcode switch
                    {
                        ActionOpcode.Trace => "trace",
                        ActionOpcode.GetTime => "getTimer",
                        ActionOpcode.TargetPath => "targetPath",
                        ActionOpcode.ToNumber => "Number",
                        ActionOpcode.ToString => "String",
                        ActionOpcode.ToInteger => "int",
                        ActionOpcode.RandomNumber => "random",
                        ActionOpcode.StringLength => "length",
                        ActionOpcode.MBStringLength => "mblength",
                        ActionOpcode.CharToAscii => "ord",
                        ActionOpcode.AsciiToChar => "chr",
                        ActionOpcode.MBCharToAscii => "mbord",
                        ActionOpcode.MBAsciiToChar => "mbchr",
                        ActionOpcode.StringExtract => "substring",
                        ActionOpcode.MBStringExtract => "mbsubstring",
                        ActionOpcode.GetProperty => "getProperty",
                        ActionOpcode.SetProperty => "setProperty",
                        ActionOpcode.CloneSprite => "duplicateMovieClip",
                        ActionOpcode.RemoveSprite => "removeMovieClip",
                        ActionOpcode.StartDrag => "startDrag",
                        ActionOpcode.EndDrag => "stopDrag",
                        ActionOpcode.NextFrame => "nextFrame",
                        ActionOpcode.PreviousFrame => "prevFrame",
                        ActionOpcode.Play => "play",
                        ActionOpcode.Stop => "stop",
                        ActionOpcode.StopSounds => "stopAllSounds",
                        ActionOpcode.ToggleQuality => "toggleHighQuality",
                        ActionOpcode.Call => "call",
                        _ => "undefined"
                    };
                    var arguments = FormatCallArguments(node, 0);
                    return $"{name}({arguments})";
                }

            case Avm1AstNodeKind.CallFunction:
                {
                    var nameIndex = _ast.GetChild(node, 0);
                    return $"{FormatCallableName(nameIndex)}({FormatCallArguments(node, 1)})";
                }

            case Avm1AstNodeKind.NewObject:
                {
                    var nameIndex = _ast.GetChild(node, 0);
                    return $"new {FormatConstructorName(nameIndex)}({FormatCallArguments(node, 1)})";
                }

            case Avm1AstNodeKind.NewMethod:
                return $"new {FormatMethodCallTarget(node, normalizeAccessors: false, parentPrecedence)}";

            case Avm1AstNodeKind.CallMethod:
                return FormatMethodCallTarget(node, normalizeAccessors: true, parentPrecedence);

            case Avm1AstNodeKind.ArrayLiteral:
                return $"[{FormatCallArguments(node, 0)}]";

            case Avm1AstNodeKind.ObjectLiteral:
                return "{" + FormatObjectLiteral(node) + "}";

            case Avm1AstNodeKind.FunctionLiteral:
                return FormatFunctionLiteral(node);

            default:
                return "undefined";
        }
    }

    private string FormatRegisterName(int register, int version)
    {
        if (_registerVersionAliases.TryGetValue((register, version), out var versionAlias))
            return versionAlias;

        if (_registerAliases.TryGetValue(register, out var alias))
            return alias;

        if (GetMethod().SymbolTable.TryGetRegisterSymbol(register, version, out var symbol))
            return symbol.Name;

        return $"_loc{register.ToString(CultureInfo.InvariantCulture)}_v{version}";
    }

    private string FormatCallableName(AstIndex index)
    {
        return TryGetStringLiteral(index, out var name) && IsIdentifier(name)
            ? name
            : FormatExpression(index);
    }

    private string FormatConstructorName(AstIndex index)
    {
        return TryGetStringLiteral(index, out var name) && IsQualifiedIdentifier(name)
            ? name
            : FormatExpression(index);
    }

    private string FormatVariableTarget(AstIndex index)
    {
        return TryGetStringLiteral(index, out var name) && IsIdentifier(name)
            ? name
            : FormatExpression(index);
    }

    private string FormatMethodCallTarget(
        Avm1AstNode node,
        bool normalizeAccessors,
        int parentPrecedence)
    {
        var targetIndex = _ast!.GetChild(node, 0);
        var nameIndex = _ast.GetChild(node, 1);
        var target = FormatExpression(targetIndex, 12);
        if (TryGetStringLiteral(nameIndex, out var name) && name.Length > 0)
        {
            var argumentCount = Avm1AstArena.GetChildCount(node) - 2;
            if (normalizeAccessors &&
                argumentCount == 0 &&
                TryGetAccessorProperty(name, "__get__", out var getterProperty))
            {
                return $"{target}.{getterProperty}";
            }

            if (normalizeAccessors &&
                argumentCount == 1 &&
                TryGetAccessorProperty(name, "__set__", out var setterProperty))
            {
                var result = $"{target}.{setterProperty} = {FormatExpression(_ast.GetChild(node, 2), 1)}";
                return parentPrecedence > 1 ? $"({result})" : result;
            }

            return IsIdentifier(name)
                ? $"{target}.{name}({FormatCallArguments(node, 2)})"
                : $"{target}[{FormatExpression(nameIndex)}]({FormatCallArguments(node, 2)})";
        }

        return IsUndefinedExpression(nameIndex)
            ? $"{target}({FormatCallArguments(node, 2)})"
            : $"{target}[{FormatExpression(nameIndex)}]({FormatCallArguments(node, 2)})";
    }

    private static bool TryGetAccessorProperty(string methodName, string prefix, out string propertyName)
    {
        propertyName = string.Empty;
        if (!methodName.StartsWith(prefix, StringComparison.Ordinal) || methodName.Length == prefix.Length)
            return false;

        propertyName = methodName[prefix.Length..];
        return IsIdentifier(propertyName);
    }

    private string FormatImmediateGetUrl(Avm1AstNode node)
    {
        var method = GetMethod();
        if (!node.OriginAction.IsValid ||
            node.OriginAction.Value >= method.Instructions.Count ||
            method.Instructions[node.OriginAction].Action is not ActionGetURL action)
        {
            return "undefined";
        }

        const string fsCommandPrefix = "FSCommand:";
        var isFsCommand = action.Url.StartsWith(
            fsCommandPrefix,
            StringComparison.Ordinal);
        var level = 0;
        var isLevelLoad = !isFsCommand &&
            TryParseLevelTarget(action.Target, out level);
        var name = isFsCommand
            ? "fscommand"
            : isLevelLoad ? "loadMovieNum" : "getURL";
        var first = FormatStringLiteral(
            isFsCommand ? action.Url[fsCommandPrefix.Length..] : action.Url);
        if (isLevelLoad)
            return $"{name}({first}, {level.ToString(CultureInfo.InvariantCulture)})";
        return action.Target.Length == 0
            ? $"{name}({first})"
            : $"{name}({first}, {FormatStringLiteral(action.Target)})";
    }

    private string FormatImmediateTimelineGoto(Avm1AstNode node)
    {
        var method = GetMethod();
        if (!node.OriginAction.IsValid ||
            node.OriginAction.Value >= method.Instructions.Count)
        {
            return "undefined";
        }

        var target = method.Instructions[node.OriginAction].Action switch
        {
            ActionGotoFrame gotoFrame =>
                (gotoFrame.Frame + 1).ToString(CultureInfo.InvariantCulture),
            Swf3.ActionGoToLabel gotoLabel =>
                FormatStringLiteral(gotoLabel.Label),
            _ => string.Empty
        };
        if (target.Length == 0)
            return "undefined";

        var name = node.IntOperand == 1 ? "gotoAndPlay" : "gotoAndStop";
        return $"{name}({target})";
    }

    private string FormatStackTimelineGoto(Avm1AstNode node)
    {
        var method = GetMethod();
        if (!node.OriginAction.IsValid ||
            node.OriginAction.Value >= method.Instructions.Count ||
            method.Instructions[node.OriginAction].Action is not ActionGotoFrame2 action)
        {
            return "undefined";
        }

        var target = FormatCallArguments(node, 0);
        var name = action.Play ? "gotoAndPlay" : "gotoAndStop";
        if (!action.HasSceneBias)
            return $"{name}({target})";
        if (method.TimelineLayout is null ||
            !method.TimelineLayout.TryGetSceneByFrameOffset(
                action.SceneBias,
                out var scene))
        {
            return "undefined";
        }

        return $"{name}({FormatStringLiteral(scene.Name)}, {target})";
    }

    private string FormatStackGetUrl(Avm1AstNode node)
    {
        var flags = (GetUrlFlags)(byte)node.IntOperand;
        var loadTarget = flags.HasFlag(GetUrlFlags.LoadTarget);
        var loadVariables = flags.HasFlag(GetUrlFlags.LoadVariables);
        var url = FormatExpression(_ast!.GetChild(node, 0));
        var targetIndex = _ast.GetChild(node, 1);
        string name;
        string target;

        var level = string.Empty;
        var hasLevel = !loadTarget &&
            TryFormatLevelArgument(targetIndex, out level);
        if (!loadTarget && loadVariables && !hasLevel)
            return "undefined";

        if (hasLevel)
        {
            name = loadVariables ? "loadVariablesNum" : "loadMovieNum";
            target = level;
        }
        else
        {
            name = (loadTarget, loadVariables) switch
            {
                (true, true) => "loadVariables",
                (true, false) => "loadMovie",
                (false, true) => throw new InvalidOperationException(),
                _ => "getURL"
            };
            target = FormatExpression(targetIndex);
        }

        var arguments = $"{url}, {target}";
        var method = flags & GetUrlFlags.MethodMask;
        if (method is GetUrlFlags.MethodGet or GetUrlFlags.MethodPost)
            arguments += method is GetUrlFlags.MethodGet ? ", \"GET\"" : ", \"POST\"";
        return $"{name}({arguments})";
    }

    private bool TryFormatLevelArgument(AstIndex targetIndex, out string level)
    {
        if (TryGetStringLiteral(targetIndex, out var target) &&
            TryParseLevelTarget(target, out var literalLevel))
        {
            level = literalLevel.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        if (targetIndex.IsValid)
        {
            var targetNode = _ast![targetIndex];
            if (targetNode.Kind is Avm1AstNodeKind.Binary &&
                (ActionOpcode)targetNode.StartAction.Value is ActionOpcode.StringAdd &&
                Avm1AstArena.GetChildCount(targetNode) == 2 &&
                TryGetStringLiteral(_ast.GetChild(targetNode, 0), out var prefix) &&
                prefix == "_level")
            {
                level = FormatExpression(_ast.GetChild(targetNode, 1));
                return true;
            }
        }

        level = string.Empty;
        return false;
    }

    private static bool TryParseLevelTarget(string target, out int level)
    {
        level = 0;
        const string prefix = "_level";
        return target.StartsWith(prefix, StringComparison.Ordinal) &&
            target.Length > prefix.Length &&
            int.TryParse(
                target.AsSpan(prefix.Length),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out level);
    }

    private string FormatCallArguments(Avm1AstNode node, int firstArgument)
    {
        var count = Avm1AstArena.GetChildCount(node);
        if (firstArgument >= count)
            return string.Empty;

        var parts = new string[count - firstArgument];
        for (var i = firstArgument; i < count; i++)
            parts[i - firstArgument] = FormatExpression(_ast!.GetChild(node, i));

        return string.Join(", ", parts);
    }

    private string FormatObjectLiteral(Avm1AstNode node)
    {
        var count = Avm1AstArena.GetChildCount(node);
        if (count == 0)
            return string.Empty;

        var parts = new string[count / 2];
        for (var i = 0; i + 1 < count; i += 2)
        {
            var key = _ast!.GetChild(node, i);
            var value = _ast.GetChild(node, i + 1);
            parts[i / 2] = $"{FormatObjectKey(key)}: {FormatExpression(value)}";
        }

        return string.Join(", ", parts);
    }

    private string FormatFunctionLiteral(Avm1AstNode node)
    {
        if (!node.StartAction.IsValid)
            return "function() {}";

        var method = GetMethod();
        var action = method.Instructions[node.StartAction].Action;
        var function = action switch
        {
            ActionDefineFunction2 function2 => function2,
            ActionDefineFunction function1 => Avm1ClassDecompiler.UpgradeFunction(function1),
            _ => null
        };
        if (function is null)
            return "function() {}";

        var bodyActions = Avm1Decompiler.DecodeActions(function.Body, method.Instructions.SwfVersion).ToList();
        var outerActions = method.Instructions.Instructions.Select(instruction => instruction.Action).ToArray();
        var activePool = Avm1ClassDecompiler.FindActiveConstantPool(outerActions, node.StartAction.Value);
        if (activePool is not null)
            bodyActions.Insert(0, new ActionConstantPool(activePool));

        var nested = Avm1Decompiler.DecompileMethod(
            bodyActions,
            method.Instructions.SwfVersion,
            new FunctionContext(function.Flags, function.Parameters),
            method.TypeEnvironment,
            new Avm1MethodDecompilationOptions
            {
                TimelineLayout = method.TimelineLayout
            });
        using var bodyWriter = new StringWriter(CultureInfo.InvariantCulture);
        new Avm1StructuredAs2Emitter(bodyWriter, _indent + 1).Write(nested);

        var result = new StringBuilder();
        result.Append("function");
        if (function.Name.Length > 0)
            result.Append(' ').Append(function.Name);
        result.Append('(')
            .Append(string.Join(", ", function.Parameters.Select(parameter => parameter.Name)))
            .AppendLine(")");
        AppendIndent(result, _indent);
        result.AppendLine("{");
        result.Append(bodyWriter.ToString());
        AppendIndent(result, _indent);
        result.Append('}');
        return result.ToString();
    }

    private static void AppendIndent(StringBuilder builder, int indent)
    {
        for (var i = 0; i < indent; i++)
            builder.Append("    ");
    }

    private string FormatObjectKey(AstIndex index)
    {
        if (!TryGetStringLiteral(index, out var name))
            return FormatExpression(index);

        return IsIdentifier(name)
            ? name
            : FormatConstant(Avm1ValueFact.String(ValueIndex.Invalid, name));
    }

    private bool TryGetStringLiteral(AstIndex index, out string value)
    {
        value = string.Empty;
        if (!index.IsValid)
            return false;

        var node = _ast![index];
        if (node.Kind is not Avm1AstNodeKind.Literal || node.StartAction.Value < 0)
            return false;

        var fact = GetMethod().ValueAnalysis[new ValueIndex(node.StartAction.Value)];
        if (fact.ConstantKind is not Avm1ConstantKind.String || fact.StringValue is null)
            return false;

        value = fact.StringValue;
        return true;
    }

    private static string GetBinaryOpSymbol(ActionOpcode opcode, bool inverted = false)
    {
        if (inverted)
        {
            return opcode switch
            {
                ActionOpcode.Equals or ActionOpcode.Equals2 or ActionOpcode.StringEquals => "!=",
                ActionOpcode.StrictEquals => "!==",
                ActionOpcode.Less or ActionOpcode.Less2 or ActionOpcode.StringLess => ">=",
                ActionOpcode.Greater or ActionOpcode.StringGreater => "<=",
                _ => "?"
            };
        }

        return opcode switch
        {
            ActionOpcode.Add or ActionOpcode.Add2 or ActionOpcode.StringAdd => "+",
            ActionOpcode.Subtract => "-",
            ActionOpcode.Multiply => "*",
            ActionOpcode.Divide => "/",
            ActionOpcode.Modulo => "%",
            ActionOpcode.Equals or ActionOpcode.Equals2 or ActionOpcode.StringEquals => "==",
            ActionOpcode.StrictEquals => "===",
            ActionOpcode.Less or ActionOpcode.Less2 or ActionOpcode.StringLess => "<",
            ActionOpcode.Greater or ActionOpcode.StringGreater => ">",
            ActionOpcode.And => "&&",
            ActionOpcode.Or => "||",
            ActionOpcode.BitAnd => "&",
            ActionOpcode.BitOr => "|",
            ActionOpcode.BitXor => "^",
            ActionOpcode.BitLShift => "<<",
            ActionOpcode.BitRShift => ">>",
            ActionOpcode.BitURShift => ">>>",
            ActionOpcode.InstanceOf => "instanceof",
            _ => "?"
        };
    }

    private static string GetCompoundAssignmentSymbol(ActionOpcode opcode) =>
        GetBinaryOpSymbol(opcode) + "=";

    private static int GetPrecedence(string op) => op switch
    {
        "||" => 1,
        "&&" => 2,
        "|" => 3,
        "^" => 4,
        "&" => 5,
        "==" or "!=" or "===" or "!==" => 6,
        "<" or ">" or "<=" or ">=" or "instanceof" => 7,
        "<<" or ">>" or ">>>" => 8,
        "+" or "-" => 9,
        "*" or "/" or "%" => 10,
        _ => 0
    };

    private static string FormatConstant(Avm1ValueFact fact) => fact.ConstantKind switch
    {
        Avm1ConstantKind.Undefined => "undefined",
        Avm1ConstantKind.Null => "null",
        Avm1ConstantKind.Boolean => fact.BooleanValue ? "true" : "false",
        Avm1ConstantKind.Integer => fact.IntegerValue.ToString(CultureInfo.InvariantCulture),
        Avm1ConstantKind.Number => fact.NumberValue.ToString(CultureInfo.InvariantCulture),
        Avm1ConstantKind.String => FormatStringLiteral(fact.StringValue ?? string.Empty),
        _ => "undefined"
    };

    private static string FormatStringLiteral(string value)
    {
        var length = 2;
        foreach (var character in value)
            length += GetStringLiteralCharacterLength(character);

        return string.Create(length, value, static (destination, source) =>
        {
            var position = 0;
            destination[position++] = '"';

            foreach (var character in source)
            {
                var shortEscape = character switch
                {
                    '"' => '"',
                    '\\' => '\\',
                    '\b' => 'b',
                    '\t' => 't',
                    '\n' => 'n',
                    '\f' => 'f',
                    '\r' => 'r',
                    _ => '\0'
                };

                if (shortEscape != '\0')
                {
                    destination[position++] = '\\';
                    destination[position++] = shortEscape;
                    continue;
                }

                if (RequiresUnicodeEscape(character))
                {
                    destination[position++] = '\\';
                    destination[position++] = 'u';
                    destination[position++] = ToHexDigit(character >> 12);
                    destination[position++] = ToHexDigit(character >> 8);
                    destination[position++] = ToHexDigit(character >> 4);
                    destination[position++] = ToHexDigit(character);
                    continue;
                }

                destination[position++] = character;
            }

            destination[position] = '"';
        });
    }

    private static int GetStringLiteralCharacterLength(char value)
    {
        if (value is '"' or '\\' or '\b' or '\t' or '\n' or '\f' or '\r')
            return 2;

        return RequiresUnicodeEscape(value) ? 6 : 1;
    }

    private static bool RequiresUnicodeEscape(char value) =>
        char.IsControl(value) ||
        char.IsSurrogate(value) ||
        value is '\u2028' or '\u2029';

    private static char ToHexDigit(int value)
    {
        value &= 0xF;
        return (char)(value < 10 ? '0' + value : 'A' + value - 10);
    }

    private static bool IsIdentifier(string value)
    {
        if (value.Length == 0) return false;
        if (value[0] != '_' && value[0] != '$' && !char.IsAsciiLetter(value[0])) return false;
        for (var i = 1; i < value.Length; i++)
            if (value[i] != '_' && value[i] != '$' && !char.IsAsciiLetterOrDigit(value[i]))
                return false;
        return true;
    }

    private static bool IsQualifiedIdentifier(string value)
    {
        if (value.Length == 0)
            return false;

        var start = 0;
        while (start < value.Length)
        {
            var dot = value.IndexOf('.', start);
            var end = dot < 0 ? value.Length : dot;
            if (end == start || !IsIdentifier(value[start..end]))
                return false;

            if (dot < 0)
                return true;

            start = dot + 1;
        }

        return false;
    }

    private static bool IsIdentifierStart(char value)
    {
        return value is '_' or '$' || char.IsAsciiLetter(value);
    }

    private static bool IsIdentifierPart(char value)
    {
        return IsIdentifierStart(value) || char.IsAsciiDigit(value);
    }

    private void WriteIndent()
    {
        for (var i = 0; i < _indent; i++)
            _writer.Write("    ");
    }

    private Avm1MethodDecompilation GetMethod()
    {
        return _method ?? throw new InvalidOperationException("No method is currently being emitted.");
    }
}
