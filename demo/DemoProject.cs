using Dirge.Project;

namespace DirgeDemo;

/// <summary>
/// The demo project as code. <c>demo.dirge.json</c> is generated from this once (see <see cref="Demo"/>) so the checked-in
/// file is always serializer output; edit here, delete the JSON, run the demo to regenerate.
/// </summary>
public static class DemoProject
{
    public const string WindAsset = "res://demo/audio/wind.wav";
    public const string ClickAsset = "res://demo/audio/click.wav";
    public const string AmbienceBus = "Ambience";
    public const string WindEvent = "Ambience/Wind";
    public const string ClickEvent = "UI/Click";
    public const string MuffledSnapshot = "Muffled";
    public const string WindParameter = "Wind";
    public const float MuffledAmbienceDb = -18f;

    public static AudioProject Create()
    {
        var project = new AudioProject { Name = "Dirge Demo" };

        var master = new Bus { Id = Id("11111111111111111111111111111111"), Name = "Master" };
        var ambience = new Bus { Id = Id("22222222222222222222222222222222"), Name = AmbienceBus, Parent = master.Id, VolumeDb = -3f };
        var lowPass = new BusEffect { Id = Id("33333333333333333333333333333333"), TypeName = "AudioEffectLowPassFilter" };
        lowPass.Parameters["cutoff_hz"] = 20000f;
        lowPass.Parameters["resonance"] = 0.5f;
        ambience.Effects.Add(lowPass);
        project.Mixer.Buses.Add(master);
        project.Mixer.Buses.Add(ambience);

        project.GlobalParameters.Add(new ParameterDefinition
        {
            Id = Id("44444444444444444444444444444444"),
            Name = WindParameter,
            Minimum = 0f,
            Maximum = 1f,
            Default = 0.5f,
            SeekSpeedUp = 0.5f,
            SeekSpeedDown = 0.5f,
        });

        var muffled = new Snapshot { Id = Id("55555555555555555555555555555555"), Name = MuffledSnapshot, Priority = 10 };
        muffled.Values.Add(new SnapshotValue { Target = PropertyRef.BusVolume(ambience.Id), Value = MuffledAmbienceDb });
        muffled.Values.Add(new SnapshotValue { Target = PropertyRef.Effect(ambience.Id, 0, "cutoff_hz"), Value = 800f });
        project.Snapshots.Add(muffled);

        var wind = new AudioEvent { Id = Id("66666666666666666666666666666666"), Path = WindEvent, Bus = ambience.Id };
        var windTrack = new Track { Id = Id("77777777777777777777777777777777"), Name = "Bed" };
        windTrack.Instruments.Add(new ClipInstrument
        {
            Id = Id("88888888888888888888888888888888"),
            Name = "Wind loop",
            Asset = new AssetRef(WindAsset),
            Loop = true,
            FadeIn = 0.25f,
            FadeOut = 0.5f,
        });
        wind.Tracks.Add(windTrack);
        project.Events.Add(wind);

        var click = new AudioEvent { Id = Id("99999999999999999999999999999999"), Path = ClickEvent, Bus = master.Id };
        click.Macros.MaxInstances = 4;
        var clickTrack = new Track { Id = Id("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), Name = "Click" };
        clickTrack.Instruments.Add(new ClipInstrument
        {
            Id = Id("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
            Name = "Click",
            Asset = new AssetRef(ClickAsset),
            VolumeDb = -6f,
            PitchRandom = 0.05f,
        });
        click.Tracks.Add(clickTrack);
        project.Events.Add(click);

        return project;
    }

    private static DirgeId Id(string hex) => DirgeId.Parse(hex);
}
