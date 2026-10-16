using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Archy.Generators;

[Generator]
public sealed class PersistenceGenerator : IIncrementalGenerator {
  private static readonly DiagnosticDescriptor s_invalid = new(
    "ECS002", "Invalid persistence declaration", "Invalid persistence declaration '{0}': {1}",
    "Archy", DiagnosticSeverity.Error, isEnabledByDefault: true
  );

  public void Initialize(IncrementalGeneratorInitializationContext context) {
    var types = context.SyntaxProvider.CreateSyntaxProvider(
      static (node, _) => node is TypeDeclarationSyntax { AttributeLists.Count: > 0 },
      static (syntax, cancellation) => (INamedTypeSymbol)syntax.SemanticModel.GetDeclaredSymbol(syntax.Node, cancellation)!
    ).Where(static type => type.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString()
      is "Archy.ComponentKeyAttribute" or "Archy.TagNameAttribute"));
    context.RegisterSourceOutput(types.Collect(), static (output, symbols) => Emit(output, symbols));
  }

  private static void Emit(SourceProductionContext output, ImmutableArray<INamedTypeSymbol> symbols) {
    var catalog = new StringBuilder("#nullable enable\nnamespace Archy.Generated {\ninternal static class __PersistenceCatalog {\n");
    catalog.AppendLine("[global::System.Runtime.CompilerServices.ModuleInitializer] internal static void Register() {");
    var keys = new HashSet<string>(StringComparer.Ordinal);
    var count = 0;

    foreach (var type in symbols.Distinct(SymbolEqualityComparer.Default).Cast<INamedTypeSymbol>()) {
      var attribute = type.GetAttributes().First(item => item.AttributeClass?.ToDisplayString()
        is "Archy.ComponentKeyAttribute" or "Archy.TagNameAttribute");
      var tag = attribute.AttributeClass!.Name == "TagNameAttribute";
      var key = attribute.ConstructorArguments[0].Value as string;
      var version = attribute.NamedArguments.FirstOrDefault(item => item.Key == "Version").Value.Value as int? ?? 1;
      var codec = attribute.NamedArguments.FirstOrDefault(item => item.Key == "Codec").Value.Value as INamedTypeSymbol;
      var owners = new List<INamedTypeSymbol>();

      for (var owner = type; owner is not null; owner = owner.ContainingType) owners.Add(owner);

      var bridge = owners.Any(owner => owner.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal))
        || codec is not null && codec.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal);
      var validCodec = codec is null || !codec.IsAbstract && codec.Arity == 0
        && codec.AllInterfaces.Any(contract => contract.OriginalDefinition.ToDisplayString() == "Archy.IComponentCodec<T>"
          && SymbolEqualityComparer.Default.Equals(contract.TypeArguments[0], type))
        && (codec.IsValueType || codec.InstanceConstructors.Any(ctor => ctor.Parameters.Length == 0
          && ctor.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal));
      var valid = type.IsValueType && !type.IsRefLikeType && owners.All(owner => owner.Arity == 0)
        && type.AllInterfaces.Any(contract => contract.ToDisplayString() == (tag ? "Archy.ITag" : "Archy.IComponent"))
        && !(type.AllInterfaces.Any(contract => contract.ToDisplayString() == "Archy.ITag")
          && type.AllInterfaces.Any(contract => contract.ToDisplayString() == "Archy.IComponent"))
        && (!tag || !type.GetMembers().OfType<IFieldSymbol>().Any(field => !field.IsStatic))
        && !type.GetAttributes().Any(item => item.AttributeClass?.ToDisplayString() == "Archy.TransientAttribute")
        && !string.IsNullOrWhiteSpace(key) && version > 0 && validCodec
        && (!bridge || owners.All(owner => owner.DeclaringSyntaxReferences.All(reference =>
          reference.GetSyntax() is TypeDeclarationSyntax declaration && declaration.Modifiers.Any(SyntaxKind.PartialKeyword))));

      if (!valid || !keys.Add(key!)) {
        output.ReportDiagnostic(Diagnostic.Create(s_invalid, type.Locations.FirstOrDefault(), type.Name,
          "requires a unique nonempty key, valid struct contract/version/codec, and partial types for private catalog access"));

        continue;
      }

      var name = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
      var call = tag ? $"global::Archy.GeneratedComponents.RegisterTag<{name}>({SymbolDisplay.FormatLiteral(key!, true)});"
        : $"global::Archy.GeneratedComponents.RegisterComponent<{name}>({SymbolDisplay.FormatLiteral(key!, true)}, {version}, "
          + (codec is null ? "null" : $"new {codec.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}()") + ");";

      if (!bridge) catalog.AppendLine(call);
      else {
        var method = "__RegisterPersistence" + count++;
        var source = new StringBuilder("#nullable enable\n");
        var namespaced = !type.ContainingNamespace.IsGlobalNamespace;

        if (namespaced) source.Append("namespace ").Append(type.ContainingNamespace.ToDisplayString()).AppendLine(" {");

        owners.Reverse();

        for (var i = 0; i < owners.Count; i++) {
          var owner = owners[i];
          source.Append(owner.IsStatic ? "static " : owner.IsReadOnly ? "readonly " : "").Append("partial ")
            .Append(owner.IsRecord ? owner.IsValueType ? "record struct " : "record class " : owner.IsValueType ? "struct " : owner.TypeKind == TypeKind.Interface ? "interface " : "class ")
            .Append('@').Append(owner.Name).AppendLine(" {");

          if (i < owners.Count - 1) source.Append("internal static void ").Append(method).Append("() => @")
            .Append(owners[i + 1].Name).Append('.').Append(method).AppendLine("();");
        }

        source.Append("internal static void ").Append(method).Append("() { ").Append(call).AppendLine(" }");

        foreach (var _ in owners) source.AppendLine("}");
        if (namespaced) source.AppendLine("}");

        output.AddSource(type.ToDisplayString() + ".Persistence.g.cs", SourceText.From(source.ToString(), Encoding.UTF8));
        catalog.Append(owners[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).Append('.').Append(method).AppendLine("();");
      }
    }

    catalog.AppendLine("}\n}\n}");
    output.AddSource("Archy.PersistenceCatalog.g.cs", SourceText.From(catalog.ToString(), Encoding.UTF8));
  }
}
