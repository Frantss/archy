using Archy;
using Archy.Adapters.Arch;
using Archy.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Runtime.Loader;

var parseOptions = new CSharpParseOptions(LanguageVersion.CSharp14);
var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
  .Append(typeof(IComponent).Assembly.Location)
  .Append(typeof(ArchStorage).Assembly.Location)
  .Select(path => MetadataReference.CreateFromFile(path)).ToArray();
var count = 0;

(Compilation Compilation, GeneratorDriverRunResult Result) Generate(string source) {
  var compilation = CSharpCompilation.Create(
    "EcsFixture" + Guid.NewGuid().ToString("N"),
    [CSharpSyntaxTree.ParseText("using Archy; using Archy.Adapters.Arch; " + source, parseOptions)], references,
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable)
  );
  GeneratorDriver driver = CSharpGeneratorDriver.Create([new IndexGenerator().AsSourceGenerator(), new PersistenceGenerator().AsSourceGenerator()], parseOptions: parseOptions);
  driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var generated, out _);

  return (generated, driver.GetRunResult());
}

void Require(bool condition, string message) {
  if (!condition) throw new InvalidOperationException(message);

  count++;
}

var valid = Generate("""
  public partial record struct Identity([property: Indexed(Unique = true)] int Value) : IComponent;
  public static class Consumer {
    public static bool Run() {
      using var store = new EntityStore(new ArchStorage());
      var entity = store.Create();
      entity.Add(new Identity(7));
      return store.ComponentIndex<Identity, int>()[7][0] == entity;
    }
  }
  """);
Require(valid.Result.Diagnostics.IsEmpty, "Valid indexed component generates without diagnostics.");
using var stream = new MemoryStream();
var emitted = valid.Compilation.Emit(stream);
Require(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
stream.Position = 0;
var assembly = AssemblyLoadContext.Default.LoadFromStream(stream);
Require((bool)assembly.GetType("Consumer")!.GetMethod("Run")!.Invoke(null, null)!, "Compiled generated catalog registers a working index.");

var primitives = Generate("""
  public partial record struct Wide([property: Indexed(Unique = true)] ulong Value) : IComponent;
  public partial record struct Token([property: Indexed(Unique = true)] System.Guid Value) : IComponent;
  public static class Consumer {
    public static bool Run() {
      using var store = new EntityStore(new ArchStorage());
      var first = store.Create();
      var second = store.Create();
      first.Add(new Wide(ulong.MaxValue));
      second.Add(new Wide(0));
      var token = System.Guid.NewGuid();
      first.Add(new Token(token));
      return !second.TryUpsert(new Wide(ulong.MaxValue))
        && store.ComponentIndex<Wide, ulong>()[ulong.MaxValue][0] == first
        && store.ComponentIndex<Token, System.Guid>()[token][0] == first;
    }
  }
  """);
Require(primitives.Result.Diagnostics.IsEmpty, "Primitive and Guid index metadata generate without diagnostics.");
using var primitiveStream = new MemoryStream();
var primitiveEmit = primitives.Compilation.Emit(primitiveStream);
Require(primitiveEmit.Success, string.Join(Environment.NewLine, primitiveEmit.Diagnostics));
primitiveStream.Position = 0;
var primitiveAssembly = AssemblyLoadContext.Default.LoadFromStream(primitiveStream);
Require((bool)primitiveAssembly.GetType("Consumer")!.GetMethod("Run")!.Invoke(null, null)!,
  "Compiled primitive metadata preserves wide keys, Guid lookup and unique conflicts.");

var nested = Generate("""
  public partial class Container {
    private readonly partial record struct Data(int X, string Name) : IComponent {
      [Indexed(Unique = true)] private (int, string) @event() => (X, Name);
    }
    private readonly partial record struct Named(string Name) : IComponent {
      [Indexed(Unique = true, Comparer = typeof(Folded))] private string Key => Name;
    }
    private enum Hidden { First }
    private readonly partial record struct HiddenData(Hidden Value) : IComponent {
      [Indexed] private Hidden Key => Value;
    }
    public sealed class Folded : System.Collections.Generic.IEqualityComparer<string> {
      public bool Equals(string? a, string? b) => System.StringComparer.OrdinalIgnoreCase.Equals(a, b);
      public int GetHashCode(string a) => System.StringComparer.OrdinalIgnoreCase.GetHashCode(a);
    }
    public static bool Run() {
      using var store = new EntityStore(new ArchStorage());
      var entity = store.Create();
      entity.Add(new Data(3, "a"));
      entity.Upsert(new Data(4, "b"));
      var index = store.ComponentIndex<Data, (int, string)>();
      if (index[(3, "a")].Count != 0 || index[(4, "b")][0] != entity) return false;
      entity.Add(new HiddenData(Hidden.First));
      if (store.ComponentIndex<HiddenData, Hidden>()[Hidden.First][0] != entity) return false;
      entity.Add(new Named("Abc"));
      var second = store.Create();
      return !second.TryAdd(new Named("abc")) && store.ComponentIndex<Named, string>()["ABC"][0] == entity;
    }
  }
  """);
Require(nested.Result.Diagnostics.IsEmpty, "Nested private computed indexes generate.");
using var nestedStream = new MemoryStream();
var nestedEmit = nested.Compilation.Emit(nestedStream);
Require(nestedEmit.Success, string.Join(Environment.NewLine, nestedEmit.Diagnostics));
nestedStream.Position = 0;
var nestedAssembly = AssemblyLoadContext.Default.LoadFromStream(nestedStream);
Require((bool)nestedAssembly.GetType("Container")!.GetMethod("Run")!.Invoke(null, null)!, "Private tuple methods and computed comparer indexes work after upsert.");

string[] invalid = [
  "public struct Data : IComponent { [Indexed] public int Key; }",
  "public partial struct Data : IComponent { [Indexed] public static int Key; }",
  "public partial struct Data : IComponent { [Indexed] public int A; [Indexed] public int B; }",
  "public partial struct Data { [Indexed] public int Key; }",
  "public partial struct Data : IComponent { [Indexed] public int? Key; }",
  "public partial struct Data : IComponent { [Indexed] public int Key { set { } } }",
  "public partial struct Data : IComponent { [Indexed] public int Key(int x) => x; }",
  "public partial struct Data : IComponent { [Indexed] public void Key() {} }",
  "public partial struct Data : IComponent { [Indexed] public object Key => new(); }",
  "public partial struct Data : IComponent { [Indexed(Comparer = typeof(string))] public int Key; }",
  "public class Outer { public partial struct Data : IComponent { [Indexed] public int Key; } }",
  "public partial struct Data : IComponent { [EntityReference] public Entity Target; }",
  "public partial struct Data : IComponent { [Indexed, EntityReference] public int Target; }",
  "public partial struct Data : IComponent { [Indexed] public Entity Target; }",
  "public interface IKey { int Key { get; } } public partial struct Data : IComponent, IKey { [Indexed] int IKey.Key => 1; }",
  "public partial struct Data : IComponent { [Indexed, EntityReference(OnDelete = (TargetDeletion)99)] public Entity Target; }"
];

foreach (var source in invalid) {
  var result = Generate(source);
  Require(result.Result.Diagnostics.Any(diagnostic => diagnostic.Id == "ECS001"), "Invalid index declaration reports ECS001.");
}

var persistent = Generate("""
  [ComponentKey("fixture.value", Version = 2)] public readonly record struct Value(int Number) : IComponent;
  [TagName("fixture.tag")] public readonly struct Mark : ITag;
  public static class Consumer {
    public static bool Run() {
      var serializer = new StoreSerializer(typeof(Value).Assembly);
      return serializer.ReadComponent<Value>(serializer.WriteComponent(new Value(5))).Number == 5;
    }
  }
  """);
using var persistentStream = new MemoryStream();
var persistentEmit = persistent.Compilation.Emit(persistentStream);
Require(persistentEmit.Success && persistent.Result.Diagnostics.IsEmpty, string.Join(Environment.NewLine, persistentEmit.Diagnostics));
persistentStream.Position = 0;
var persistentAssembly = AssemblyLoadContext.Default.LoadFromStream(persistentStream);
Require((bool)persistentAssembly.GetType("Consumer")!.GetMethod("Run")!.Invoke(null, null)!, "Generated persistence catalogs round trip compiled consumers.");
string[] invalidPersistence = [
  "[ComponentKey(\"\")] public struct Data : IComponent;",
  "[ComponentKey(\"x\", Version = 0)] public struct Data : IComponent;",
  "[ComponentKey(\"x\"), Transient] public struct Data : IComponent;",
  "[ComponentKey(\"x\")] public struct Data;",
  "[ComponentKey(\"x\")] public struct A : IComponent; [TagName(\"x\")] public struct B : ITag;",
  "[TagName(\"x\")] public struct Tag : ITag { public int Value; }",
  "[ComponentKey(\"x\")] public struct Data : IComponent, ITag;"
];

foreach (var source in invalidPersistence) Require(Generate(source).Result.Diagnostics.Any(diagnostic => diagnostic.Id == "ECS002"), "Invalid persistence declarations report ECS002.");

System.Reflection.Assembly DuplicateAssembly(string name) {
  var fixture = Generate("[ComponentKey(\"duplicate\")] public struct " + name + " : IComponent;");
  using var output = new MemoryStream();
  Require(fixture.Compilation.Emit(output).Success, "Duplicate-key fixture compiles independently.");
  output.Position = 0;

  return AssemblyLoadContext.Default.LoadFromStream(output);
}

var duplicateA = DuplicateAssembly("DuplicateA");
var duplicateB = DuplicateAssembly("DuplicateB");
var rejected = false;

try { _ = new StoreSerializer(duplicateA, duplicateB); } catch (InvalidOperationException) { rejected = true; }

Require(rejected, "Serializer rejects duplicate keys across selected assembly catalogs.");

var interfaceOwner = Generate("""
  public partial interface ITypes {
    [ComponentKey("fixture.private")] private readonly partial record struct Data([property: Indexed] int Value) : IComponent;
    public static bool Run() {
      var serializer = new StoreSerializer(typeof(Data).Assembly);
      using var store = new EntityStore(new ArchStorage());
      var entity = store.Create();
      entity.Add(serializer.ReadComponent<Data>(serializer.WriteComponent(new Data(4))));
      return store.ComponentIndex<Data, int>()[4][0] == entity;
    }
  }
  """);
Require(interfaceOwner.Result.Diagnostics.IsEmpty, "Private types within partial interfaces have valid catalogs.");
using var interfaceStream = new MemoryStream();
var interfaceEmit = interfaceOwner.Compilation.Emit(interfaceStream);
Require(interfaceEmit.Success, string.Join(Environment.NewLine, interfaceEmit.Diagnostics));
interfaceStream.Position = 0;
var interfaceAssembly = AssemblyLoadContext.Default.LoadFromStream(interfaceStream);
Require((bool)interfaceAssembly.GetType("ITypes")!.GetMethod("Run")!.Invoke(null, null)!, "Private nested interface consumers use generated persistence and index catalogs.");

var missingGenerator = CSharpCompilation.Create("MissingGenerator", [CSharpSyntaxTree.ParseText("""
  using Archy;
  using Archy.Adapters.Arch;
  public readonly partial record struct Link([property: Indexed, EntityReference] Entity Target) : IComponent;
  public static class Consumer {
    public static bool Run() {
      using var store = new EntityStore(new ArchStorage());
      var entity = store.Create();
      try { entity.Add(new Link(entity)); return false; }
      catch (System.InvalidOperationException) { return !entity.Has<Link>(); }
    }
  }
  """, parseOptions)], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
using var missingStream = new MemoryStream();
Require(missingGenerator.Emit(missingStream).Success, "A consumer can compile without the analyzer reference.");
missingStream.Position = 0;
var missingAssembly = AssemblyLoadContext.Default.LoadFromStream(missingStream);
Require((bool)missingAssembly.GetType("Consumer")!.GetMethod("Run")!.Invoke(null, null)!, "Missing generated reference metadata fails before publishing a component.");

Console.WriteLine($"ECS generator checks passed: {count}.");
