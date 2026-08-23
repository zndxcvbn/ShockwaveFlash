namespace ShockwaveFlash.Avm1.Source;

public static class Avm1ReferenceCatalogs
{
    public static Avm1ReferenceCatalog ActionScript2 { get; } =
        new(CreateActionScript2Classes());

    public static Avm1ReferenceCatalog Flash { get; } =
        new(CreateFlashClasses());

    public static Avm1ReferenceCatalog Scaleform { get; } =
        new(CreateScaleformClasses());

    public static Avm1ReferenceCatalog SkyUi { get; } =
        new(CreateSkyUiClasses());

    public static IAvm1ReferenceProvider Default { get; } =
        new Avm1CompositeReferenceProvider([
            ActionScript2,
            Flash,
            Scaleform,
            SkyUi
        ]);

    private static IEnumerable<Avm1SourceReferenceClass>
        CreateActionScript2Classes()
    {
        yield return Class("Array", members:
        [
            Constructor("Array", [Rest("values")]),
            StaticField("CASEINSENSITIVE", "Number"),
            StaticField("DESCENDING", "Number"),
            StaticField("NUMERIC", "Number"),
            Field("length", "Number"),
            Method("concat", "Array", [Rest("values")]),
            Method("join", "String", [Optional("separator", "String")]),
            Method("pop"),
            Method("push", "Number", [Rest("values")]),
            Method("reverse", "Array"),
            Method("shift"),
            Method("slice", "Array", [
                Optional("start", "Number"),
                Optional("end", "Number")
            ]),
            Method("sort", "Array", [Rest("options")]),
            Method("sortOn", "Array", [
                Parameter("fieldName"),
                Optional("options")
            ]),
            Method("splice", "Array", [
                Parameter("start", "Number"),
                Parameter("deleteCount", "Number"),
                Rest("values")
            ]),
            Method("unshift", "Number", [Rest("values")])
        ]);
        yield return Class("Boolean", members: [Constructor("Boolean")]);
        yield return Class("Color");
        yield return Class("Date", members: [Constructor("Date", [Rest("values")])]);
        yield return Class("Function");
        yield return Class("Key");
        yield return Class("LoadVars", members:
        [
            Constructor("LoadVars"),
            Method("load", "Boolean", [Parameter("url", "String")])
        ]);
        yield return Class("Math");
        yield return Class("Mouse", members:
        [
            StaticMethod("getTopMostEntity", "Object")
        ]);
        yield return Class("MovieClip", members:
        [
            Field("_parent", "MovieClip"),
            Field("_visible", "Boolean"),
            Field("_x", "Number"),
            Field("_y", "Number"),
            Field("_width", "Number"),
            Field("_height", "Number")
        ]);
        yield return Class("Number", members: [Constructor("Number")]);
        yield return Class("Object", members: [Constructor("Object")]);
        yield return Class("Selection", members:
        [
            StaticMethod("getControllerFocusGroup", "Number", [
                Parameter("controllerIndex", "Number")
            ]),
            StaticMethod("getControllerMaskByFocusGroup", "Number", [
                Parameter("focusIndex", "Number")
            ]),
            StaticMethod("getFocus", "String", [
                Optional("controllerIndex", "Number")
            ]),
            StaticMethod("setFocus", "Void", [
                Parameter("target"),
                Optional("controllerIndex", "Number")
            ]),
            StaticMethod("findFocus", "Object", [Rest("arguments")])
        ]);
        yield return Class("String", members:
        [
            Constructor("String"),
            Field("length", "Number"),
            Method("charAt", "String", [Parameter("index", "Number")]),
            Method("indexOf", "Number", [
                Parameter("value", "String"),
                Optional("start", "Number")
            ]),
            Method("split", "Array", [Optional("separator", "String")]),
            Method("substr", "String", [
                Parameter("start", "Number"),
                Optional("length", "Number")
            ]),
            Method("substring", "String", [
                Parameter("start", "Number"),
                Optional("end", "Number")
            ])
        ]);
        yield return Class("System");
        yield return Class("TextField", baseType: "MovieClip", members:
        [
            Field("text", "String"),
            Field("htmlText", "String"),
            Field("multiline", "Boolean")
        ]);
        yield return Class("TextField.StyleSheet");
    }

    private static IEnumerable<Avm1SourceReferenceClass> CreateFlashClasses()
    {
        yield return Class("flash.display.BitmapData");
        yield return Class("flash.external.ExternalInterface", members:
        [
            StaticMethod("available", "Boolean"),
            StaticMethod("call", null, [Rest("arguments")])
        ]);
        yield return Class("flash.geom.ColorTransform");
        yield return Class("flash.geom.Matrix");
        yield return Class("flash.geom.Rectangle", members:
        [
            Field("x", "Number"),
            Field("y", "Number"),
            Field("width", "Number"),
            Field("height", "Number")
        ]);
        yield return Class("flash.geom.Transform");
    }

    private static IEnumerable<Avm1SourceReferenceClass> CreateScaleformClasses()
    {
        yield return Class("mx.transitions.Tween");
        yield return Class("mx.transitions.easing.None");
        yield return Class("mx.utils.Delegate", members:
        [
            StaticMethod("create", "Function", [
                Parameter("scope", "Object"),
                Parameter("method", "Function")
            ])
        ]);
        yield return Class("gfx.io.GameDelegate", members:
        [
            StaticMethod("call", null, [Rest("arguments")])
        ]);
    }

    private static IEnumerable<Avm1SourceReferenceClass> CreateSkyUiClasses()
    {
        yield return Class("Shared.PlatformChangeUser.PlatformChange");
        yield return Class("skyui.components.list.AlphaEntryFormatter");
        yield return Class("skyui.components.list.BasicEntryFactory");
        yield return Class("skyui.components.list.ButtonEntryFormatter");
        yield return Interface("skyui.components.list.IEntryClipBuilder");
        yield return Interface("skyui.components.list.IEntryFormatter");
        yield return Class("skyui.filter.ItemSortingFilter");
        yield return Class("skyui.util.ConfigLoader");
    }

    private static Avm1SourceReferenceClass Class(
        string name,
        string? baseType = null,
        IReadOnlyList<Avm1SourceReferenceMember>? members = null) =>
        new(
            new Avm1SourceQualifiedName(name),
            members,
            baseType is null ? null : Type(baseType));

    private static Avm1SourceReferenceClass Interface(string name) =>
        new(
            new Avm1SourceQualifiedName(name),
            kind: Avm1SourceTypeDeclarationKind.Interface);

    private static Avm1SourceReferenceMember Constructor(
        string name,
        IReadOnlyList<Avm1SourceReferenceParameter>? parameters = null) =>
        new(name, Avm1SourceReferenceMemberKind.Constructor, parameters: parameters);

    private static Avm1SourceReferenceMember Field(string name, string type) =>
        new(name, Avm1SourceReferenceMemberKind.Field, Type(type));

    private static Avm1SourceReferenceMember StaticField(string name, string type) =>
        new(
            name,
            Avm1SourceReferenceMemberKind.Field,
            Type(type),
            modifiers: Avm1SourceDeclarationModifiers.Public |
                Avm1SourceDeclarationModifiers.Static);

    private static Avm1SourceReferenceMember Method(
        string name,
        string? type = null,
        IReadOnlyList<Avm1SourceReferenceParameter>? parameters = null) =>
        new(
            name,
            Avm1SourceReferenceMemberKind.Method,
            type is null ? null : Type(type),
            parameters);

    private static Avm1SourceReferenceMember StaticMethod(
        string name,
        string? type = null,
        IReadOnlyList<Avm1SourceReferenceParameter>? parameters = null) =>
        new(
            name,
            Avm1SourceReferenceMemberKind.Method,
            type is null ? null : Type(type),
            parameters,
            Avm1SourceDeclarationModifiers.Public |
                Avm1SourceDeclarationModifiers.Static);

    private static Avm1SourceReferenceParameter Parameter(
        string name,
        string? type = null) =>
        new(name, type is null ? null : Type(type));

    private static Avm1SourceReferenceParameter Optional(
        string name,
        string? type = null) =>
        new(
            name,
            type is null ? null : Type(type),
            Avm1SourceReferenceParameterFlags.Optional);

    private static Avm1SourceReferenceParameter Rest(string name) =>
        new(
            name,
            flags: Avm1SourceReferenceParameterFlags.Rest);

    private static Avm1SourceTypeReference Type(string name) =>
        new(new Avm1SourceQualifiedName(name));
}
