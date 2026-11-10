using Content.Shared.Access.Systems;
using Content.Shared.Construction.EntitySystems;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.IdentityManagement;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Content.Shared.Storage.Components;
using Content.Shared.Verbs;
using Content.Shared.Whitelist;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Containers;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Shared.SmartFridge;

public abstract partial class SharedSmartFridgeSystem : EntitySystem
{
    [Dependency] private AccessReaderSystem _accessReader = default!;
    [Dependency] private EntityWhitelistSystem _whitelist = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private SharedContainerSystem _container = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedPopupSystem _popup = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<SmartFridgeComponent, InteractUsingEvent>(OnInteractUsing, after: [typeof(AnchorableSystem)]);
        SubscribeLocalEvent<SmartFridgeComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<SmartFridgeComponent, EntInsertedIntoContainerMessage>(OnItemInserted);
        SubscribeLocalEvent<SmartFridgeComponent, EntRemovedFromContainerMessage>(OnItemRemoved);
        SubscribeLocalEvent<SmartFridgeComponent, AfterAutoHandleStateEvent>((ent, ref _) => UpdateUI(ent));

        SubscribeLocalEvent<SmartFridgeComponent, GetVerbsEvent<AlternativeVerb>>(OnGetAltVerb);
        SubscribeLocalEvent<SmartFridgeComponent, GetDumpableVerbEvent>(OnGetDumpableVerb);
        SubscribeLocalEvent<SmartFridgeComponent, DumpEvent>(OnDump);

        Subs.BuiEvents<SmartFridgeComponent>(SmartFridgeUiKey.Key,
            sub =>
            {
                sub.Event<SmartFridgeDispenseItemMessage>(OnDispenseItem);
                sub.Event<SmartFridgeRemoveEntryMessage>(OnRemoveEntry);
            });
    }

    private bool DoInsert(Entity<SmartFridgeComponent> ent, EntityUid user, IEnumerable<EntityUid> usedItems, bool playSound)
    {
        if (!_container.TryGetContainer(ent, ent.Comp.Container, out var container))
            return false;

        if (!Allowed(ent, user))
            return true;

        bool anyInserted = false;
        foreach (var used in usedItems)
        {
            if (!_whitelist.CheckBoth(used, ent.Comp.Blacklist, ent.Comp.Whitelist))
                continue;
            anyInserted = true;

            _container.Insert(used, container);
        }

        if (anyInserted && playSound)
        {
            _audio.PlayPredicted(ent.Comp.InsertSound, ent, user);
        }

        return anyInserted;
    }

    private void OnInteractUsing(Entity<SmartFridgeComponent> ent, ref InteractUsingEvent args)
    {
        if (args.Handled || !_hands.CanDrop(args.User, args.Used))
            return;

        args.Handled = DoInsert(ent, args.User, [args.Used], true);
    }

    // Which items are in the fridge isn't saved, but the items themselves are (in its container), e.g. when loading a
    // world save. Work the list out again from what's actually inside.
    private void OnStartup(Entity<SmartFridgeComponent> ent, ref ComponentStartup args)
    {
        if (_timing.ApplyingState || !_container.TryGetContainer(ent, ent.Comp.Container, out var container))
            return;

        foreach (var item in container.ContainedEntities)
        {
            var key = new SmartFridgeEntry(Identity.Name(item, EntityManager));
            if (!ent.Comp.Entries.Contains(key))
                ent.Comp.Entries.Add(key);

            AddContained(ent, key, GetNetEntity(item));
        }

        // Entries whose items have all been taken out stay listed (with 0 and an X); give them an empty set so the X
        // can still clear them.
        foreach (var key in ent.Comp.Entries)
        {
            if (!ent.Comp.ContainedEntries.ContainsKey(key))
                ent.Comp.ContainedEntries[key] = new HashSet<NetEntity>();
        }
    }

    // The sets in ContainedEntries are shared with the last networked state (its auto-generated state only copies the
    // dictionary, not the sets in it). Changing one in place during client prediction also changed the state the client
    // rewinds to, so every re-prediction of a dispense took out another item: the count ran down to 0 before the
    // server's real count arrived. Always swap in a new set instead.
    private static void AddContained(Entity<SmartFridgeComponent> ent, SmartFridgeEntry key, NetEntity item)
    {
        var items = ent.Comp.ContainedEntries.TryGetValue(key, out var old) ? new HashSet<NetEntity>(old) : new HashSet<NetEntity>();
        items.Add(item);
        ent.Comp.ContainedEntries[key] = items;
    }

    private static void RemoveContained(Entity<SmartFridgeComponent> ent, SmartFridgeEntry key, NetEntity item)
    {
        if (!ent.Comp.ContainedEntries.TryGetValue(key, out var old) || !old.Contains(item))
            return;

        var items = new HashSet<NetEntity>(old);
        items.Remove(item);
        ent.Comp.ContainedEntries[key] = items;
    }

    private void OnItemInserted(Entity<SmartFridgeComponent> ent, ref EntInsertedIntoContainerMessage args)
    {
        if (args.Container.ID != ent.Comp.Container || _timing.ApplyingState)
            return;

        var key = new SmartFridgeEntry(Identity.Name(args.Entity, EntityManager));
        if (!ent.Comp.Entries.Contains(key))
            ent.Comp.Entries.Add(key);

        AddContained(ent, key, GetNetEntity(args.Entity));

        Dirty(ent);
        UpdateUI(ent);
    }

    private void OnItemRemoved(Entity<SmartFridgeComponent> ent, ref EntRemovedFromContainerMessage args)
    {
        if (args.Container.ID != ent.Comp.Container  || _timing.ApplyingState)
            return;

        var key = new SmartFridgeEntry(Identity.Name(args.Entity, EntityManager));

        RemoveContained(ent, key, GetNetEntity(args.Entity));

        Dirty(ent);
        UpdateUI(ent);
    }

    private bool Allowed(Entity<SmartFridgeComponent> machine, EntityUid user)
    {
        if (_accessReader.IsAllowed(user, machine))
            return true;

        _audio.PlayPredicted(machine.Comp.SoundDeny, machine, user);
        _popup.PopupEntity(Loc.GetString("smart-fridge-component-try-eject-access-denied"), machine, user);
        return false;
    }

    private void OnDispenseItem(Entity<SmartFridgeComponent> ent, ref SmartFridgeDispenseItemMessage args)
    {
        if (!Allowed(ent, args.Actor))
            return;

        if (!ent.Comp.ContainedEntries.TryGetValue(args.Entry, out var contained))
        {
            _audio.PlayPredicted(ent.Comp.SoundDeny, ent, args.Actor);
            _popup.PopupEntity(Loc.GetString("smart-fridge-component-try-eject-unknown-entry"), ent, args.Actor);
            return;
        }

        foreach (var item in contained)
        {
            if (!_container.TryRemoveFromContainer(GetEntity(item)))
                continue;

            _audio.PlayPredicted(ent.Comp.SoundVend, ent, args.Actor);
            RemoveContained(ent, args.Entry, item);
            Dirty(ent);
            UpdateUI(ent);
            return;
        }

        _audio.PlayPredicted(ent.Comp.SoundDeny, ent, args.Actor);
        _popup.PopupEntity(Loc.GetString("smart-fridge-component-try-eject-out-of-stock"), ent, args.Actor);
    }

    private void OnGetAltVerb(Entity<SmartFridgeComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        var user = args.User;

        if (!args.CanInteract
            || args.Using is not { } item
            || !_hands.CanDrop(user, item)
            || !_whitelist.CheckBoth(item, ent.Comp.Blacklist, ent.Comp.Whitelist))
            return;

        args.Verbs.Add(new AlternativeVerb
        {
            Act = () => DoInsert(ent, user, [item], true),
            Text = Loc.GetString("verb-categories-insert"),
            Icon = new SpriteSpecifier.Texture(new ResPath("/Textures/Interface/VerbIcons/insert.svg.192dpi.png")),
        });
    }

    private void OnRemoveEntry(Entity<SmartFridgeComponent> ent, ref SmartFridgeRemoveEntryMessage args)
    {
        if (!Allowed(ent, args.Actor))
            return;

        // No item set at all counts as empty too.
        if (ent.Comp.ContainedEntries.TryGetValue(args.Entry, out var contained) && contained.Count > 0
            || !ent.Comp.Entries.Contains(args.Entry))
            return;

        ent.Comp.Entries.Remove(args.Entry);
        ent.Comp.ContainedEntries.Remove(args.Entry);
        Dirty(ent);
        UpdateUI(ent);
    }

    private void OnGetDumpableVerb(Entity<SmartFridgeComponent> ent, ref GetDumpableVerbEvent args)
    {
        if (_accessReader.IsAllowed(args.User, ent))
        {
            args.Verb = Loc.GetString("dump-smartfridge-verb-name", ("unit", ent));
        }
    }

    private void OnDump(Entity<SmartFridgeComponent> ent, ref DumpEvent args)
    {
        if (args.Handled)
            return;

        args.Handled = true;
        args.PlaySound = true;

        DoInsert(ent, args.User, args.DumpQueue, false);
    }

    protected virtual void UpdateUI(Entity<SmartFridgeComponent> ent)
    {

    }
}
