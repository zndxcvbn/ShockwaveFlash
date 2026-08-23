using System.Globalization;

namespace ShockwaveFlash.Avm1.Source.Emit;

public sealed class Avm1SourceAs2Emitter
{
    private readonly TextWriter _writer;
    private readonly Avm1SourceFileProjection? _projection;
    private readonly HashSet<SourceSymbolIndex> _declaredSymbols = [];
    private Avm1SourceArena? _arena;
    private int _indent;

    public Avm1SourceAs2Emitter(TextWriter writer, int initialIndent = 0)
        : this(writer, initialIndent, projection: null)
    {
    }

    internal Avm1SourceAs2Emitter(
        TextWriter writer,
        int initialIndent,
        Avm1SourceFileProjection? projection)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentOutOfRangeException.ThrowIfNegative(initialIndent);
        _writer = writer;
        _indent = initialIndent;
        _projection = projection;
    }

    public void Write(Avm1SourceMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);
        _declaredSymbols.Clear();
        foreach (var parameter in method.Parameters)
            _declaredSymbols.Add(parameter);
        Write(method.Arena, method.Body);
    }

    public void Write(Avm1SourceExpressionFragment expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        _arena = expression.Arena;
        _writer.Write(FormatExpression(expression.Expression));
    }

    private void Write(Avm1SourceArena arena, SourceStatementIndex body)
    {
        _arena = arena;
        WriteStatement(body);
    }

    private void WriteStatement(SourceStatementIndex index)
    {
        var statement = GetArena()[index];
        switch (statement.Kind)
        {
            case Avm1SourceStatementKind.Block:
                WriteStatementChildren(statement);
                break;

            case Avm1SourceStatementKind.Expression:
                WriteIndent();
                _writer.Write(FormatExpression(statement.Expression));
                _writer.WriteLine(";");
                break;

            case Avm1SourceStatementKind.VariableDeclaration:
                if (!_declaredSymbols.Add(statement.Symbol))
                {
                    if (!statement.Expression.IsValid)
                        break;
                    WriteIndent();
                    _writer.Write(GetSymbolName(statement.Symbol));
                    _writer.Write(" = ");
                    _writer.Write(FormatExpression(statement.Expression));
                    _writer.WriteLine(";");
                    break;
                }
                WriteIndent();
                _writer.Write("var ");
                _writer.Write(GetSymbolName(statement.Symbol));
                _writer.Write(GetSymbolTypeSuffix(statement.Symbol));
                if (statement.Expression.IsValid)
                {
                    _writer.Write(" = ");
                    _writer.Write(FormatExpression(statement.Expression));
                }
                _writer.WriteLine(";");
                break;

            case Avm1SourceStatementKind.FunctionDeclaration:
                WriteIndent();
                _writer.Write(FormatFunctionLiteral(statement.Expression, requireDeclaration: true));
                _writer.WriteLine();
                break;

            case Avm1SourceStatementKind.Return:
                WriteIndent();
                _writer.Write("return");
                if (statement.Expression.IsValid)
                {
                    _writer.Write(' ');
                    _writer.Write(FormatExpression(statement.Expression));
                }
                _writer.WriteLine(";");
                break;

            case Avm1SourceStatementKind.Throw:
                WriteIndent();
                _writer.Write("throw ");
                _writer.Write(FormatExpression(statement.Expression));
                _writer.WriteLine(";");
                break;

            case Avm1SourceStatementKind.If:
                WriteIf(statement);
                break;

            case Avm1SourceStatementKind.IfFrameLoaded:
                WriteIfFrameLoaded(statement);
                break;

            case Avm1SourceStatementKind.While:
                WriteWhile(statement);
                break;

            case Avm1SourceStatementKind.DoWhile:
                WriteDoWhile(statement);
                break;

            case Avm1SourceStatementKind.For:
                WriteFor(statement);
                break;

            case Avm1SourceStatementKind.ForIn:
                WriteForIn(statement);
                break;

            case Avm1SourceStatementKind.With:
                WriteWith(statement);
                break;

            case Avm1SourceStatementKind.TellTarget:
                WriteTellTarget(statement);
                break;

            case Avm1SourceStatementKind.Try:
                WriteTry(statement);
                break;

            case Avm1SourceStatementKind.CatchClause:
            case Avm1SourceStatementKind.FinallyClause:
                throw new InvalidOperationException(
                    $"Source HIR {statement.Kind} must be a direct child of a try statement.");

            case Avm1SourceStatementKind.Switch:
                WriteSwitch(statement);
                break;

            case Avm1SourceStatementKind.SwitchCase:
            case Avm1SourceStatementKind.SwitchDefault:
                throw new InvalidOperationException(
                    $"Source HIR {statement.Kind} must be a direct child of a switch statement.");

            case Avm1SourceStatementKind.Break:
                WriteControlJump("break", statement);
                break;

            case Avm1SourceStatementKind.Continue:
                WriteControlJump("continue", statement);
                break;

            case Avm1SourceStatementKind.Opaque:
                WriteIndent();
                _writer.Write("/* unsupported Source HIR statement: ");
                _writer.Write(GetOpaqueDescription(
                    statement.Opaque,
                    statement.Name,
                    statement.Kind.ToString()));
                _writer.WriteLine(" */");
                break;

            default:
                WriteIndent();
                _writer.Write("/* Source HIR emitter does not support ");
                _writer.Write(statement.Kind);
                _writer.WriteLine(" yet */");
                break;
        }
    }

    private void WriteIf(Avm1SourceStatement statement, bool writeIndent = true)
    {
        if (statement.Children.Count is < 1 or > 2)
            throw new InvalidOperationException("Source HIR if statement has an invalid branch count.");

        if (writeIndent)
            WriteIndent();
        _writer.Write("if (");
        _writer.Write(FormatExpression(statement.Expression));
        _writer.WriteLine(")");
        WriteBracedStatement(GetArena().GetChild(statement, 0));

        if (statement.Children.Count == 1)
            return;

        var elseBranch = GetArena().GetChild(statement, 1);
        WriteIndent();
        if (TryGetSingleIf(elseBranch, out var nestedIf))
        {
            _writer.Write("else ");
            WriteIf(nestedIf, writeIndent: false);
            return;
        }

        _writer.WriteLine("else");
        WriteBracedStatement(elseBranch);
    }

    private void WriteIfFrameLoaded(Avm1SourceStatement statement)
    {
        if (statement.Children.Count != 1 || !statement.Expression.IsValid)
        {
            throw new InvalidOperationException(
                "Source HIR ifFrameLoaded statement has an invalid layout.");
        }

        WriteIndent();
        _writer.Write("ifFrameLoaded(");
        _writer.Write(FormatExpression(statement.Expression));
        if (statement.SecondaryExpression.IsValid)
        {
            _writer.Write(", ");
            _writer.Write(FormatExpression(statement.SecondaryExpression));
        }
        _writer.WriteLine(")");
        WriteBracedStatement(GetArena().GetChild(statement, 0));
    }

    private bool TryGetSingleIf(
        SourceStatementIndex branch,
        out Avm1SourceStatement nestedIf)
    {
        nestedIf = default;
        if (!branch.IsValid)
            return false;

        var statement = GetArena()[branch];
        if (statement.Kind is Avm1SourceStatementKind.If)
        {
            nestedIf = statement;
            return true;
        }

        if (statement.Kind is not Avm1SourceStatementKind.Block || statement.Children.Count != 1)
            return false;

        var child = GetArena()[GetArena().GetChild(statement, 0)];
        if (child.Kind is not Avm1SourceStatementKind.If)
            return false;

        nestedIf = child;
        return true;
    }

    private void WriteWhile(Avm1SourceStatement statement)
    {
        WriteControlLabel(statement);
        WriteIndent();
        _writer.Write("while (");
        _writer.Write(FormatExpression(statement.Expression));
        _writer.WriteLine(")");
        WriteBracedChildren(statement);
    }

    private void WriteDoWhile(Avm1SourceStatement statement)
    {
        WriteControlLabel(statement);
        WriteIndent();
        _writer.WriteLine("do");
        WriteIndent();
        _writer.WriteLine("{");
        _indent++;
        WriteStatementChildren(statement);
        _indent--;
        WriteIndent();
        _writer.Write("} while (");
        _writer.Write(FormatExpression(statement.Expression));
        _writer.WriteLine(");");
    }

    private void WriteFor(Avm1SourceStatement statement)
    {
        WriteControlLabel(statement);
        WriteIndent();
        _writer.Write("for (");
        _writer.Write(FormatForInitializers(statement));
        _writer.Write("; ");
        if (statement.Expression.IsValid)
            _writer.Write(FormatExpression(statement.Expression));
        _writer.Write("; ");
        _writer.Write(FormatForUpdates(statement));
        _writer.WriteLine(")");
        WriteBracedChildren(statement);
    }

    private void WriteForIn(Avm1SourceStatement statement)
    {
        if (!statement.Expression.IsValid || !statement.SecondaryExpression.IsValid)
            throw new InvalidOperationException("Source HIR for-in statement has an invalid clause.");

        WriteControlLabel(statement);
        WriteIndent();
        _writer.Write("for (");
        if (statement.Flags.HasFlag(Avm1SourceStatementFlags.ForInDeclaresKey))
            _writer.Write("var ");
        _writer.Write(FormatExpression(statement.Expression));
        if (statement.Flags.HasFlag(Avm1SourceStatementFlags.ForInDeclaresKey) &&
            TryGetReferencedSymbol(statement.Expression, out var keySymbol))
        {
            _writer.Write(GetSymbolTypeSuffix(keySymbol));
        }
        _writer.Write(" in ");
        _writer.Write(FormatExpression(statement.SecondaryExpression));
        _writer.WriteLine(")");
        WriteBracedChildren(statement);
    }

    private void WriteSwitch(Avm1SourceStatement statement)
    {
        if (!statement.Expression.IsValid)
            throw new InvalidOperationException("Source HIR switch statement has no discriminator.");

        WriteControlLabel(statement);
        WriteIndent();
        _writer.Write("switch (");
        _writer.Write(FormatExpression(statement.Expression));
        _writer.WriteLine(")");
        WriteIndent();
        _writer.WriteLine("{");
        _indent++;

        var hasDefault = false;
        for (var i = 0; i < statement.Children.Count; i++)
        {
            var label = GetArena()[GetArena().GetChild(statement, i)];
            switch (label.Kind)
            {
                case Avm1SourceStatementKind.SwitchCase:
                    if (!label.Expression.IsValid)
                        throw new InvalidOperationException("Source HIR switch case has no value.");
                    WriteIndent();
                    _writer.Write("case ");
                    _writer.Write(FormatExpression(label.Expression));
                    _writer.WriteLine(":");
                    break;

                case Avm1SourceStatementKind.SwitchDefault when !hasDefault:
                    WriteIndent();
                    _writer.WriteLine("default:");
                    hasDefault = true;
                    break;

                case Avm1SourceStatementKind.SwitchDefault:
                    throw new InvalidOperationException(
                        "Source HIR switch statement contains more than one default label.");

                default:
                    throw new InvalidOperationException(
                        $"Source HIR switch child {label.Kind} is not a label.");
            }

            _indent++;
            WriteStatementChildren(label);
            _indent--;
        }

        _indent--;
        WriteIndent();
        _writer.WriteLine("}");
    }

    private void WriteWith(Avm1SourceStatement statement)
    {
        if (!statement.Expression.IsValid || statement.Children.Count != 1)
            throw new InvalidOperationException("Source HIR with statement has an invalid layout.");

        WriteIndent();
        _writer.Write("with (");
        _writer.Write(FormatExpression(statement.Expression));
        _writer.WriteLine(")");
        WriteBracedStatement(GetArena().GetChild(statement, 0));
    }

    private void WriteTellTarget(Avm1SourceStatement statement)
    {
        if (!statement.Expression.IsValid || statement.Children.Count != 1)
        {
            throw new InvalidOperationException(
                "Source HIR tellTarget statement has an invalid layout.");
        }

        WriteIndent();
        _writer.Write("tellTarget(");
        _writer.Write(FormatExpression(statement.Expression));
        _writer.WriteLine(")");
        WriteBracedStatement(GetArena().GetChild(statement, 0));
    }

    private void WriteTry(Avm1SourceStatement statement)
    {
        if (statement.Children.Count is < 2 or > 3)
            throw new InvalidOperationException("Source HIR try statement has an invalid clause count.");

        WriteIndent();
        _writer.WriteLine("try");
        WriteBracedStatement(GetArena().GetChild(statement, 0));

        var clauseIndex = 1;
        var clause = GetArena()[GetArena().GetChild(statement, clauseIndex)];
        if (clause.Kind is Avm1SourceStatementKind.CatchClause)
        {
            if (!clause.Symbol.IsValid || clause.Children.Count != 1)
                throw new InvalidOperationException("Source HIR catch clause has an invalid layout.");

            WriteIndent();
            _writer.Write("catch (");
            _writer.Write(GetSymbolName(clause.Symbol));
            _writer.WriteLine(")");
            WriteBracedStatement(GetArena().GetChild(clause, 0));
            clauseIndex++;
        }

        if (clauseIndex < statement.Children.Count)
        {
            clause = GetArena()[GetArena().GetChild(statement, clauseIndex)];
            if (clause.Kind is not Avm1SourceStatementKind.FinallyClause ||
                clause.Children.Count != 1)
            {
                throw new InvalidOperationException("Source HIR finally clause has an invalid layout.");
            }

            WriteIndent();
            _writer.WriteLine("finally");
            WriteBracedStatement(GetArena().GetChild(clause, 0));
            clauseIndex++;
        }

        if (clauseIndex != statement.Children.Count)
            throw new InvalidOperationException("Source HIR try statement has invalid clause ordering.");
    }

    private string FormatForInitializers(Avm1SourceStatement statement)
    {
        if (statement.Initializers.Count == 0)
            return string.Empty;

        var declarationSymbols = new List<SourceSymbolIndex>();
        var allDeclarationsAlreadyVisible = true;
        for (var i = 0; i < statement.Initializers.Count; i++)
        {
            var initializer = GetArena()[GetArena().GetInitializer(statement, i)];
            if (initializer.Kind is not Avm1SourceStatementKind.VariableDeclaration)
                continue;
            declarationSymbols.Add(initializer.Symbol);
            allDeclarationsAlreadyVisible &= _declaredSymbols.Contains(initializer.Symbol);
        }

        var declarations = new string[statement.Initializers.Count];
        var expressions = new string[statement.Initializers.Count];
        var declarationCount = 0;
        var expressionCount = 0;
        for (var i = 0; i < statement.Initializers.Count; i++)
        {
            var initializer = GetArena()[GetArena().GetInitializer(statement, i)];
            if (initializer.Kind is Avm1SourceStatementKind.VariableDeclaration)
            {
                var text = GetSymbolName(initializer.Symbol) +
                    GetSymbolTypeSuffix(initializer.Symbol);
                if (initializer.Expression.IsValid)
                    text += " = " + FormatExpression(initializer.Expression);
                if (allDeclarationsAlreadyVisible)
                    expressions[expressionCount++] = text;
                else
                    declarations[declarationCount++] = text;
                continue;
            }

            if (initializer.Kind is Avm1SourceStatementKind.Expression && initializer.Expression.IsValid)
            {
                expressions[expressionCount++] = FormatExpression(initializer.Expression);
                continue;
            }

            throw new InvalidOperationException(
                $"Source HIR {initializer.Kind} cannot be emitted in a for initializer.");
        }

        if (declarationCount != 0 && expressionCount != 0)
            throw new InvalidOperationException("Source HIR for initializer mixes declarations and expressions.");
        if (declarationCount != 0)
        {
            foreach (var symbol in declarationSymbols)
                _declaredSymbols.Add(symbol);
            return "var " + string.Join(", ", declarations, 0, declarationCount);
        }
        return string.Join(", ", expressions, 0, expressionCount);
    }

    private string FormatForUpdates(Avm1SourceStatement statement)
    {
        if (statement.Expressions.Count == 0)
            return string.Empty;

        var updates = new string[statement.Expressions.Count];
        for (var i = 0; i < updates.Length; i++)
            updates[i] = FormatExpression(GetArena().GetExpression(statement, i));
        return string.Join(", ", updates);
    }

    private void WriteBracedStatement(SourceStatementIndex body)
    {
        WriteIndent();
        _writer.WriteLine("{");
        _indent++;
        WriteStatement(body);
        _indent--;
        WriteIndent();
        _writer.WriteLine("}");
    }

    private void WriteBracedChildren(Avm1SourceStatement statement)
    {
        WriteIndent();
        _writer.WriteLine("{");
        _indent++;
        WriteStatementChildren(statement);
        _indent--;
        WriteIndent();
        _writer.WriteLine("}");
    }

    private void WriteControlLabel(Avm1SourceStatement statement)
    {
        if (!statement.Label.IsValid)
            return;

        WriteIndent();
        _writer.Write(GetLabelName(statement.Label));
        _writer.WriteLine(":");
    }

    private void WriteControlJump(string keyword, Avm1SourceStatement statement)
    {
        WriteIndent();
        _writer.Write(keyword);
        if (statement.Label.IsValid)
        {
            _writer.Write(' ');
            _writer.Write(GetLabelName(statement.Label));
        }
        _writer.WriteLine(";");
    }

    private void WriteStatementChildren(Avm1SourceStatement statement)
    {
        for (var i = 0; i < statement.Children.Count; i++)
            WriteStatement(GetArena().GetChild(statement, i));
    }

    private string FormatExpression(SourceExpressionIndex index, int parentPrecedence = 0)
    {
        if (!index.IsValid)
            return "undefined";

        if (_projection is not null &&
            _projection.TryGetExpressionSpelling(GetArena(), index, out var selectedSpelling))
        {
            return selectedSpelling;
        }

        var expression = GetArena()[index];
        switch (expression.Kind)
        {
            case Avm1SourceExpressionKind.Literal:
                return FormatLiteral(GetArena()[expression.Literal]);

            case Avm1SourceExpressionKind.SymbolReference:
                return GetSymbolName(expression.Symbol);

            case Avm1SourceExpressionKind.DynamicName:
            case Avm1SourceExpressionKind.QualifiedName:
                return GetArena()[expression.Name];

            case Avm1SourceExpressionKind.ComputedDynamicName:
                return "eval(" + FormatExpression(GetArena().GetChild(expression, 0)) + ")";

            case Avm1SourceExpressionKind.Assignment:
                {
                    var target = FormatExpression(GetArena().GetChild(expression, 0), 1);
                    var value = FormatExpression(GetArena().GetChild(expression, 1));
                    var result = target + " " + GetOperatorText(expression.Operator) + " " + value;
                    return parentPrecedence > 0 ? "(" + result + ")" : result;
                }

            case Avm1SourceExpressionKind.Unary:
                return FormatUnary(expression, parentPrecedence);

            case Avm1SourceExpressionKind.Binary:
                {
                    var precedence = GetPrecedence(expression.Operator);
                    var left = FormatExpression(GetArena().GetChild(expression, 0), precedence);
                    var right = FormatExpression(GetArena().GetChild(expression, 1), precedence + 1);
                    var result = left + " " + GetOperatorText(expression.Operator) + " " + right;
                    return parentPrecedence > precedence ? "(" + result + ")" : result;
                }

            case Avm1SourceExpressionKind.Sequence:
                {
                    var items = new string[expression.Children.Count];
                    for (var i = 0; i < items.Length; i++)
                    {
                        items[i] = FormatExpression(
                            GetArena().GetChild(expression, i));
                    }
                    return "(" + string.Join(", ", items) + ")";
                }

            case Avm1SourceExpressionKind.Conditional:
                {
                    var condition = FormatExpression(GetArena().GetChild(expression, 0), 1);
                    var whenTrue = FormatExpression(GetArena().GetChild(expression, 1), 1);
                    var whenFalse = FormatExpression(GetArena().GetChild(expression, 2));
                    var result = condition + " ? " + whenTrue + " : " + whenFalse;
                    return parentPrecedence > 0 ? "(" + result + ")" : result;
                }

            case Avm1SourceExpressionKind.MemberAccess:
                return FormatMemberAccess(expression);

            case Avm1SourceExpressionKind.Delete:
                return "delete " + FormatExpression(GetArena().GetChild(expression, 0), 11);

            case Avm1SourceExpressionKind.IntrinsicCall:
                return GetArena()[expression.Name] + "(" + FormatArguments(expression, 0) + ")";

            case Avm1SourceExpressionKind.Call:
                return FormatExpression(GetArena().GetChild(expression, 0), 12) +
                    "(" + FormatArguments(expression, 1) + ")";

            case Avm1SourceExpressionKind.New:
                return "new " + FormatExpression(GetArena().GetChild(expression, 0), 12) +
                    "(" + FormatArguments(expression, 1) + ")";

            case Avm1SourceExpressionKind.ArrayLiteral:
                return "[" + FormatArguments(expression, 0) + "]";

            case Avm1SourceExpressionKind.ObjectLiteral:
                return "{" + FormatObjectLiteral(expression) + "}";

            case Avm1SourceExpressionKind.FunctionLiteral:
                return FormatFunctionLiteral(expression.Index, requireDeclaration: false);

            case Avm1SourceExpressionKind.Opaque:
                return "(/* unsupported Source HIR expression: " +
                    GetOpaqueDescription(
                        expression.Opaque,
                        expression.Name,
                        expression.Kind.ToString()) +
                    " */ undefined)";

            default:
                throw new InvalidOperationException($"Unknown Source HIR expression kind {expression.Kind}.");
        }
    }

    private string FormatUnary(Avm1SourceExpression expression, int parentPrecedence)
    {
        var operand = GetArena().GetChild(expression, 0);
        var isPostfix = expression.Operator is
            Avm1SourceOperator.PostfixIncrement or
            Avm1SourceOperator.PostfixDecrement;
        var precedence = isPostfix ? 12 : 11;
        var operandText = FormatExpression(operand, precedence);
        var operatorText = GetOperatorText(expression.Operator);
        var result = isPostfix ? operandText + operatorText : operatorText + operandText;
        return parentPrecedence > precedence ? "(" + result + ")" : result;
    }

    private string FormatMemberAccess(Avm1SourceExpression expression)
    {
        var target = FormatExpression(GetArena().GetChild(expression, 0), 12);
        var memberIndex = GetArena().GetChild(expression, 1);
        if (expression.Flags.HasFlag(Avm1SourceExpressionFlags.ComputedMember))
            return target + "[" + FormatExpression(memberIndex) + "]";

        if (TryGetStringLiteral(memberIndex, out var memberName))
            return target + "." + memberName;

        return target + "." + FormatExpression(memberIndex, 12);
    }

    private string FormatArguments(Avm1SourceExpression expression, int firstArgument)
    {
        if (firstArgument >= expression.Children.Count)
            return string.Empty;

        var arguments = new string[expression.Children.Count - firstArgument];
        for (var i = firstArgument; i < expression.Children.Count; i++)
            arguments[i - firstArgument] = FormatExpression(GetArena().GetChild(expression, i));
        return string.Join(", ", arguments);
    }

    private string FormatObjectLiteral(Avm1SourceExpression expression)
    {
        if (expression.Children.Count == 0)
            return string.Empty;

        var properties = new string[expression.Children.Count / 2];
        for (var i = 0; i + 1 < expression.Children.Count; i += 2)
        {
            var key = GetArena().GetChild(expression, i);
            var value = GetArena().GetChild(expression, i + 1);
            properties[i / 2] = FormatObjectKey(key) + ": " + FormatExpression(value);
        }
        return string.Join(", ", properties);
    }

    private string FormatFunctionLiteral(
        SourceExpressionIndex expressionIndex,
        bool requireDeclaration)
    {
        var expression = GetArena()[expressionIndex];
        if (expression.Kind is not Avm1SourceExpressionKind.FunctionLiteral ||
            !expression.Function.IsValid)
        {
            throw new InvalidOperationException("Source HIR function expression has an invalid layout.");
        }

        var function = GetArena()[expression.Function];
        if (!function.Body.IsValid ||
            requireDeclaration != function.Flags.HasFlag(Avm1SourceFunctionFlags.Declaration))
        {
            throw new InvalidOperationException("Source HIR function declaration state is inconsistent.");
        }

        var result = new System.Text.StringBuilder();
        result.Append("function");
        if (function.NameSymbol.IsValid)
            result.Append(' ').Append(GetSymbolName(function.NameSymbol));
        result.Append('(');
        for (var i = 0; i < function.Parameters.Count; i++)
        {
            if (i > 0)
                result.Append(", ");
            var parameter = GetArena().GetParameter(function, i);
            result.Append(GetSymbolName(parameter));
            result.Append(GetSymbolTypeSuffix(parameter));
        }
        result.Append(')');
        if (_projection?.TryGetInferredReturnType(
                GetArena(),
                function.Index,
                out var inferredReturnType) is true)
        {
            result.Append(FormatTypeSuffix(inferredReturnType));
        }
        result.AppendLine();
        AppendIndent(result, _indent);
        result.AppendLine("{");

        using var bodyWriter = new StringWriter(CultureInfo.InvariantCulture);
        new Avm1SourceAs2Emitter(bodyWriter, _indent + 1, _projection)
            .Write(GetArena(), function.Body);
        result.Append(bodyWriter.ToString());
        AppendIndent(result, _indent);
        result.Append('}');
        return result.ToString();
    }

    private string FormatObjectKey(SourceExpressionIndex index)
    {
        if (TryGetStringLiteral(index, out var name) && IsIdentifier(name))
            return name;
        return FormatExpression(index);
    }

    private bool TryGetStringLiteral(SourceExpressionIndex index, out string value)
    {
        value = string.Empty;
        if (!index.IsValid)
            return false;

        var expression = GetArena()[index];
        if (expression.Kind is not Avm1SourceExpressionKind.Literal || !expression.Literal.IsValid)
            return false;

        var literal = GetArena()[expression.Literal];
        if (literal.Kind is not Avm1SourceLiteralKind.String || !literal.StringValue.IsValid)
            return false;

        value = GetArena()[literal.StringValue];
        return true;
    }

    private string GetSymbolName(SourceSymbolIndex index)
    {
        var symbol = GetArena()[index];
        return GetArena()[symbol.Name];
    }

    private string GetOpaqueDescription(
        SourceOpaqueIndex opaque,
        SourceStringIndex fallbackName,
        string fallback)
    {
        if (opaque.IsValid)
        {
            var region = GetArena()[opaque];
            if (region.Description.IsValid)
                return GetArena()[region.Description];
        }

        return fallbackName.IsValid
            ? GetArena()[fallbackName]
            : fallback;
    }

    private string GetSymbolTypeSuffix(SourceSymbolIndex index)
    {
        if (!index.IsValid)
            return string.Empty;
        var symbol = GetArena()[index];
        if (symbol.DeclaredType.IsValid)
            return FormatTypeSuffix(GetArena()[symbol.DeclaredType]);
        if (_projection?.TryGetInferredType(
                GetArena(),
                index,
                out var inferredType) is true)
        {
            return FormatTypeSuffix(inferredType);
        }
        return string.Empty;
    }

    private string FormatTypeSuffix(Avm1SourceType type)
    {
        var name = type.Kind switch
        {
            Avm1SourceTypeKind.Boolean => "Boolean",
            Avm1SourceTypeKind.Number => "Number",
            Avm1SourceTypeKind.String => "String",
            Avm1SourceTypeKind.Object => "Object",
            Avm1SourceTypeKind.Array => "Array",
            Avm1SourceTypeKind.Function => "Function",
            Avm1SourceTypeKind.Void => "Void",
            Avm1SourceTypeKind.Nominal when type.Name.IsValid =>
                _projection?.GetTypeSpelling(
                    new Avm1SourceQualifiedName(GetArena()[type.Name])) ??
                new Avm1SourceQualifiedName(GetArena()[type.Name]).SimpleName,
            _ => null
        };
        return name is null ? string.Empty : ": " + name;
    }

    private string FormatTypeSuffix(Avm1SourceTypeReference? type)
    {
        if (type is null)
            return string.Empty;
        return ": " + (_projection?.GetTypeSpelling(type.Name) ?? type.Name.SimpleName);
    }

    private bool TryGetReferencedSymbol(
        SourceExpressionIndex expressionIndex,
        out SourceSymbolIndex symbol)
    {
        symbol = SourceSymbolIndex.Invalid;
        if (!expressionIndex.IsValid)
            return false;
        var expression = GetArena()[expressionIndex];
        if (expression.Kind is not Avm1SourceExpressionKind.SymbolReference ||
            !expression.Symbol.IsValid)
        {
            return false;
        }
        symbol = expression.Symbol;
        return true;
    }

    private string GetLabelName(SourceLabelIndex index)
    {
        var label = GetArena()[index];
        return GetArena()[label.Name];
    }

    private static string GetOperatorText(Avm1SourceOperator @operator) => @operator switch
    {
        Avm1SourceOperator.Assign => "=",
        Avm1SourceOperator.AddAssign => "+=",
        Avm1SourceOperator.SubtractAssign => "-=",
        Avm1SourceOperator.MultiplyAssign => "*=",
        Avm1SourceOperator.DivideAssign => "/=",
        Avm1SourceOperator.ModuloAssign => "%=",
        Avm1SourceOperator.BitAndAssign => "&=",
        Avm1SourceOperator.BitOrAssign => "|=",
        Avm1SourceOperator.BitXorAssign => "^=",
        Avm1SourceOperator.ShiftLeftAssign => "<<=",
        Avm1SourceOperator.ShiftRightAssign => ">>=",
        Avm1SourceOperator.ShiftRightUnsignedAssign => ">>>=",
        Avm1SourceOperator.PrefixIncrement or Avm1SourceOperator.PostfixIncrement => "++",
        Avm1SourceOperator.PrefixDecrement or Avm1SourceOperator.PostfixDecrement => "--",
        Avm1SourceOperator.LogicalNot => "!",
        Avm1SourceOperator.BitwiseNot => "~",
        Avm1SourceOperator.TypeOf => "typeof ",
        Avm1SourceOperator.UnaryPlus => "+",
        Avm1SourceOperator.UnaryMinus => "-",
        Avm1SourceOperator.Void => "void ",
        Avm1SourceOperator.Add => "+",
        Avm1SourceOperator.Subtract => "-",
        Avm1SourceOperator.Multiply => "*",
        Avm1SourceOperator.Divide => "/",
        Avm1SourceOperator.Modulo => "%",
        Avm1SourceOperator.Equal => "==",
        Avm1SourceOperator.NotEqual => "!=",
        Avm1SourceOperator.StrictEqual => "===",
        Avm1SourceOperator.StrictNotEqual => "!==",
        Avm1SourceOperator.Less => "<",
        Avm1SourceOperator.LessOrEqual => "<=",
        Avm1SourceOperator.Greater => ">",
        Avm1SourceOperator.GreaterOrEqual => ">=",
        Avm1SourceOperator.LogicalAnd => "&&",
        Avm1SourceOperator.LogicalOr => "||",
        Avm1SourceOperator.BitAnd => "&",
        Avm1SourceOperator.BitOr => "|",
        Avm1SourceOperator.BitXor => "^",
        Avm1SourceOperator.ShiftLeft => "<<",
        Avm1SourceOperator.ShiftRight => ">>",
        Avm1SourceOperator.ShiftRightUnsigned => ">>>",
        Avm1SourceOperator.In => "in",
        Avm1SourceOperator.InstanceOf => "instanceof",
        _ => throw new InvalidOperationException($"Source HIR operator {@operator} has no AS2 spelling.")
    };

    private static int GetPrecedence(Avm1SourceOperator @operator) => @operator switch
    {
        Avm1SourceOperator.LogicalOr => 1,
        Avm1SourceOperator.LogicalAnd => 2,
        Avm1SourceOperator.BitOr => 3,
        Avm1SourceOperator.BitXor => 4,
        Avm1SourceOperator.BitAnd => 5,
        Avm1SourceOperator.Equal or
            Avm1SourceOperator.NotEqual or
            Avm1SourceOperator.StrictEqual or
            Avm1SourceOperator.StrictNotEqual => 6,
        Avm1SourceOperator.Less or
            Avm1SourceOperator.LessOrEqual or
            Avm1SourceOperator.Greater or
            Avm1SourceOperator.GreaterOrEqual or
            Avm1SourceOperator.In or
            Avm1SourceOperator.InstanceOf => 7,
        Avm1SourceOperator.ShiftLeft or
            Avm1SourceOperator.ShiftRight or
            Avm1SourceOperator.ShiftRightUnsigned => 8,
        Avm1SourceOperator.Add or Avm1SourceOperator.Subtract => 9,
        Avm1SourceOperator.Multiply or
            Avm1SourceOperator.Divide or
            Avm1SourceOperator.Modulo => 10,
        _ => 0
    };

    private string FormatLiteral(Avm1SourceLiteral literal) => literal.Kind switch
    {
        Avm1SourceLiteralKind.Undefined => "undefined",
        Avm1SourceLiteralKind.Null => "null",
        Avm1SourceLiteralKind.Boolean => literal.BooleanValue ? "true" : "false",
        Avm1SourceLiteralKind.Integer => literal.IntegerValue.ToString(CultureInfo.InvariantCulture),
        Avm1SourceLiteralKind.Number => literal.NumberValue.ToString(CultureInfo.InvariantCulture),
        Avm1SourceLiteralKind.String => FormatStringLiteral(GetArena()[literal.StringValue]),
        _ => throw new InvalidOperationException($"Unknown Source HIR literal kind {literal.Kind}.")
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
        if (value.Length == 0 ||
            value[0] != '_' && value[0] != '$' && !char.IsAsciiLetter(value[0]))
        {
            return false;
        }

        for (var i = 1; i < value.Length; i++)
        {
            if (value[i] != '_' && value[i] != '$' && !char.IsAsciiLetterOrDigit(value[i]))
                return false;
        }
        return true;
    }

    private void WriteIndent()
    {
        for (var i = 0; i < _indent; i++)
            _writer.Write("    ");
    }

    private static void AppendIndent(System.Text.StringBuilder builder, int indent)
    {
        for (var i = 0; i < indent; i++)
            builder.Append("    ");
    }

    private Avm1SourceArena GetArena() => _arena ??
        throw new InvalidOperationException("No Source HIR method is being emitted.");
}
