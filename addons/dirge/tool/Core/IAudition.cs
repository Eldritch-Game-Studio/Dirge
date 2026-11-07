#nullable enable
using System;
using System.Collections.Generic;
using Dirge.Project;
using Dirge.Runtime;

namespace Dirge.Tool.Core;

/// <summary>
/// Live playback of the edited project inside the editor (or the game when connected via live update).
/// The shell rebuilds the underlying <see cref="DirgeSystem"/> after structural changes; value-only changes are pushed live.
/// </summary>
public interface IAudition
{
    /// <summary>Null while rebuilding or when disconnected.</summary>
    DirgeSystem? System { get; }
    /// <summary>Raised after a rebuild; panes holding <see cref="EventInstance"/>s must drop them.</summary>
    event Action? Rebuilt;
    /// <summary>Stops everything, drops the current system and builds a fresh one immediately (not debounced); <see cref="Rebuilt"/> is raised synchronously before that system's first Update.</summary>
    void Restart();

    EventInstance? Play(DirgeId eventId);
    void StopAll(StopMode mode);
    /// <summary>Instances started from the tool (transport shows these).</summary>
    IReadOnlyList<EventInstance> Instances { get; }

    /// <summary>Peak level in dB for a model bus (left/right); -80 when silent/unknown.</summary>
    (float Left, float Right) BusPeakDb(DirgeId bus);
    /// <summary>Model Update time of the last frame in milliseconds.</summary>
    double LastUpdateMs { get; }
    /// <summary>Preview emitter attributes used for 3D events started from the tool.</summary>
    Attributes3D PreviewEmitter { get; set; }
    Attributes3D PreviewListener { get; set; }
}
