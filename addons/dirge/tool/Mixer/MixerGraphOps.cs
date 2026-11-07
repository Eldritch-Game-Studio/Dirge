#nullable enable
using System;
using System.Collections.Generic;
using Dirge.Project;

namespace Dirge.Tool.Mixer;

/// <summary>
/// Engine-free structural edits on <see cref="MixerGraph"/> and the snapshot values that address it. Every operation is a
/// pair of forward/inverse functions (or a memento) so the pane can wrap it in <c>ProjectDocument.Mutate</c>.
/// This file has no Godot or tool-core dependency and is compiled into the model test project.
/// </summary>
public static class MixerGraphOps
{
    public static Bus? Master(MixerGraph mixer)
    {
        for (int i = 0; i < mixer.Buses.Count; i++)
        {
            if (mixer.Buses[i].Parent.IsNone) return mixer.Buses[i];
        }
        return null;
    }

    public static Bus? FindBus(MixerGraph mixer, DirgeId id)
    {
        for (int i = 0; i < mixer.Buses.Count; i++)
        {
            if (mixer.Buses[i].Id == id) return mixer.Buses[i];
        }
        return null;
    }

    public static int IndexOfBus(MixerGraph mixer, DirgeId id)
    {
        for (int i = 0; i < mixer.Buses.Count; i++)
        {
            if (mixer.Buses[i].Id == id) return i;
        }
        return -1;
    }

    public static Vca? FindVca(MixerGraph mixer, DirgeId id)
    {
        for (int i = 0; i < mixer.Vcas.Count; i++)
        {
            if (mixer.Vcas[i].Id == id) return mixer.Vcas[i];
        }
        return null;
    }

    /// <summary>True when <paramref name="candidate"/> is <paramref name="ancestor"/> or routed (transitively) into it.</summary>
    public static bool IsSelfOrDescendant(MixerGraph mixer, DirgeId candidate, DirgeId ancestor)
    {
        int guard = mixer.Buses.Count + 1;
        DirgeId current = candidate;
        while (!current.IsNone && guard-- > 0)
        {
            if (current == ancestor) return true;
            Bus? bus = FindBus(mixer, current);
            if (bus is null) return false;
            current = bus.Parent;
        }
        return false;
    }

    /// <summary>Buses that <paramref name="bus"/> may route to: everything except itself and its descendants.</summary>
    public static void RoutingTargets(MixerGraph mixer, DirgeId bus, List<Bus> result)
    {
        result.Clear();
        for (int i = 0; i < mixer.Buses.Count; i++)
        {
            Bus candidate = mixer.Buses[i];
            if (!IsSelfOrDescendant(mixer, candidate.Id, bus)) result.Add(candidate);
        }
    }

    /// <summary>A unique bus name derived from <paramref name="baseName"/> ("Bus", "Bus 2", "Bus 3", …).</summary>
    public static string UniqueBusName(MixerGraph mixer, string baseName)
    {
        string name = baseName;
        int n = 2;
        while (HasBusNamed(mixer, name)) name = baseName + " " + n++;
        return name;
    }

    public static string UniqueVcaName(MixerGraph mixer, string baseName)
    {
        string name = baseName;
        int n = 2;
        bool Taken(string candidate)
        {
            for (int i = 0; i < mixer.Vcas.Count; i++) if (mixer.Vcas[i].Name == candidate) return true;
            return false;
        }
        while (Taken(name)) name = baseName + " " + n++;
        return name;
    }

    public static string UniqueSnapshotName(List<Snapshot> snapshots, string baseName)
    {
        string name = baseName;
        int n = 2;
        bool Taken(string candidate)
        {
            for (int i = 0; i < snapshots.Count; i++) if (snapshots[i].Name == candidate) return true;
            return false;
        }
        while (Taken(name)) name = baseName + " " + n++;
        return name;
    }

    private static bool HasBusNamed(MixerGraph mixer, string name)
    {
        for (int i = 0; i < mixer.Buses.Count; i++) if (mixer.Buses[i].Name == name) return true;
        return false;
    }

    /// <summary>Move the bus at <paramref name="from"/> so it ends at index <paramref name="to"/> (both indices into <see cref="MixerGraph.Buses"/>).</summary>
    public static void MoveBus(MixerGraph mixer, int from, int to)
    {
        if (from == to || from < 0 || to < 0 || from >= mixer.Buses.Count || to >= mixer.Buses.Count) return;
        Bus bus = mixer.Buses[from];
        mixer.Buses.RemoveAt(from);
        mixer.Buses.Insert(to, bus);
    }

    // ---- bus deletion -----------------------------------------------------------------------------------------------

    /// <summary>Everything a bus deletion changes, so it can be reverted exactly.</summary>
    public sealed class BusDeletion
    {
        public Bus Bus { get; }
        public int Index { get; }
        public DirgeId NewParent { get; }
        public List<Bus> ReparentedChildren { get; } = new();
        public List<AudioEvent> ReroutedEvents { get; } = new();
        public List<(Snapshot Snapshot, int Index, SnapshotValue Value)> RemovedValues { get; } = new();

        public BusDeletion(Bus bus, int index, DirgeId newParent) { Bus = bus; Index = index; NewParent = newParent; }
    }

    /// <summary>Plan the deletion of <paramref name="busId"/>: children and events re-route to its parent; snapshot values that
    /// target it are dropped. Returns null for the master bus or an unknown id.</summary>
    public static BusDeletion? PlanDeleteBus(AudioProject project, DirgeId busId)
    {
        MixerGraph mixer = project.Mixer;
        int index = IndexOfBus(mixer, busId);
        if (index < 0) return null;
        Bus bus = mixer.Buses[index];
        if (bus.Parent.IsNone) return null;
        var plan = new BusDeletion(bus, index, bus.Parent);
        for (int i = 0; i < mixer.Buses.Count; i++)
        {
            if (mixer.Buses[i].Parent == busId) plan.ReparentedChildren.Add(mixer.Buses[i]);
        }
        for (int i = 0; i < project.Events.Count; i++)
        {
            if (project.Events[i].Bus == busId) plan.ReroutedEvents.Add(project.Events[i]);
        }
        for (int s = 0; s < project.Snapshots.Count; s++)
        {
            Snapshot snapshot = project.Snapshots[s];
            for (int v = 0; v < snapshot.Values.Count; v++)
            {
                if (snapshot.Values[v].Target.Owner == busId) plan.RemovedValues.Add((snapshot, v, snapshot.Values[v]));
            }
        }
        return plan;
    }

    public static void ApplyDeleteBus(AudioProject project, BusDeletion plan)
    {
        for (int i = 0; i < plan.ReparentedChildren.Count; i++) plan.ReparentedChildren[i].Parent = plan.NewParent;
        for (int i = 0; i < plan.ReroutedEvents.Count; i++) plan.ReroutedEvents[i].Bus = plan.NewParent;
        // Remove from the back so earlier recorded indices stay valid.
        for (int i = plan.RemovedValues.Count - 1; i >= 0; i--)
        {
            (Snapshot snapshot, int index, SnapshotValue value) = plan.RemovedValues[i];
            if (index < snapshot.Values.Count && ReferenceEquals(snapshot.Values[index], value)) snapshot.Values.RemoveAt(index);
            else snapshot.Values.Remove(value);
        }
        project.Mixer.Buses.Remove(plan.Bus);
    }

    public static void RevertDeleteBus(AudioProject project, BusDeletion plan)
    {
        List<Bus> buses = project.Mixer.Buses;
        if (!buses.Contains(plan.Bus)) buses.Insert(Math.Min(plan.Index, buses.Count), plan.Bus);
        for (int i = 0; i < plan.ReparentedChildren.Count; i++) plan.ReparentedChildren[i].Parent = plan.Bus.Id;
        for (int i = 0; i < plan.ReroutedEvents.Count; i++) plan.ReroutedEvents[i].Bus = plan.Bus.Id;
        for (int i = 0; i < plan.RemovedValues.Count; i++)
        {
            (Snapshot snapshot, int index, SnapshotValue value) = plan.RemovedValues[i];
            if (!snapshot.Values.Contains(value)) snapshot.Values.Insert(Math.Min(index, snapshot.Values.Count), value);
        }
    }

    // ---- effects -----------------------------------------------------------------------------------------------------

    /// <summary>Move an effect within a bus chain and remap every snapshot value addressing that chain. Inverse: call with the
    /// indices swapped.</summary>
    public static void MoveEffect(AudioProject project, DirgeId busId, int from, int to)
    {
        Bus? bus = FindBus(project.Mixer, busId);
        if (bus is null || from == to || from < 0 || to < 0 || from >= bus.Effects.Count || to >= bus.Effects.Count) return;
        BusEffect effect = bus.Effects[from];
        bus.Effects.RemoveAt(from);
        bus.Effects.Insert(to, effect);
        for (int s = 0; s < project.Snapshots.Count; s++)
        {
            List<SnapshotValue> values = project.Snapshots[s].Values;
            for (int v = 0; v < values.Count; v++)
            {
                PropertyRef target = values[v].Target;
                if (target.Kind != PropertyKind.EffectParameter || target.Owner != busId) continue;
                int index = target.EffectIndex;
                int remapped;
                if (index == from) remapped = to;
                else if (from < to && index > from && index <= to) remapped = index - 1;
                else if (to < from && index >= to && index < from) remapped = index + 1;
                else continue;
                values[v].Target = target with { EffectIndex = remapped };
            }
        }
    }

    /// <summary>Memento of an effect removal: the effect, its slot and the snapshot values that addressed it.</summary>
    public sealed class EffectRemoval
    {
        public DirgeId Bus { get; }
        public BusEffect Effect { get; }
        public int Index { get; }
        public List<(Snapshot Snapshot, int Index, SnapshotValue Value)> RemovedValues { get; } = new();
        public EffectRemoval(DirgeId bus, BusEffect effect, int index) { Bus = bus; Effect = effect; Index = index; }
    }

    public static EffectRemoval? PlanRemoveEffect(AudioProject project, DirgeId busId, int index)
    {
        Bus? bus = FindBus(project.Mixer, busId);
        if (bus is null || index < 0 || index >= bus.Effects.Count) return null;
        var plan = new EffectRemoval(busId, bus.Effects[index], index);
        for (int s = 0; s < project.Snapshots.Count; s++)
        {
            Snapshot snapshot = project.Snapshots[s];
            for (int v = 0; v < snapshot.Values.Count; v++)
            {
                PropertyRef target = snapshot.Values[v].Target;
                if (target.Kind == PropertyKind.EffectParameter && target.Owner == busId && target.EffectIndex == index)
                    plan.RemovedValues.Add((snapshot, v, snapshot.Values[v]));
            }
        }
        return plan;
    }

    public static void ApplyRemoveEffect(AudioProject project, EffectRemoval plan)
    {
        Bus? bus = FindBus(project.Mixer, plan.Bus);
        if (bus is null) return;
        for (int i = plan.RemovedValues.Count - 1; i >= 0; i--)
        {
            (Snapshot snapshot, int _, SnapshotValue value) = plan.RemovedValues[i];
            snapshot.Values.Remove(value);
        }
        bus.Effects.Remove(plan.Effect);
        ShiftEffectValues(project, plan.Bus, plan.Index, -1);
    }

    public static void RevertRemoveEffect(AudioProject project, EffectRemoval plan)
    {
        Bus? bus = FindBus(project.Mixer, plan.Bus);
        if (bus is null) return;
        ShiftEffectValues(project, plan.Bus, plan.Index, +1);
        if (!bus.Effects.Contains(plan.Effect)) bus.Effects.Insert(Math.Min(plan.Index, bus.Effects.Count), plan.Effect);
        for (int i = 0; i < plan.RemovedValues.Count; i++)
        {
            (Snapshot snapshot, int index, SnapshotValue value) = plan.RemovedValues[i];
            if (!snapshot.Values.Contains(value)) snapshot.Values.Insert(Math.Min(index, snapshot.Values.Count), value);
        }
    }

    /// <summary>Insert an effect at <paramref name="index"/>; snapshot values at or after that slot shift up.</summary>
    public static void InsertEffect(AudioProject project, DirgeId busId, BusEffect effect, int index)
    {
        Bus? bus = FindBus(project.Mixer, busId);
        if (bus is null) return;
        index = Math.Clamp(index, 0, bus.Effects.Count);
        ShiftEffectValues(project, busId, index, +1);
        bus.Effects.Insert(index, effect);
    }

    /// <summary>Inverse of <see cref="InsertEffect"/> for an effect that has no snapshot values of its own.</summary>
    public static void UninsertEffect(AudioProject project, DirgeId busId, BusEffect effect)
    {
        Bus? bus = FindBus(project.Mixer, busId);
        if (bus is null) return;
        int index = bus.Effects.IndexOf(effect);
        if (index < 0) return;
        bus.Effects.RemoveAt(index);
        ShiftEffectValues(project, busId, index, -1);
    }

    private static void ShiftEffectValues(AudioProject project, DirgeId busId, int fromIndex, int delta)
    {
        for (int s = 0; s < project.Snapshots.Count; s++)
        {
            List<SnapshotValue> values = project.Snapshots[s].Values;
            for (int v = 0; v < values.Count; v++)
            {
                PropertyRef target = values[v].Target;
                if (target.Kind == PropertyKind.EffectParameter && target.Owner == busId && target.EffectIndex >= fromIndex)
                    values[v].Target = target with { EffectIndex = target.EffectIndex + delta };
            }
        }
    }

    // ---- snapshots -----------------------------------------------------------------------------------------------------

    public static SnapshotValue? FindValue(Snapshot snapshot, PropertyRef target)
    {
        for (int i = 0; i < snapshot.Values.Count; i++)
        {
            if (snapshot.Values[i].Target == target) return snapshot.Values[i];
        }
        return null;
    }

    public static bool IsScoped(Snapshot snapshot, PropertyRef target) => FindValue(snapshot, target) is not null;

    /// <summary>Set (scoping if needed) a snapshot value. Returns the previous value, or null when the property was unscoped.</summary>
    public static float? SetValue(Snapshot snapshot, PropertyRef target, float value)
    {
        SnapshotValue? existing = FindValue(snapshot, target);
        if (existing is not null)
        {
            float previous = existing.Value;
            existing.Value = value;
            return previous;
        }
        snapshot.Values.Add(new SnapshotValue { Target = target, Value = value });
        return null;
    }

    /// <summary>Restore a property to <paramref name="previous"/>, or unscope it when <paramref name="previous"/> is null.</summary>
    public static void RestoreValue(Snapshot snapshot, PropertyRef target, float? previous)
    {
        if (previous is float value) SetValue(snapshot, target, value);
        else Unscope(snapshot, target);
    }

    public static SnapshotValue? Unscope(Snapshot snapshot, PropertyRef target)
    {
        for (int i = 0; i < snapshot.Values.Count; i++)
        {
            if (snapshot.Values[i].Target == target)
            {
                SnapshotValue removed = snapshot.Values[i];
                snapshot.Values.RemoveAt(i);
                return removed;
            }
        }
        return null;
    }
}
