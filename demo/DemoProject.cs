using System.Collections.Generic;
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

    // ---- sandbox ---------------------------------------------------------------------------------------------------

    public const string SandboxAudio = "res://demo/sandbox/audio/";
    public const string MusicBus = "Music";
    public const string SfxBus = "SFX";
    public const string DialogueBus = "Dialogue";
    public const string ThreatParameter = "Threat";
    public const string DuckingSnapshot = "Dialogue Ducking";
    public const string MusicEvent = "Music/Main";
    public const string StingerEvent = "Music/Stinger";
    public const string BirdsEvent = "Ambience/Birds";
    public const string DialogueEvent = "Dialogue/Line";
    public const string SandboxClickEvent = "UI/Click";
    public const string DialogueTable = "Dialogue";
    public const string DialogueKey = "line";
    public const float DuckingMusicDb = -12f;
    public const float DuckingAmbienceDb = -6f;
    public const float SandboxBpm = 120f;
    public const double BeatSeconds = 60.0 / SandboxBpm;
    /// <summary>Music sections: Calm occupies [0, SectionSeconds), Combat [SectionSeconds, 2 × SectionSeconds).</summary>
    public const double SectionSeconds = 8 * BeatSeconds;
    public const double CombatSectionStart = SectionSeconds;
    public const float ThreatCombatThreshold = 0.5f;

    public const string CalmPadAsset = SandboxAudio + "calm_pad.wav";
    public const string CalmArpAsset = SandboxAudio + "calm_arp.wav";
    public const string CombatDrumsAsset = SandboxAudio + "combat_drums.wav";
    public const string CombatLeadAsset = SandboxAudio + "combat_lead.wav";
    public const string StingerAsset = SandboxAudio + "stinger.wav";
    public const string LineEnAsset = SandboxAudio + "line_en.wav";
    public const string LineDeAsset = SandboxAudio + "line_de.wav";
    public const string UiClickAsset = SandboxAudio + "ui_click.wav";
    public static readonly string[] BirdAssets =
    {
        SandboxAudio + "bird_chirp_1.wav",
        SandboxAudio + "bird_chirp_2.wav",
        SandboxAudio + "bird_chirp_3.wav",
    };

    /// <summary>Sample rate of the generated sandbox WAVs (<c>scripts/gen-sandbox-audio.py</c>).</summary>
    private const int SandboxRate = 22050;

    /// <summary>
    /// The sandbox project as code (<c>demo/sandbox/sandbox.dirge.json</c> is serializer output of this). Exercises the
    /// adaptive-music, scatterer, ducking-snapshot, programmer/audio-table and quantized-start paths of the model.
    /// Music is laid out as two horizontal sections on one looping conductor timeline: Calm [0, 4) and Combat [4, 8),
    /// each with its own loop region and a Beat-quantized transition region conditioned on <see cref="ThreatParameter"/>.
    /// Timeline placement is what makes the Combat layers start exactly on a beat; parameter-sheet triggers fire the
    /// moment the parameter crosses and cannot be quantized by the model clock.
    /// </summary>
    public static AudioProject BuildSandbox()
    {
        var project = new AudioProject { Name = "Dirge Sandbox" };
        int next = 1;
        DirgeId NextId() => SandboxId(next++);

        // -- mixer: Master → Music, SFX → Ambience, Dialogue
        var master = new Bus { Id = NextId(), Name = "Master" };
        var music = new Bus { Id = NextId(), Name = MusicBus, Parent = master.Id, VolumeDb = -3f };
        var sfx = new Bus { Id = NextId(), Name = SfxBus, Parent = master.Id };
        var ambience = new Bus { Id = NextId(), Name = AmbienceBus, Parent = sfx.Id, VolumeDb = -2f };
        var dialogue = new Bus { Id = NextId(), Name = DialogueBus, Parent = sfx.Id };
        project.Mixer.Buses.Add(master);
        project.Mixer.Buses.Add(music);
        project.Mixer.Buses.Add(sfx);
        project.Mixer.Buses.Add(ambience);
        project.Mixer.Buses.Add(dialogue);

        // -- ducking snapshot: Music −12, Ambience −6, 0.2 s in / 0.8 s out
        var ducking = new Snapshot
        {
            Id = NextId(),
            Name = DuckingSnapshot,
            Priority = 10,
            Intensity = new Ahdsr { Attack = 0.2f, Release = 0.8f },
        };
        ducking.Values.Add(new SnapshotValue { Target = PropertyRef.BusVolume(music.Id), Value = DuckingMusicDb });
        ducking.Values.Add(new SnapshotValue { Target = PropertyRef.BusVolume(ambience.Id), Value = DuckingAmbienceDb });
        project.Snapshots.Add(ducking);

        // -- global parameter Threat 0..1, seeks at 0.5 units/s both ways
        var threat = new ParameterDefinition
        {
            Id = NextId(),
            Name = ThreatParameter,
            Minimum = 0f,
            Maximum = 1f,
            Default = 0f,
            SeekSpeedUp = 0.5f,
            SeekSpeedDown = 0.5f,
        };
        project.GlobalParameters.Add(threat);

        // -- Music/Stinger: one-shot whose Start() waits for the conductor's next beat
        var stinger = new AudioEvent { Id = NextId(), Path = StingerEvent, Bus = music.Id };
        stinger.Macros.StartQuantization = Quantization.Beat;
        stinger.Macros.MaxInstances = 2;
        var stingerTrack = new Track { Id = NextId(), Name = "Hit" };
        stingerTrack.Instruments.Add(new ClipInstrument
        {
            Id = NextId(),
            Name = "Stinger",
            Asset = new AssetRef(StingerAsset),
            VolumeDb = -2f,
        });
        stinger.Tracks.Add(stingerTrack);
        project.Events.Add(stinger);

        // -- Music/Main: conductor; Calm and Combat layer pairs, each sample-locked through a SyncGroup
        var mainMusic = new AudioEvent { Id = NextId(), Path = MusicEvent, Bus = music.Id, IsConductor = true };
        mainMusic.Macros.Persistent = true;
        mainMusic.Macros.MaxInstances = 1;
        mainMusic.Macros.Priority = 0;
        mainMusic.Tempo.Add(new TempoMarker { Position = 0f, Bpm = SandboxBpm, BeatsPerBar = 4 });

        var calm = new Track { Id = NextId(), Name = "Calm" };
        calm.Instruments.Add(Layer(NextId(), "Calm pad", CalmPadAsset, 0f, syncGroup: 1));
        calm.Instruments.Add(Layer(NextId(), "Calm arp", CalmArpAsset, 0f, syncGroup: 1, volumeDb: -4f));
        var combat = new Track { Id = NextId(), Name = "Combat" };
        combat.Instruments.Add(Layer(NextId(), "Combat drums", CombatDrumsAsset, (float)CombatSectionStart, syncGroup: 2));
        combat.Instruments.Add(Layer(NextId(), "Combat lead", CombatLeadAsset, (float)CombatSectionStart, syncGroup: 2, volumeDb: -3f));
        var cues = new Track { Id = NextId(), Name = "Cues" };
        // Fires once each time Threat enters the combat range; the stinger's own StartQuantization lands it on the beat
        // together with the section transition below.
        cues.Instruments.Add(new CommandInstrument
        {
            Id = NextId(),
            Name = "Combat stinger",
            Action = CommandAction.StartEvent,
            TargetEvent = stinger.Id,
            Placement = new Placement { Parameter = threat.Id, Start = ThreatCombatThreshold, Length = 1f - ThreatCombatThreshold },
        });
        mainMusic.Tracks.Add(calm);
        mainMusic.Tracks.Add(combat);
        mainMusic.Tracks.Add(cues);

        var calmMarker = new DestinationMarker { Id = NextId(), Name = "Calm", Position = 0f };
        var combatMarker = new DestinationMarker { Id = NextId(), Name = "Combat", Position = (float)CombatSectionStart };
        var toCombat = new TransitionRegion
        {
            Id = NextId(),
            Name = "To Combat",
            Position = 0f,
            Length = (float)SectionSeconds,
            Destination = combatMarker.Id,
            Quantization = Quantization.Beat,
        };
        toCombat.Conditions.Add(new TriggerCondition { Parameter = threat.Id, Minimum = ThreatCombatThreshold, Maximum = 1f });
        var toCalm = new TransitionRegion
        {
            Id = NextId(),
            Name = "To Calm",
            Position = (float)CombatSectionStart,
            Length = (float)SectionSeconds,
            Destination = calmMarker.Id,
            Quantization = Quantization.Beat,
        };
        toCalm.Conditions.Add(new TriggerCondition { Parameter = threat.Id, Minimum = 0f, Maximum = ThreatCombatThreshold - 0.01f });
        mainMusic.Markers.Add(calmMarker);
        mainMusic.Markers.Add(new LoopRegion { Id = NextId(), Name = "Calm loop", Position = 0f, Length = (float)SectionSeconds });
        mainMusic.Markers.Add(toCombat);
        mainMusic.Markers.Add(combatMarker);
        mainMusic.Markers.Add(new LoopRegion { Id = NextId(), Name = "Combat loop", Position = (float)CombatSectionStart, Length = (float)SectionSeconds });
        mainMusic.Markers.Add(toCalm);
        project.Events.Add(mainMusic);

        // -- Ambience/Birds: 3D scatterer around the emitter, 2..30 m attenuation
        var birds = new AudioEvent { Id = NextId(), Path = BirdsEvent, Bus = ambience.Id };
        birds.Macros.Is3D = true;
        birds.Macros.MinDistance = 2f;
        birds.Macros.MaxDistance = 30f;
        birds.Macros.Persistent = true;
        birds.Macros.MaxInstances = 1;
        var birdTrack = new Track { Id = NextId(), Name = "Birds" };
        var scatterer = new ScattererInstrument
        {
            Id = NextId(),
            Name = "Chirps",
            MinInterval = 0.4f,
            MaxInterval = 1.6f,
            MinScatterDistance = 1f,
            MaxScatterDistance = 6f,
            Mode = PlaylistMode.Shuffle,
            VolumeRandomDb = 3f,
            PitchRandom = 0.08f,
            Polyphony = 4,
        };
        for (int i = 0; i < BirdAssets.Length; i++)
        {
            scatterer.Entries.Add(new MultiEntry { Asset = new AssetRef(BirdAssets[i]) });
        }

        birdTrack.Instruments.Add(scatterer);
        birds.Tracks.Add(birdTrack);
        project.Events.Add(birds);

        // -- Dialogue/Line: programmer key resolved through the audio table; ducking snapshot for the line's region
        const float lineRegion = 2f;
        var line = new AudioEvent { Id = NextId(), Path = DialogueEvent, Bus = dialogue.Id };
        line.Macros.MaxInstances = 1;
        line.Macros.Stealing = StealingMode.Oldest;
        var voiceTrack = new Track { Id = NextId(), Name = "Voice" };
        voiceTrack.Instruments.Add(new ProgrammerInstrument
        {
            Id = NextId(),
            Name = "Line",
            Key = DialogueKey,
            Placement = new Placement { Start = 0f, Length = lineRegion },
        });
        var duckTrack = new Track { Id = NextId(), Name = "Ducking" };
        duckTrack.Instruments.Add(new SnapshotInstrument
        {
            Id = NextId(),
            Name = "Duck music",
            Snapshot = ducking.Id,
            Placement = new Placement { Start = 0f, Length = lineRegion },
        });
        line.Tracks.Add(voiceTrack);
        line.Tracks.Add(duckTrack);
        project.Events.Add(line);

        var table = new AudioTable { Id = NextId(), Name = DialogueTable };
        table.Entries[DialogueKey] = new Dictionary<string, AssetRef>
        {
            ["en"] = new AssetRef(LineEnAsset),
            ["de"] = new AssetRef(LineDeAsset),
        };
        project.AudioTables.Add(table);

        // -- UI/Click
        var click = new AudioEvent { Id = NextId(), Path = SandboxClickEvent, Bus = sfx.Id };
        click.Macros.MaxInstances = 4;
        var clickTrack = new Track { Id = NextId(), Name = "Click" };
        clickTrack.Instruments.Add(new ClipInstrument
        {
            Id = NextId(),
            Name = "Click",
            Asset = new AssetRef(UiClickAsset),
            VolumeDb = -6f,
            PitchRandom = 0.05f,
        });
        click.Tracks.Add(clickTrack);
        project.Events.Add(click);

        // -- asset lengths (frame counts from gen-sandbox-audio.py: RATE * ms // 1000) so one-shot ends are model-scheduled
        // and a Recording replays without the engine.
        project.AssetLengths[BirdAssets[0]] = Seconds(350);
        project.AssetLengths[BirdAssets[1]] = Seconds(450);
        project.AssetLengths[BirdAssets[2]] = Seconds(300);
        project.AssetLengths[CalmPadAsset] = Seconds(4000);
        project.AssetLengths[CalmArpAsset] = Seconds(4000);
        project.AssetLengths[CombatDrumsAsset] = Seconds(4000);
        project.AssetLengths[CombatLeadAsset] = Seconds(4000);
        project.AssetLengths[StingerAsset] = Seconds(1000);
        project.AssetLengths[LineEnAsset] = Seconds(1600);
        project.AssetLengths[LineDeAsset] = Seconds(1800);
        project.AssetLengths[UiClickAsset] = Seconds(80);

        return project;
    }

    private static ClipInstrument Layer(DirgeId id, string name, string asset, float start, int syncGroup, float volumeDb = 0f) => new()
    {
        Id = id,
        Name = name,
        Asset = new AssetRef(asset),
        Placement = new Placement { Start = start, Length = (float)SectionSeconds },
        Loop = true,
        SyncGroup = syncGroup,
        VolumeDb = volumeDb,
        FadeIn = 0.02f,
        FadeOut = 0.08f,
        CutMode = CutMode.FadeOut,
    };

    /// <summary>Duration of a generated WAV: the generator's integer frame count divided by its rate, exactly as <c>AudioStreamWAV.GetLength</c> reports it.</summary>
    private static double Seconds(int milliseconds) => (SandboxRate * milliseconds / 1000) / (double)SandboxRate;

    private static DirgeId SandboxId(int n) => DirgeId.Parse($"5a4db0c0{n:x24}");

    private static DirgeId Id(string hex) => DirgeId.Parse(hex);
}
