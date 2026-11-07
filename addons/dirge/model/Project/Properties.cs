namespace Dirge.Project;

/// <summary>Addressable mixer/instrument property. Automation, modulators and snapshots all target one of these.</summary>
public readonly record struct PropertyRef(PropertyKind Kind, DirgeId Owner, int EffectIndex = -1, string? Name = null)
{
    public static PropertyRef BusVolume(DirgeId bus) => new(PropertyKind.BusVolumeDb, bus);
    public static PropertyRef BusPitch(DirgeId bus) => new(PropertyKind.BusPitch, bus);
    public static PropertyRef Effect(DirgeId bus, int effectIndex, string parameter) => new(PropertyKind.EffectParameter, bus, effectIndex, parameter);
    public static PropertyRef VcaVolume(DirgeId vca) => new(PropertyKind.VcaVolumeDb, vca);
    public static PropertyRef EventVolume(DirgeId ev) => new(PropertyKind.EventVolumeDb, ev);
    public static PropertyRef EventPitch(DirgeId ev) => new(PropertyKind.EventPitch, ev);
    public static PropertyRef InstrumentVolume(DirgeId instrument) => new(PropertyKind.InstrumentVolumeDb, instrument);
    public static PropertyRef InstrumentPitch(DirgeId instrument) => new(PropertyKind.InstrumentPitch, instrument);
}

public enum PropertyKind
{
    BusVolumeDb,
    BusPitch,
    EffectParameter,
    VcaVolumeDb,
    EventVolumeDb,
    EventPitch,
    InstrumentVolumeDb,
    InstrumentPitch,
}
