# Standalone ECS replacement with generated indexes and scoped events

The current API layer and storage adapters are documented in
[storage and integration](../src/ecs/IMPLEMENTATION.md). Adapter requirements are
defined in [adding an adapter](adapters.md).

## Problem Statement

The project needs an ECS library whose component writes consistently maintain
indexes and produce observable events, with explicit mutation semantics and a
Friflo-like query interface. Writable component references can bypass those
guarantees, while handwritten index getters and external index registration add
repetition to component declarations.

The replacement must support entities, components, tags, indexed components,
queries, systems, lifecycle and custom events, command buffers, component/store
serialization, and prefabs. It must live in a nested project and remain usable
independently of any game engine or gameplay implementation.

## Solution

Provide a standalone ECS runtime library and an incremental source generator.
Developers declare component/tag data normally, mark an indexed member with
`[Indexed]`, and use explicit add, update, upsert, and remove methods. The runtime
owns index maintenance, reference validation, and event capture for every write.

Keep query construction and traversal close to Friflo. Allow lifecycle and
entity-associated custom event subscriptions on an individual entity or its store,
with the originating entity included in every entity-associated event. Support
store-only custom messages through a distinct envelope. Use deferred command
playback and event delivery with specified ordering and failure behavior.

Provide immutable prefab templates and logical, versioned component/store
serialization, including entity-reference remapping. Establish the new library in
the existing solution without migrating the running game as part of this feature.

## User Stories

1. As a game developer, I want an ECS library in a nested project, so that I can develop and verify it separately from the game.
2. As a library consumer, I want the ECS runtime to work without Godot or gameplay dependencies, so that I can use it in ordinary .NET applications and tests.
3. As a game developer, I want to create and delete entities, so that I can represent the lifecycle of game objects.
4. As a game developer, I want entity handles to distinguish stores and recycled generations, so that old handles cannot access a different entity.
5. As a game developer, I want to check whether an entity is alive, so that I can handle targets that have disappeared.
6. As a game developer, I want to add a component only when absent, so that accidental replacement is detected.
7. As a game developer, I want to update a component only when present, so that missing state is detected.
8. As a game developer, I want to upsert a component, so that one operation adds it when absent or updates it when present.
9. As a game developer, I want to remove a component explicitly, so that composition changes are observable and consistent.
10. As a game developer, I want try variations of mutation methods, so that ordinary failures can be handled without exceptions or partial changes.
11. As a game developer, I want successful try-upserts to report success for either branch, so that their boolean result is unambiguous.
12. As a game developer, I want component reads to return independent values, so that stored data cannot change without a mutation method.
13. As a game developer, I want component membership and try-read operations, so that I can handle optional components directly.
14. As a game developer, I want presence-only tags with add/remove and try variations, so that I can classify entities without storing a component value.
15. As a game developer, I want to declare an indexed field or member directly on its component, so that index configuration stays next to the data it describes.
16. As a game developer, I want the generator to infer the index key type, so that I do not repeat it in a handwritten index interface or getter.
17. As a game developer, I want indexed fields, readable properties, and parameterless methods, so that both stored and computed keys are supported.
18. As a game developer, I want computed tuple keys, so that I can index a combination of component values.
19. As a game developer, I want to declare uniqueness on an indexed member, so that identity conflicts are rejected before state changes.
20. As a game developer, I want shared-key indexes to return every matching entity, so that spatial cells and other grouping keys work naturally.
21. As a game developer, I want component changes to maintain their indexes automatically, so that lookups remain correct after every supported write path.
22. As a game developer, I want invalid index declarations diagnosed during compilation, so that mistakes are found before running the game.
23. As a game developer, I want relationships represented by ordinary components, so that they use the same queries, mutations, and serialization as other state.
24. As a game developer, I want reverse lookup by an entity reference, so that I can find items, owners, targets, or other linked entities.
25. As a game developer, I want declared target-deletion policies, so that deleting a target either rejects the operation or removes referencing components predictably.
26. As a game developer, I want Friflo-like typed and untyped queries, so that existing query patterns remain familiar.
27. As a game developer, I want All/Any and exclusion filters for components and tags, so that systems select the intended entity compositions.
28. As a game developer, I want indexed equality and inclusive range predicates, so that queries can select component values.
29. As a game developer, I want entity traversal, component-chunk traversal, callbacks, and counts, so that I can use the query form appropriate to a system.
30. As a game developer, I want reusable queries whose filters can be frozen, so that systems can retain prepared query configurations.
31. As a game developer, I want existing-component updates during queries, so that systems can compute and commit new values while iterating.
32. As a game developer, I want indexed query membership fixed for each traversal, so that an index update cannot invalidate the active iterator.
33. As a game developer, I want structural mutations rejected during traversal and available through commands, so that composition changes cannot corrupt iteration.
34. As a game developer, I want ordered systems with a simulation tick context, so that I can control the sequence of gameplay work.
35. As a game developer, I want explicit command and event checkpoints, so that I know when changes become visible to later systems.
36. As a game developer, I want entity-created, entity-modified, and entity-deleted events, so that observers can follow entity lifecycle changes.
37. As a game developer, I want component-added, component-updated, and component-removed events, so that observers can respond to component changes.
38. As a game developer, I want update/removal events to preserve old values, so that observers do not need to reconstruct historical state.
39. As a game developer, I want tag-added and tag-removed events, so that observers can respond to classification changes.
40. As a game developer, I want to attach event handlers to one entity, so that local observers receive only that entity's events.
41. As a game developer, I want to attach event handlers to the store, so that one observer can follow matching events across entities.
42. As a game developer, I want store-level handlers to receive the originating entity, so that they can identify which entity triggered an event.
43. As a game developer, I want subscriptions to be disposable and cleaned up after deletion, so that handlers do not leak or transfer to recycled entity slots.
44. As a game developer, I want failed mutations to emit no events and upserts to emit their actual add/update event, so that notifications reflect successful operations.
45. As a game developer, I want ordered deferred delivery with owned payloads, so that handlers observe stable event data without invalidating active mutations or queries.
46. As a game developer, I want handler failures reported without preventing other handlers from running, so that one observer cannot silently suppress unrelated notifications.
47. As a game developer, I want custom event payloads, so that application messages can use the same dispatch mechanism as lifecycle events.
48. As a game developer, I want entity-associated custom events observable on either the entity or store, so that local and global observers can share an event type.
49. As a game developer, I want custom envelopes to supply origin and sequence, so that application payloads do not duplicate dispatcher metadata.
50. As a game developer, I want store-only custom events, so that messages without an originating entity do not need a fabricated entity handle.
51. As a game developer, I want custom emission and try-emission methods, so that event origin validity is handled explicitly.
52. As a game developer, I want handler-emitted custom events deferred to another batch, so that event chains cannot recurse within the active dispatch.
53. As a game developer, I want command buffers that preserve recording order, so that deferred operations have the same observable meaning as immediate operations.
54. As a game developer, I want reserved entity handles in command buffers, so that relationship components can refer to entities whose creation is deferred.
55. As a game developer, I want buffered try operations to return result tickets, so that I can inspect their actual playback outcomes.
56. As a game developer, I want strict playback failures to identify the failed command and completed prefix, so that I can handle partial playback without accidentally repeating it.
57. As a game developer, I want custom events recorded in command buffers, so that they are emitted at a defined position relative to deferred mutations.
58. As a game developer, I want component serialization and deserialization, so that individual component values can be saved and restored through normal writes.
59. As a game developer, I want store serialization independent of memory layout and runtime type IDs, so that saved data describes logical entity state.
60. As a game developer, I want stable serialization keys, versions, and explicit migrations, so that component renames and data evolution can be handled deliberately.
61. As a game developer, I want entity references remapped during loading, so that relationships and cycles remain valid in a new store.
62. As a game developer, I want failed store loads to leave no partially published store, so that invalid data cannot enter simulation.
63. As a game developer, I want immutable prefab templates with editable drafts and variants, so that entity defaults can be reused without shared mutable state.
64. As a game developer, I want prefab graph instantiation to remap internal references and bind external references explicitly, so that multiple instances have independent relationships.
65. As a game developer, I want prefab initialization validated before events or queries expose it, so that observers never see a partially constructed instance.
66. As a library maintainer, I want behavior checked through public interfaces and compiled consumers, so that storage and generator internals can change without rewriting the tests.
67. As a game developer, I want representative comparisons with the existing Friflo workload, so that storage choices are assessed with measured tick times and allocations.

## Implementation Decisions

- Build the engine-independent Archy API layer and a separate Archy.Adapters.Arch storage adapter. Only the Arch adapter references Arch; the naive adapter uses managed arrays and dictionaries with no third-party dependencies. Keep indexes, events, relationships, commands, queries, systems, serialization and prefabs in the API layer. Applications supply fresh storage explicitly when constructing or restoring stores. Keep the source generator separate and reference it from component assemblies.
- Target the runtime and adapter at .NET 10 and the Roslyn generator at .NET Standard 2.0. Keep game-engine, gameplay, Arch and Roslyn dependencies out of the API layer. The Arch adapter consumes Arch 2.1.0 through NuGet. Application identity types remain ordinary component data.
- Put storage behind IEntityStorage, IStorageArchetype and IStorageChunk. Adapters own dense chunked component arrays, structural transitions, swap removal and vacated-row cleanup. The API layer owns composition masks, cached query plans and public slot locations. Keep native types private to each adapter.
- Use a single writer. All public accesses validate store identity and entity generation. Handles survive structural moves; default, reserved, deleted, and disposed-store handles are not alive. Retire slots instead of wrapping generations. Runtime handles are distinct from persisted application identity components.
- Keep component and tag marker interfaces. Components use value-based reads; tags are presence-only and allocate no value column. Do not expose mutable component references or writable column spans.
- Add and update are strict: add requires absence; update requires presence. Upsert adds when absent and updates when present. Remove requires presence. Provide try variations returning false with no changes on ordinary failures. Try-upsert returns true for either successful branch; ordinary upsert returns no branch indicator. Existing equal-value updates still count as explicit writes.
- Provide get, try-get, membership, liveness, delete, and try-delete operations. There is no tag-update operation or generic entity-update operation. Invalid declarations, wrong-thread operations, and forbidden structural writes throw even through try methods.
- Use one mutation kernel for immediate methods, command playback, prefab construction, and restoration invariants. Prepare validation, index/reference changes, and owned event payloads before commit; invoke no application callbacks during commit. Expected validation failures leave no partial mutation. Process failure and memory exhaustion are outside the atomicity guarantee.
- Require value graphs, approved immutable references, or explicit copy policies for component/event payloads. Copies must isolate stored state at ingress, reads, command recording, event capture, and prefab copying. Read-only memory wrappers do not by themselves guarantee immutable backing storage.
- Declare indexes with the exact annotation name `[Indexed]` on a field, readable property, or parameterless instance method. Infer the key type from that member. Declare uniqueness and comparer/index options on the same annotation. Do not require a handwritten indexed-component interface, getter, or external index registration.
- Require indexed components and their enclosing types to be partial so generated accessors can read private members. Use an incremental Roslyn generator to emit typed accessors and descriptors, plus an automatically registered catalog per component assembly. Keep shared index storage in generic runtime code.
- Initially support one indexed member per component. A computed member can return a tuple for a composite key. Computed keys must be pure and depend only on component value; the generator cannot generally prove purity. Diagnose duplicate annotations, static or unreadable members, unsupported signatures, non-partial owners, and known unsupported key types at compilation.
- Keep the type catalog internal. It records runtime type IDs, component/tag kinds, generated index accessors/options, reference policies, stable serialization keys/versions, and codec/copy policies. Applications construct a store directly. Persistence selects generated catalogs from supplied assemblies rather than maintaining a handwritten schema.
- Expose Friflo-like component-index access through ComponentIndex, key-indexed entity collections, and indexed Values. Add a single-entity try lookup for unique indexes. Maintain indexes directly during mutations rather than through event handlers. Hash lookup has expected constant setup cost plus the number of matches; initial range evaluation may scan distinct keys.
- Model relationships through ordinary components. An entity-valued indexed member supplies reverse lookup; member-level entity-reference metadata supplies target validation and deletion policy. Initially support one entity reference per component. Multiple roles use separate components, including edge entities for relationships with their own data.
- Support restricting target deletion and removing referencing components. Preflight the entire deletion/cleanup operation, use a defined incoming-handle order, and emit ordinary source-component changes. References originating on entities deleted by that operation do not restrict it or generate duplicate removals. Do not infer ownership or cascade deletion of source entities.
- Follow Friflo query construction with typed/untyped ArchetypeQuery, component and tag All/Any/exclusion methods, HasValue, ValueInRange, Entities, Chunks, Count, ForEachEntity, Filter, and FreezeFilter. Filter methods configure the same query object; disallow filter changes during traversal. Cache plans privately and invalidate them when configuration changes.
- Preserve the researched Friflo combination rules: typed signatures remain mandatory; configured positive All and Any groups within one category are alternatives; exclusions veto matches; multiple indexed predicates union candidates before structural filtering. Range endpoints are inclusive. Query callbacks and chunk indexers return component values rather than writable refs.
- Allow existing-component updates during traversal. Reject entity create/delete, component add/remove, and tag add/remove while traversals are active. An upsert inside traversal is allowed only for an existing component; buffer it when the add branch must be supported. Indexed traversals snapshot membership per enumerator while reading values at visitation. Ordinary composition scans need no entity-result snapshot.
- Query order is unspecified. FIFO playback preserves recorded order but does not make order-dependent simulation deterministic when commands originate in unordered queries. Application identity sorting remains the caller's responsibility where needed.
- Provide an optional ordered system pipeline over a small system interface and context containing store, tick, and commands. Inject application dependencies through constructors. The default pipeline plays commands and dispatches events after each system, with explicit checkpoints available after traversal. Handler-recorded commands wait for the next checkpoint; final-system handler commands wait for the next tick's opening checkpoint.
- Provide entity-created, entity-modified, entity-deleted, component-added, component-updated, component-removed, tag-added, and tag-removed events. Update events own old/new values; removals own old values; creation/deletion events can own construction/final snapshots. Capture payloads only for interested subscribers.
- A successful ordinary component/tag write emits its specific event followed by one entity-modified event. Upsert emits the actual add or update event, without a separate upsert event. Failed try operations emit nothing. Construction emits created then initial additions; deletion emits final removals then deleted, without extra entity-modified events for the created/deleted entity. Incoming-reference cleanup emits normal surviving-source changes first.
- Support subscriptions through both entity events and store events. Every entity-associated payload includes the full originating Entity and sequence; both scopes use the same event type. Capture each event once, route store handlers first and entity handlers second, and preserve registration order within a scope. Registering in both scopes produces two callbacks.
- Return disposable subscription handles. Unsubscription suppresses pending callbacks. Key entity subscriptions by full store/slot/generation identity; keep them through prior queued changes and deletion delivery, then clean them up even if nobody subscribes to the deleted event. Store disposal removes all handlers. Reserved handles may be subscribed before creation; later subscriptions do not replay earlier events.
- Deliver events in ordered finite batches at explicit dispatch points. Historical payloads remain valid even when their entity is already changed or deleted. Handlers may read and record commands but cannot perform direct mutations or recursive playback. Continue delivery after handler errors, then report the errors together without rolling back committed state.
- Define custom payload structs through ICustomEvent. Entity emission and equivalent store emission with an explicit entity produce a CustomEvent envelope containing Entity, Sequence, and Data. Subscribe to that envelope at entity or store scope. Emit requires a live same-store origin; try-emission returns false for an ordinary invalid origin and true for an accepted event even without subscribers.
- Store emission without an entity produces a distinct StoreEvent envelope containing Store, Sequence, and Data, observed only by store subscribers. Entity-associated and store-only envelopes remain separate channels even for the same application payload type. Lifecycle events and dispatcher envelopes are not custom payloads and cannot be forged through custom emission.
- Custom events share routing, ownership, unsubscription, cleanup, and sequence rules with lifecycle events. They do not modify component state, maintain indexes, or produce entity-modified events. They may be queued during query traversal. Handler-emitted custom events wait for the next dispatch batch rather than recursing. Participant handles inside payloads are historical data, not component-reference policies.
- Implement command buffers as FIFO instruction streams with typed payload storage, preserving all observable operations rather than coalescing them. Commands carry full entity handles. Creation reserves a real handle without a live row; reservations belong to one buffer, and cancellation advances generations. Later commands may reference earlier completed creations. Cyclic construction uses a graph operation.
- Evaluate strict/try operations and upsert branches at playback. Buffered try methods return result tickets. Each command is atomic; the whole buffer is not transactional. Strict failure stops playback and reports the failing command plus completed results, retains the committed prefix, marks unexecuted tickets canceled, releases unexecuted reservations, and consumes the stream to prevent accidental replay. Ordinary try failure continues playback.
- Support buffered custom emission with payload copying at recording and origin validation/sequence assignment at the command's playback position. Emission after buffered creation is valid; emission after deletion fails; skipped emissions produce no events.
- Serialize components and stores as logical versioned JSON using stable component/tag keys, per-component versions, and document-local entity IDs. Component serialization shares the same codecs/envelopes. Standalone reference-containing component serialization requires an explicit reference context. Do not serialize runtime slot/generation/type IDs, chunk layout, indexes, queries, pending commands, events, or system instances.
- Restore stores by allocating all handles before decoding/remapping references, including cycles. Validate keys, versions, uniqueness, and references before publishing the new store. Rebuild indexes from values. Reject unknown keys/versions and missing targets unless explicitly handled by migrations. Failed loads dispose the unpublished store. Save at a stable writer checkpoint; asynchronous output must own a snapshot.
- Implement immutable single-entity and graph prefabs outside normal store queries. Drafts support the mutation vocabulary including upsert and try methods; variants are explicit independent drafts. Resolve overrides and fresh unique application identities before construction. Allocate graph handles before remapping internal references, require explicit external bindings, validate the whole graph before publication, and keep instances/template payloads independent. Reuse persistence codecs and migrations.

## Testing Decisions

- Confirmed primary seam: test runtime behavior through the public store/entity/query/index/event/command/system/serializer/prefab interface. Assert returned values, membership, event payloads/order, and externally visible failure effects. Do not assert private dictionaries, chunk layouts, cached transitions, allocator details, or generated helper names.
- Test generated behavior by compiling an annotated consumer and exercising its components through that same public ECS interface. This keeps index maintenance and generated key selection observable at one runtime seam.
- Use a separate compiler-diagnostic seam only for invalid declarations and generated compilation success. Follow the existing observable-generator checks, which run Roslyn, compile emitted output, load the resulting assembly, and verify behavior. Generated-text snapshots alone are insufficient.
- Follow the repository's focused executable-check style for the standalone runtime and generator checks. Existing prefab checks provide prior art for template independence, cross-store spawning, indexed values, and relationship lookup. Existing spatial checks provide prior art for comparing indexed results with full scans.
- Cover stale/default/foreign/disposed handles, generation reuse, strict/try parity, add/update/upsert/remove/tag behavior, both upsert branches, equal-value updates, and ordinary validation failures leaving state and indexes unchanged.
- Cover indexed fields, positional and ordinary properties, computed members, methods, private members, composite keys, uniqueness, and declaration diagnostics. Exercise mutations through immediate, buffered, instantiated, and restored entities to catch inconsistent write paths.
- Cover reference validation, restrict/remove-component target deletion, self-references, source cleanup ordering, and relationships remapped through graph construction and load. Verify that failed cleanup leaves every affected entity unchanged.
- Cover Friflo-like filter construction, filter freezing, combination semantics, inclusive ranges, callbacks/chunks/entities, nested indexed traversals, membership snapshots during key updates, and rejection of structural changes during traversal. Avoid asserting incidental traversal order.
- Cover each lifecycle event, old/new snapshot ownership, creation/deletion ordering, actual upsert branch events, zero events on failed tries, entity/store routing, registration order, unsubscription before delivery, deletion cleanup, and recycled-slot isolation.
- Cover entity-associated and store-only custom channels, correct origin metadata, immutable/copied payload isolation, invalid origins, try-emission without subscribers, no automatic entity modification, query-time emission, and finite next-batch handler emission. Verify delivery continues and aggregates handler failures.
- Cover command FIFO behavior, reserved handles, reservation cancellation, buffered try tickets, branch evaluation at playback, failures preserving only the completed prefix, canceled unexecuted tickets, stream consumption, and custom emission relative to create/delete/failure.
- Cover explicit system order, tick/context delivery, command/event checkpoints, and handler-command visibility through the public pipeline rather than private scheduling details.
- Cover component/store round trips, persistence versions/migrations, duplicate keys, unique-key conflicts, reference cycles, invalid-load isolation, index reconstruction, prefab variants, graph remapping, external bindings, and template/instance independence.
- Run the solution build and all README runtime, API-only and generator checks in Debug and Release. The API-only suite shares behavior checks with the Arch suite and uses the supported naive adapter without Arch dependencies. Run export-configuration builds for runtime/adapter wiring changes. No rendered or engine startup checks apply.

## Out of Scope

- Migrating existing gameplay components, systems, prefabs, networking, or presentation from Friflo; removing the current dependency; changing current game behavior.
- A drop-in binary-compatible or completely source-compatible Friflo/Arch implementation.
- Tag values or tag-modified events.
- Writable component references, writable query spans, or setters that bypass mutation methods.
- Multiple independent indexes or multiple entity-reference roles inside one component in the initial design.
- Automatic entity-deletion cascades, a separate relationship-object subsystem, or inferred ownership semantics.
- Parallel writers, automatic system conflict scheduling, and writable-ref query generators.
- Whole-buffer rollback, recovery from process failure/memory exhaustion, or deterministic results from unspecified query order.
- Live prefab inheritance/propagation, binary persistence, merge-into-live-store loading, serialized custom-event history, or general event sourcing.
- Guaranteed performance superiority, a fixed chunk-size promise, and speculative ordered-index optimization before measurement.

## Further Notes

The runtime API layer and source generator are implemented. The Arch adapter is
in `src/adapters/arch/Archy.Adapters.Arch.csproj` and depends on the API layer in
`src/ecs/Archy.csproj`. The minimal managed adapter is in
`src/adapters/naive/Archy.Adapters.Naive.csproj`. Additional adapters implement the
storage contracts without
changing the public entity, query, mutation, event or persistence behavior.
