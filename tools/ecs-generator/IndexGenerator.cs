using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Archy.Generators;

[Generator]
public sealed class IndexGenerator : IIncrementalGenerator {
  private static readonly DiagnosticDescriptor s_invalid = new(
    "ECS001", "Invalid indexed member", "Invalid index declaration '{0}': {1}",
    "Archy", DiagnosticSeverity.Error, isEnabledByDefault: true
  );

  public void Initialize(IncrementalGeneratorInitializationContext context) {
    var members = context.SyntaxProvider.CreateSyntaxProvider(
      static (node, _) => node is TypeDeclarationSyntax,
      static (syntax, cancellation) => ((INamedTypeSymbol)syntax.SemanticModel.GetDeclaredSymbol(syntax.Node, cancellation)!)
        .GetMembers().Where(member => member.GetAttributes().Any(attribute =>
          attribute.AttributeClass?.ToDisplayString() is "Archy.IndexedAttribute" or "Archy.EntityReferenceAttribute")).ToImmutableArray()
    );
    context.RegisterSourceOutput(members.Collect(), static (output, groups) =>
      Emit(output, groups.SelectMany(group => group).Distinct(SymbolEqualityComparer.Default).ToImmutableArray()));
  }

  private static void Emit(SourceProductionContext output, ImmutableArray<ISymbol> symbols) {
    var catalog = new StringBuilder("#nullable enable\nnamespace Archy.Generated {\ninternal static class __IndexCatalog {\n");
    catalog.AppendLine("[global::System.Runtime.CompilerServices.ModuleInitializer]");
    catalog.AppendLine("internal static void Register() {");
    var number = 0;

    foreach (var group in symbols.GroupBy(symbol => symbol.ContainingType.ToDisplayString())) {
      var member = group.First();
      var owner = member.ContainingType;
      var property = member as IPropertySymbol;
      var method = member as IMethodSymbol;
      var key = (member as IFieldSymbol)?.Type ?? property?.Type ?? method?.ReturnType;
      var owners = new List<INamedTypeSymbol>();

      for (var type = owner; type is not null; type = type.ContainingType) owners.Add(type);

      var validOwners = owners.All(type => type.Arity == 0 && type.DeclaringSyntaxReferences.All(reference =>
        reference.GetSyntax() is TypeDeclarationSyntax declaration && declaration.Modifiers.Any(SyntaxKind.PartialKeyword)));
      var validMethod = method is null || method is { MethodKind: MethodKind.Ordinary, Parameters.Length: 0, Arity: 0,
        ReturnsVoid: false, ReturnsByRef: false, ReturnsByRefReadonly: false, IsAsync: false };
      var attribute = member.GetAttributes().FirstOrDefault(item => item.AttributeClass?.ToDisplayString() == "Archy.IndexedAttribute");
      var reference = member.GetAttributes().FirstOrDefault(item => item.AttributeClass?.ToDisplayString() == "Archy.EntityReferenceAttribute");

      if (attribute is null || reference is not null && key?.ToDisplayString() != "Archy.Entity"
        || key?.ToDisplayString() == "Archy.Entity" && reference is null) {
        output.ReportDiagnostic(Diagnostic.Create(s_invalid, member.Locations.FirstOrDefault(), member.Name, "EntityReference requires an Entity-valued Indexed member"));

        continue;
      }
      if (reference is not null && reference.NamedArguments.FirstOrDefault(item => item.Key == "OnDelete").Value.Value is int deletionPolicy
        && deletionPolicy is not (0 or 1)) {
        output.ReportDiagnostic(Diagnostic.Create(s_invalid, member.Locations.FirstOrDefault(), member.Name, "unsupported target deletion policy"));

        continue;
      }

      var comparer = attribute.NamedArguments.FirstOrDefault(item => item.Key == "Comparer").Value.Value as INamedTypeSymbol;
      var validComparer = comparer is null || !comparer.IsAbstract && comparer.Arity == 0
        && comparer.AllInterfaces.Any(type => type.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.IEqualityComparer<T>"
          && SymbolEqualityComparer.Default.Equals(type.TypeArguments[0], key))
        && (comparer.IsValueType || comparer.InstanceConstructors.Any(ctor => ctor.Parameters.Length == 0
          && ctor.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal));

      if (group.Count() != 1 || !owner.IsValueType || owner.IsRefLikeType || !validOwners || member.IsStatic
        || !owner.AllInterfaces.Any(type => type.ToDisplayString() == "Archy.IComponent")
        || owner.AllInterfaces.Any(type => type.ToDisplayString() == "Archy.ITag")
        || key is null || !SafeKey(key, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default)) || !validMethod || !validComparer || reference is not null && comparer is not null
        || property is { IsIndexer: true } || property is { GetMethod: null }
        || property is { ExplicitInterfaceImplementations.Length: > 0 }
        || property is { ReturnsByRef: true } || property is { ReturnsByRefReadonly: true }) {
        output.ReportDiagnostic(Diagnostic.Create(s_invalid, member.Locations.FirstOrDefault(), member.Name,
          "requires one instance value member, an immutable non-null key, partial non-generic enclosing types, and a compatible constructible comparer"));

        continue;
      }

      var ownerName = owner.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
      var keyName = key.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
      var unique = attribute.NamedArguments.Any(item => item.Key == "Unique" && item.Value.Value is true);
      var primitive = key.SpecialType is >= SpecialType.System_Boolean and <= SpecialType.System_Decimal
        || key.TypeKind == TypeKind.Enum || key.ToDisplayString() == "System.Guid";
      var comparerExpression = comparer is not null ? $"new {comparer.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}()"
        : primitive ? $"global::System.Collections.Generic.EqualityComparer<{keyName}>.Default" : "null";
      var register = "__RegisterIndex" + number++;
      var source = new StringBuilder("#nullable enable\n");
      var namespaced = !owner.ContainingNamespace.IsGlobalNamespace;

      if (namespaced) source.Append("namespace ").Append(owner.ContainingNamespace.ToDisplayString()).AppendLine(" {");

      owners.Reverse();

      for (var i = 0; i < owners.Count; i++) {
        var type = owners[i];
        source.Append(type.IsStatic ? "static " : type.IsReadOnly ? "readonly " : "").Append("partial ");
        source.Append(type.IsRecord ? type.IsValueType ? "record struct " : "record class " : type.IsValueType ? "struct " : type.TypeKind == TypeKind.Interface ? "interface " : "class ")
          .Append('@').Append(type.Name).AppendLine(" {");

        if (i < owners.Count - 1) source.Append("internal static void ").Append(register).Append("() => @")
          .Append(owners[i + 1].Name).Append('.').Append(register).AppendLine("();");
      }

      source.Append("private static ").Append(keyName).Append(" __GetIndexValue(in ").Append(ownerName)
        .Append(" component) => component.@").Append(member.Name).Append(method is null ? ";\n" : "();\n");
      source.Append("internal static void ").Append(register).Append("() => global::Archy.GeneratedComponents.RegisterIndex<")
        .Append(ownerName).Append(", ").Append(keyName).Append(">(__GetIndexValue, ").Append(unique ? "true" : "false")
        .Append(", ").Append(comparerExpression);

      if (reference is not null) {
        var deletion = reference.NamedArguments.FirstOrDefault(item => item.Key == "OnDelete").Value.Value;
        var optional = reference.NamedArguments.Any(item => item.Key == "Optional" && item.Value.Value is true);
        source.Append(", (global::Archy.TargetDeletion)").Append(deletion ?? 0).Append(", ").Append(optional ? "true" : "false");
      }

      source.AppendLine(");");

      foreach (var _ in owners) source.AppendLine("}");
      if (namespaced) source.AppendLine("}");

      output.AddSource(owner.ToDisplayString() + ".Index.g.cs", SourceText.From(source.ToString(), Encoding.UTF8));
      catalog.Append(owners[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).Append('.').Append(register).AppendLine("();");
    }

    catalog.AppendLine("}\n}\n}");
    output.AddSource("Archy.IndexCatalog.g.cs", SourceText.From(catalog.ToString(), Encoding.UTF8));
  }

  private static bool SafeKey(ITypeSymbol key, HashSet<ITypeSymbol> seen) {
    if (key.NullableAnnotation == NullableAnnotation.Annotated || key is IArrayTypeSymbol || key.TypeKind is TypeKind.Pointer or TypeKind.TypeParameter
      || key is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } || key.SpecialType == SpecialType.System_Void) return false;
    if (key.SpecialType == SpecialType.System_String || key.ToDisplayString() == "Archy.Entity" || key.TypeKind == TypeKind.Enum) return true;
    if (key.SpecialType is >= SpecialType.System_Boolean and <= SpecialType.System_Decimal) return true;
    if (!key.IsValueType || key is INamedTypeSymbol { IsRefLikeType: true }) return false;
    if (!seen.Add(key)) return true;

    return key.GetMembers().OfType<IFieldSymbol>().Where(field => !field.IsStatic).All(field => SafeKey(field.Type, seen));
  }
}
