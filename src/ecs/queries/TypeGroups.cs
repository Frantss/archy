namespace Archy;

public readonly struct ComponentTypes {
  internal TypeDescriptor[] Types { get; }
  private ComponentTypes(TypeDescriptor[] types) => this.Types = types;
  public static ComponentTypes Get<T1>() where T1 : struct, IComponent => new([TypeCatalog.Component<T1>()]);
  public static ComponentTypes Get<T1, T2>() where T1 : struct, IComponent where T2 : struct, IComponent => new([TypeCatalog.Component<T1>(), TypeCatalog.Component<T2>()]);
  public static ComponentTypes Get<T1, T2, T3>() where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent => new([TypeCatalog.Component<T1>(), TypeCatalog.Component<T2>(), TypeCatalog.Component<T3>()]);
  public static ComponentTypes Get<T1, T2, T3, T4>() where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent where T4 : struct, IComponent => new([TypeCatalog.Component<T1>(), TypeCatalog.Component<T2>(), TypeCatalog.Component<T3>(), TypeCatalog.Component<T4>()]);
}

public readonly struct Tags {
  internal TypeDescriptor[] Types { get; }
  private Tags(TypeDescriptor[] types) => this.Types = types;
  public static Tags Get<T1>() where T1 : struct, ITag => new([TypeCatalog.Tag<T1>()]);
  public static Tags Get<T1, T2>() where T1 : struct, ITag where T2 : struct, ITag => new([TypeCatalog.Tag<T1>(), TypeCatalog.Tag<T2>()]);
  public static Tags Get<T1, T2, T3>() where T1 : struct, ITag where T2 : struct, ITag where T3 : struct, ITag => new([TypeCatalog.Tag<T1>(), TypeCatalog.Tag<T2>(), TypeCatalog.Tag<T3>()]);
  public static Tags Get<T1, T2, T3, T4>() where T1 : struct, ITag where T2 : struct, ITag where T3 : struct, ITag where T4 : struct, ITag => new([TypeCatalog.Tag<T1>(), TypeCatalog.Tag<T2>(), TypeCatalog.Tag<T3>(), TypeCatalog.Tag<T4>()]);
}
