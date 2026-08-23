namespace ShockwaveFlash.Avm1.Source.Emit;

public sealed class Avm1SourceFileAs2Emitter
{
    private readonly TextWriter _writer;
    private readonly Avm1SourceFileProjection? _projection;

    public Avm1SourceFileAs2Emitter(TextWriter writer) : this(writer, projection: null)
    {
    }

    internal Avm1SourceFileAs2Emitter(
        TextWriter writer,
        Avm1SourceFileProjection? projection)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writer = writer;
        _projection = projection;
    }

    public void Write(Avm1SourceFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (_projection is not null && !ReferenceEquals(_projection.SourceFile, file))
        {
            throw new ArgumentException(
                "The projection belongs to a different Source file.",
                nameof(file));
        }

        var imports = _projection?.Imports ?? file.Imports;
        for (var i = 0; i < imports.Count; i++)
        {
            _writer.Write("import ");
            _writer.Write(imports[i].Value);
            _writer.WriteLine(";");
        }

        if (imports.Count > 0 && file.Classes.Count > 0)
            _writer.WriteLine();

        for (var i = 0; i < file.Classes.Count; i++)
        {
            if (i > 0)
                _writer.WriteLine();
            WriteClass(file.Classes[i]);
        }
    }

    private void WriteClass(Avm1SourceClass sourceClass)
    {
        WriteModifiers(sourceClass.Modifiers, includeStatic: false);
        _writer.Write(sourceClass.Kind is Avm1SourceTypeDeclarationKind.Interface
            ? "interface "
            : "class ");
        _writer.Write(sourceClass.Name.Value);
        if (sourceClass.BaseType is not null)
        {
            _writer.Write(" extends ");
            _writer.Write(GetTypeSpelling(sourceClass.BaseType));
        }

        if (sourceClass.Interfaces.Count > 0)
        {
            _writer.Write(sourceClass.Kind is Avm1SourceTypeDeclarationKind.Interface
                ? " extends "
                : " implements ");
            for (var i = 0; i < sourceClass.Interfaces.Count; i++)
            {
                if (i > 0)
                    _writer.Write(", ");
                _writer.Write(GetTypeSpelling(sourceClass.Interfaces[i]));
            }
        }

        _writer.WriteLine();
        _writer.WriteLine("{");
        foreach (var member in sourceClass.Members)
        {
            switch (member)
            {
                case Avm1SourceField field:
                    WriteField(field);
                    break;
                case Avm1SourceMethodDeclaration method:
                    WriteMethod(method);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unknown Source HIR class member {member.GetType().Name}.");
            }
        }
        _writer.WriteLine("}");
    }

    private void WriteField(Avm1SourceField field)
    {
        _writer.Write("   ");
        WriteModifiers(field.Modifiers, includeStatic: true);
        _writer.Write("var ");
        _writer.Write(field.Name);
        var fieldType = field.DeclaredType;
        if (fieldType is null)
            _projection?.TryGetInferredType(field, out fieldType);
        WriteType(fieldType);
        if (field.Initializer is not null)
        {
            _writer.Write(" = ");
            new Avm1SourceAs2Emitter(_writer, initialIndent: 0, projection: _projection)
                .Write(field.Initializer);
        }
        _writer.WriteLine(";");
    }

    private void WriteMethod(Avm1SourceMethodDeclaration method)
    {
        _writer.WriteLine();
        _writer.Write("   ");
        WriteModifiers(method.Modifiers, includeStatic: true);
        _writer.Write("function ");
        if (method.Kind is Avm1SourceMethodKind.Getter)
            _writer.Write("get ");
        else if (method.Kind is Avm1SourceMethodKind.Setter)
            _writer.Write("set ");
        _writer.Write(method.Name);
        _writer.Write('(');
        WriteParameters(method.Body);
        _writer.Write(')');
        var returnType = method.DeclaredReturnType;
        if (returnType is null && method.Kind is not Avm1SourceMethodKind.Constructor)
            _projection?.TryGetInferredType(method, out returnType);
        WriteType(returnType);
        if (!method.HasBody)
        {
            _writer.WriteLine(";");
            return;
        }
        _writer.WriteLine();
        _writer.WriteLine("   {");
        new Avm1SourceAs2Emitter(_writer, initialIndent: 2, projection: _projection)
            .Write(method.Body);
        _writer.WriteLine("   }");
    }

    private void WriteParameters(Avm1SourceMethod method)
    {
        for (var i = 0; i < method.Parameters.Count; i++)
        {
            if (i > 0)
                _writer.Write(", ");

            var symbol = method.Arena[method.Parameters[i]];
            _writer.Write(method.Arena[symbol.Name]);
            if (symbol.DeclaredType.IsValid)
                WriteType(method.Arena, symbol.DeclaredType);
            else if (_projection?.TryGetInferredType(
                         method.Arena,
                         symbol.Index,
                         out var inferredType) is true)
                WriteType(inferredType);
        }
    }

    private void WriteType(Avm1SourceTypeReference? type)
    {
        if (type is null)
            return;
        _writer.Write(": ");
        _writer.Write(GetTypeSpelling(type.Name));
    }

    private void WriteType(Avm1SourceArena arena, SourceTypeIndex typeIndex)
    {
        var type = arena[typeIndex];
        _writer.Write(": ");
        _writer.Write(type.Kind switch
        {
            Avm1SourceTypeKind.Boolean => "Boolean",
            Avm1SourceTypeKind.Number => "Number",
            Avm1SourceTypeKind.String => "String",
            Avm1SourceTypeKind.Object => "Object",
            Avm1SourceTypeKind.Array => "Array",
            Avm1SourceTypeKind.Function => "Function",
            Avm1SourceTypeKind.Void => "Void",
            Avm1SourceTypeKind.Nominal when type.Name.IsValid =>
                GetTypeSpelling(new Avm1SourceQualifiedName(arena[type.Name])),
            _ => throw new InvalidOperationException(
                $"Source type {type.Kind} cannot be emitted as an AS2 declaration.")
        });
    }

    private string GetTypeSpelling(Avm1SourceQualifiedName type) =>
        _projection?.GetTypeSpelling(type) ?? type.SimpleName;

    private void WriteModifiers(
        Avm1SourceDeclarationModifiers modifiers,
        bool includeStatic)
    {
        if (modifiers.HasFlag(Avm1SourceDeclarationModifiers.Public))
            _writer.Write("public ");
        if (modifiers.HasFlag(Avm1SourceDeclarationModifiers.Private))
            _writer.Write("private ");
        if (modifiers.HasFlag(Avm1SourceDeclarationModifiers.Protected))
            _writer.Write("protected ");
        if (includeStatic && modifiers.HasFlag(Avm1SourceDeclarationModifiers.Static))
            _writer.Write("static ");
        if (modifiers.HasFlag(Avm1SourceDeclarationModifiers.Final))
            _writer.Write("final ");
        if (modifiers.HasFlag(Avm1SourceDeclarationModifiers.Override))
            _writer.Write("override ");
        if (modifiers.HasFlag(Avm1SourceDeclarationModifiers.Dynamic))
            _writer.Write("dynamic ");
        if (modifiers.HasFlag(Avm1SourceDeclarationModifiers.Intrinsic))
            _writer.Write("intrinsic ");
    }
}
