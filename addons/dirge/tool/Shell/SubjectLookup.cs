#nullable enable
using Dirge.Project;
using Dirge.Tool.Core;

namespace Dirge.Tool.Shell;

/// <summary>Finds what kind of object an id denotes so a diagnostic click can select it.</summary>
public static class SubjectLookup
{
    public readonly record struct Subject(SelectionKind Kind, DirgeId Id, DirgeId Owner, int Index, string Name)
    {
        public bool IsNone => Kind == SelectionKind.None;
        public static readonly Subject None = new(SelectionKind.None, DirgeId.None, DirgeId.None, -1, "");
    }

    public static Subject Resolve(AudioProject project, DirgeId id)
    {
        if (id.IsNone)
        {
            return Subject.None;
        }

        foreach (Bus bus in project.Mixer.Buses)
        {
            if (bus.Id == id)
            {
                return new Subject(SelectionKind.Bus, id, DirgeId.None, -1, bus.Name);
            }

            for (int i = 0; i < bus.Effects.Count; i++)
            {
                if (bus.Effects[i].Id == id)
                {
                    return new Subject(SelectionKind.Effect, id, bus.Id, i, $"{bus.Name}/{bus.Effects[i].TypeName}");
                }
            }
        }

        foreach (Vca vca in project.Mixer.Vcas)
        {
            if (vca.Id == id)
            {
                return new Subject(SelectionKind.Vca, id, DirgeId.None, -1, vca.Name);
            }
        }

        foreach (Snapshot snapshot in project.Snapshots)
        {
            if (snapshot.Id == id)
            {
                return new Subject(SelectionKind.Snapshot, id, DirgeId.None, -1, snapshot.Name);
            }
        }

        foreach (ParameterDefinition parameter in project.GlobalParameters)
        {
            if (parameter.Id == id)
            {
                return new Subject(SelectionKind.Parameter, id, DirgeId.None, -1, parameter.Name);
            }
        }

        foreach (AudioEvent audioEvent in project.Events)
        {
            if (audioEvent.Id == id)
            {
                return new Subject(SelectionKind.Event, id, DirgeId.None, -1, audioEvent.Path);
            }

            foreach (ParameterDefinition parameter in audioEvent.LocalParameters)
            {
                if (parameter.Id == id)
                {
                    return new Subject(SelectionKind.Parameter, id, audioEvent.Id, -1, $"{audioEvent.Path}:{parameter.Name}");
                }
            }

            foreach (Track track in audioEvent.Tracks)
            {
                if (track.Id == id)
                {
                    return new Subject(SelectionKind.Track, id, audioEvent.Id, -1, $"{audioEvent.Path}/{track.Name}");
                }

                foreach (Instrument instrument in track.Instruments)
                {
                    if (instrument.Id == id)
                    {
                        string name = instrument.Name.Length > 0 ? instrument.Name : instrument.GetType().Name;
                        return new Subject(SelectionKind.Instrument, id, audioEvent.Id, -1, $"{audioEvent.Path}/{name}");
                    }
                }
            }

            foreach (LogicMarker marker in audioEvent.Markers)
            {
                if (marker.Id == id)
                {
                    string name = marker.Name.Length > 0 ? marker.Name : marker.GetType().Name;
                    return new Subject(SelectionKind.Marker, id, audioEvent.Id, -1, $"{audioEvent.Path}/{name}");
                }
            }
        }

        return Subject.None;
    }
}
