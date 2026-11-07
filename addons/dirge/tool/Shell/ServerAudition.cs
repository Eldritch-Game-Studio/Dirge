#nullable enable
using System.Collections.Generic;
using Dirge.Driver;
using Dirge.Project;
using Dirge.Runtime;
using Dirge.Tool.Core;
using Godot;

namespace Dirge.Tool.Shell;

/// <summary>
/// Audition over the running game's <see cref="DirgeServer"/> for the in-game overlay. The server keeps driving its system;
/// structural edits in the overlay's <see cref="ProjectDocument"/> replace the server's project (<see cref="DirgeServer.Load"/>),
/// value-only mixer edits are pushed live. Preview emitter/listener are the game's own; the overlay leaves them alone.
/// </summary>
public sealed class ServerAudition : AuditionBase
{
    private readonly DirgeServer _server;
    private readonly Dictionary<DirgeId, int> _busIndexById = new();
    private bool _busIndexValid;

    public ServerAudition(DirgeServer server, ProjectDocument document) : base(document)
    {
        _server = server;
        if (server.IsLoaded)
        {
            Adopt(server.System);
        }
    }

    protected override AudioDriver? Driver => _server.IsLoaded ? _server.Driver : null;

    protected override DirgeSystem CreateSystem()
    {
        _server.Load(Document.Project);
        _busIndexValid = false;
        return _server.System;
    }

    /// <summary>The server updates and applies in its own <c>_Process</c>; nothing to do here.</summary>
    protected override void Step(double dt)
    {
        if (!_server.IsLoaded || !ReferenceEquals(_server.System, System))
        {
            // The game reloaded behind us (e.g. LoadFile); follow it.
            System = _server.IsLoaded ? _server.System : null;
            _busIndexValid = false;
        }
    }

    protected override void OnListenerChanged(Attributes3D listener) { }

    public override (float Left, float Right) BusPeakDb(DirgeId bus)
    {
        if (System is null || !_server.IsLoaded)
        {
            return (-80f, -80f);
        }

        if (!_busIndexValid)
        {
            _busIndexById.Clear();
            foreach (Bus authored in System.Project.Mixer.Buses)
            {
                int index = _server.Driver.BusIndex(authored.Name);
                if (index >= 0)
                {
                    _busIndexById[authored.Id] = index;
                }
            }

            _busIndexValid = true;
        }

        if (!_busIndexById.TryGetValue(bus, out int busIndex) || busIndex >= AudioServer.GetBusCount())
        {
            return (-80f, -80f);
        }

        return (Mathf.Max(AudioServer.GetBusPeakVolumeLeftDb(busIndex, 0), -80f), Mathf.Max(AudioServer.GetBusPeakVolumeRightDb(busIndex, 0), -80f));
    }
}
