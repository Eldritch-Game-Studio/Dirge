#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Dirge.Project;
using Dirge.Serialization;

namespace Dirge.Tool.Browser;

/// <summary>
/// Deep-copies an <see cref="AudioEvent"/> through a <see cref="ProjectJson"/> round-trip of its project and gives every
/// object owned by the copy a fresh <see cref="DirgeId"/>. References between those objects (parameter sheets, conditions,
/// automation targets, transition destinations, modulator targets) follow the new ids; references to objects outside the
/// event (buses, global parameters, other events, snapshots) are left untouched. Pure C#; editor-time only.
/// </summary>
public static class EventCloner
{
    private static readonly Assembly ModelAssembly = typeof(AudioProject).Assembly;

    public static AudioEvent Clone(AudioProject project, AudioEvent source, string newPath)
    {
        string json = ProjectJson.Serialize(project, indented: false);
        AudioProject copy = ProjectJson.Deserialize(json);
        AudioEvent? clone = null;
        for (int i = 0; i < copy.Events.Count; i++)
        {
            if (copy.Events[i].Id == source.Id) { clone = copy.Events[i]; break; }
        }
        if (clone is null) throw new ArgumentException("Event is not part of the project.", nameof(source));
        ReassignIds(clone);
        clone.Path = newPath;
        return clone;
    }

    /// <summary>Gives every id-bearing object inside <paramref name="ev"/> a new id and rewrites internal references. Returns the old→new map.</summary>
    public static IReadOnlyDictionary<DirgeId, DirgeId> ReassignIds(AudioEvent ev)
    {
        var map = new Dictionary<DirgeId, DirgeId>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        Collect(ev, map, visited);
        visited.Clear();
        Remap(ev, map, visited);
        return map;
    }

    private static void Collect(object node, Dictionary<DirgeId, DirgeId> map, HashSet<object> visited)
    {
        if (!visited.Add(node)) return;
        if (node is IList list)
        {
            foreach (object? item in list) if (item is not null && IsModelObject(item)) Collect(item, map, visited);
            return;
        }
        foreach (PropertyInfo property in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0) continue;
            if (property.Name == "Id" && property.PropertyType == typeof(DirgeId))
            {
                var id = (DirgeId)property.GetValue(node)!;
                if (!id.IsNone && !map.ContainsKey(id)) map[id] = DirgeId.New();
                continue;
            }
            object? value = property.GetValue(node);
            if (value is null) continue;
            if (value is IList nested) Collect(nested, map, visited);
            else if (IsModelObject(value)) Collect(value, map, visited);
        }
    }

    private static void Remap(object node, Dictionary<DirgeId, DirgeId> map, HashSet<object> visited)
    {
        if (!visited.Add(node)) return;
        if (node is IList list)
        {
            if (list is List<DirgeId> ids)
            {
                for (int i = 0; i < ids.Count; i++) if (map.TryGetValue(ids[i], out DirgeId mapped)) ids[i] = mapped;
                return;
            }
            foreach (object? item in list) if (item is not null && IsModelObject(item)) Remap(item, map, visited);
            return;
        }
        foreach (PropertyInfo property in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0) continue;
            Type type = property.PropertyType;
            if (type == typeof(DirgeId))
            {
                if (!property.CanWrite) continue;
                var id = (DirgeId)property.GetValue(node)!;
                if (map.TryGetValue(id, out DirgeId mapped)) property.SetValue(node, mapped);
                continue;
            }
            if (type == typeof(PropertyRef))
            {
                if (!property.CanWrite) continue;
                var target = (PropertyRef)property.GetValue(node)!;
                if (map.TryGetValue(target.Owner, out DirgeId owner)) property.SetValue(node, target with { Owner = owner });
                continue;
            }
            object? value = property.GetValue(node);
            if (value is null) continue;
            if (value is IList nested) Remap(nested, map, visited);
            else if (IsModelObject(value)) Remap(value, map, visited);
        }
    }

    private static bool IsModelObject(object value)
    {
        Type type = value.GetType();
        return type.IsClass && type.Assembly == ModelAssembly;
    }
}
