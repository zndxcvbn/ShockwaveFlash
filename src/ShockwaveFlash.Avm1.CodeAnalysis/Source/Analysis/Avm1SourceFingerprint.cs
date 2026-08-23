using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ShockwaveFlash.Avm1.Source;

public static class Avm1SourceFingerprint
{
    private const string Version = "avm1-source-fingerprint-v1";

    public static string ComputeMethod(Avm1SourceMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);
        using var writer = new FingerprintWriter();
        writer.Write(Version);
        writer.Write("method");
        WriteMethod(writer, method);
        return writer.GetHash();
    }

    public static string ComputeClass(Avm1SourceClass sourceClass)
    {
        ArgumentNullException.ThrowIfNull(sourceClass);
        using var writer = new FingerprintWriter();
        writer.Write(Version);
        writer.Write("class");
        WriteClass(writer, sourceClass, includeMethodBodies: true);
        return writer.GetHash();
    }

    public static string ComputeClassShape(Avm1SourceClass sourceClass)
    {
        ArgumentNullException.ThrowIfNull(sourceClass);
        using var writer = new FingerprintWriter();
        writer.Write(Version);
        writer.Write("class-shape");
        WriteClass(writer, sourceClass, includeMethodBodies: false);
        return writer.GetHash();
    }

    private static void WriteClass(
        FingerprintWriter writer,
        Avm1SourceClass sourceClass,
        bool includeMethodBodies)
    {
        writer.Write((int)sourceClass.Kind);
        writer.Write(sourceClass.Name.Value);
        writer.Write(sourceClass.BaseType?.Value);
        writer.Write((int)sourceClass.Modifiers);
        writer.Write(sourceClass.Origin.RuntimeName);
        writer.Write(sourceClass.Interfaces.Count);
        foreach (var interfaceName in sourceClass.Interfaces)
            writer.Write(interfaceName.Value);

        writer.Write(sourceClass.Members.Count);
        foreach (var member in sourceClass.Members)
        {
            writer.Write(member is Avm1SourceField ? "field" : "method");
            WriteMemberIdentity(writer, member);
            switch (member)
            {
                case Avm1SourceField field:
                    writer.Write(field.DeclaredType?.Name.Value);
                    writer.Write(field.InferredType?.Name.Value);
                    writer.Write(field.Initializer is not null);
                    if (field.Initializer is not null)
                    {
                        WriteArena(writer, field.Initializer.Arena);
                        writer.Write(field.Initializer.Expression.Value);
                    }
                    break;
                case Avm1SourceMethodDeclaration method:
                    writer.Write((int)method.Kind);
                    writer.Write(method.HasBody);
                    writer.Write(method.DeclaredReturnType?.Name.Value);
                    writer.Write(method.InferredReturnType?.Name.Value);
                    WriteMethodSignature(writer, method.Body);
                    if (includeMethodBodies)
                        WriteMethod(writer, method.Body);
                    break;
            }
        }

        writer.Write(sourceClass.Initializer is not null);
        if (sourceClass.Initializer is not { } initializer)
            return;

        WriteMethod(writer, initializer.Body);
        writer.Write(initializer.Steps.Count);
        foreach (var step in initializer.Steps)
        {
            writer.Write((int)step.Kind);
            writer.Write(step.Member is not null);
            if (step.Member is not null)
                WriteMemberIdentity(writer, step.Member);
            writer.Write(step.Statement.Value);
        }
    }

    private static void WriteMemberIdentity(
        FingerprintWriter writer,
        Avm1SourceClassMember member)
    {
        writer.Write(member.Name);
        writer.Write((int)member.Modifiers);
        writer.Write(member.Origin.RuntimeName);
        if (member is Avm1SourceMethodDeclaration method)
            writer.Write((int)method.Kind);
    }

    private static void WriteMethodSignature(
        FingerprintWriter writer,
        Avm1SourceMethod method)
    {
        writer.Write(method.Parameters.Count);
        foreach (var parameterIndex in method.Parameters)
        {
            var parameter = method.Arena[parameterIndex];
            writer.Write((int)parameter.Kind);
            writer.Write(parameter.Name.IsValid
                ? method.Arena[parameter.Name]
                : null);
            writer.Write(parameter.DeclaredType.Value);
            writer.Write(parameter.InferredType.Value);
            writer.Write((int)parameter.Flags);
        }
    }

    private static void WriteMethod(
        FingerprintWriter writer,
        Avm1SourceMethod method)
    {
        writer.Write(method.Body.Value);
        writer.Write(method.Parameters.Count);
        foreach (var parameter in method.Parameters)
            writer.Write(parameter.Value);
        WriteArena(writer, method.Arena);
    }

    private static void WriteArena(
        FingerprintWriter writer,
        Avm1SourceArena arena)
    {
        writer.Write(arena.Strings.Count);
        foreach (var value in arena.Strings)
            writer.Write(value);

        writer.Write(arena.Types.Count);
        foreach (var type in arena.Types)
        {
            writer.Write((int)type.Kind);
            writer.Write(type.Name.Value);
        }

        writer.Write(arena.Symbols.Count);
        foreach (var symbol in arena.Symbols)
        {
            writer.Write((int)symbol.Kind);
            writer.Write(symbol.Name.Value);
            writer.Write(symbol.DeclaredType.Value);
            writer.Write(symbol.InferredType.Value);
            writer.Write((int)symbol.Flags);
        }

        writer.Write(arena.Labels.Count);
        foreach (var label in arena.Labels)
            writer.Write(label.Name.Value);

        writer.Write(arena.Literals.Count);
        foreach (var literal in arena.Literals)
        {
            writer.Write((int)literal.Kind);
            writer.Write(literal.BooleanValue);
            writer.Write(literal.IntegerValue);
            writer.Write(literal.NumberValue);
            writer.Write(literal.StringValue.Value);
        }

        writer.Write(arena.Expressions.Count);
        foreach (var expression in arena.Expressions)
        {
            writer.Write((int)expression.Kind);
            writer.Write((int)expression.Operator);
            writer.Write(expression.Symbol.Value);
            writer.Write(expression.Name.Value);
            writer.Write(expression.Literal.Value);
            writer.Write(expression.Function.Value);
            writer.Write(expression.Opaque.Value);
            writer.Write((int)expression.Flags);
            writer.Write(expression.Children.Start);
            writer.Write(expression.Children.Count);
        }
        writer.Write(arena.ExpressionChildren.Count);
        foreach (var child in arena.ExpressionChildren)
            writer.Write(child.Value);

        writer.Write(arena.Statements.Count);
        foreach (var statement in arena.Statements)
        {
            writer.Write((int)statement.Kind);
            writer.Write(statement.Expression.Value);
            writer.Write(statement.SecondaryExpression.Value);
            writer.Write(statement.Symbol.Value);
            writer.Write(statement.Name.Value);
            writer.Write(statement.Label.Value);
            writer.Write(statement.Opaque.Value);
            writer.Write((int)statement.Flags);
            writer.Write(statement.Children.Start);
            writer.Write(statement.Children.Count);
            writer.Write(statement.Initializers.Start);
            writer.Write(statement.Initializers.Count);
            writer.Write(statement.Expressions.Start);
            writer.Write(statement.Expressions.Count);
        }
        writer.Write(arena.StatementChildren.Count);
        foreach (var child in arena.StatementChildren)
            writer.Write(child.Value);

        writer.Write(arena.Functions.Count);
        foreach (var function in arena.Functions)
        {
            writer.Write(function.NameSymbol.Value);
            writer.Write(function.Body.Value);
            writer.Write(function.Parameters.Start);
            writer.Write(function.Parameters.Count);
            writer.Write(function.Captures.Start);
            writer.Write(function.Captures.Count);
            writer.Write((int)function.Flags);
        }
        writer.Write(arena.FunctionSymbols.Count);
        foreach (var symbol in arena.FunctionSymbols)
            writer.Write(symbol.Value);

        writer.Write(arena.OpaqueRegions.Count);
        foreach (var opaque in arena.OpaqueRegions)
        {
            writer.Write((int)opaque.Kind);
            writer.Write(opaque.Description.Value);
            writer.Write(opaque.Bytecode.Start);
            writer.Write(opaque.Bytecode.Count);
        }
        writer.Write(arena.OpaqueBytecode.Count);
        foreach (var value in arena.OpaqueBytecode)
            writer.Write(value);
    }

    private sealed class FingerprintWriter : IDisposable
    {
        private readonly IncrementalHash _hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private bool _completed;

        public void Write(bool value) => Write(value ? 1 : 0);

        public void Write(byte value) => _hash.AppendData([value]);

        public void Write(int value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
            _hash.AppendData(bytes);
        }

        public void Write(double value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(long)];
            BinaryPrimitives.WriteInt64LittleEndian(
                bytes,
                BitConverter.DoubleToInt64Bits(value));
            _hash.AppendData(bytes);
        }

        public void Write(string? value)
        {
            if (value is null)
            {
                Write(-1);
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(value);
            Write(bytes.Length);
            _hash.AppendData(bytes);
        }

        public string GetHash()
        {
            if (_completed)
                throw new InvalidOperationException("The fingerprint is already complete.");
            _completed = true;
            return Convert.ToHexString(_hash.GetHashAndReset());
        }

        public void Dispose() => _hash.Dispose();
    }
}
