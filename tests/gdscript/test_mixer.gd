extends RefCounted

const Mixer = preload("res://addons/dirge/model/Runtime/mixer_state.gd")
const ParameterBank = preload("res://addons/dirge/model/Runtime/parameter_bank.gd")
const Modulators = preload("res://addons/dirge/model/Evaluation/modulators.gd")

static func id(value: int) -> String:
	return "%032x" % value

static func target(kind: String, owner: int, effect: int = -1, name = null) -> Dictionary:
	return {"kind": kind, "owner": id(owner), "effectIndex": effect, "name": name}

static func project() -> Dictionary:
	return {"name": "Mixer regression", "mixer": {"buses": [
		{"id": id(1), "name": "Master", "effects": [], "vcas": []},
		{"id": id(2), "name": "Music", "parent": id(1), "volumeDb": 0.0, "faderIndex": 0, "vcas": [id(4)], "effects": [{"typeName": "AudioEffectLowPassFilter", "enabled": true, "parameters": {"cutoff_hz": 2000.0, "resonance": 0.5}}]},
		{"id": id(3), "name": "SFX", "parent": id(1), "volumeDb": -3.0, "vcas": [id(4)], "effects": []}],
		"vcas": [{"id": id(4), "name": "Main", "volumeDb": 0.0}]},
		"globalParameters": [{"id": id(5), "name": "Intensity", "minimum": 0.0, "maximum": 1.0, "default": 0.0}],
		"snapshots": [], "mixerAutomation": [], "mixerModulators": [], "platformOverrides": []}

static func snapshot(name: String, priority: int, mode: String, values: Array, envelope: Dictionary = {}) -> Dictionary:
	return {"name": name, "priority": priority, "mode": mode, "values": values, "intensity": envelope}

static func value(property: Dictionary, amount: float) -> Dictionary:
	return {"target": property, "value": amount}

static func build(p: Dictionary, seed: int = 1, platform: String = "") -> Dictionary:
	var commands: Array = []
	var globals = ParameterBank.new(p.globalParameters)
	return {"mixer": Mixer.new(p, globals, commands, Modulators.DirgeRandom.new(seed), platform), "commands": commands, "globals": globals}

func run(test: Object) -> void:
	_baseline(test)
	_snapshots(test)
	_envelopes(test)
	_automation_platform(test)
	_pitch(test)
	_modulation(test)
	_ownership(test)

func _baseline(test: Object) -> void:
	var p: Dictionary = project()
	var f: Dictionary = build(p)
	var m = f.mixer
	m.update(0.016)
	test.check(f.commands.size() == 15, "Mixer first tick emits all bus flags and effect state")
	test.check(f.commands[0] == {"type": "BusSetVolume", "bus": "Master", "volumeDb": 0.0}, "Mixer command order begins with master volume")
	test.check(m.bus_count == 3 and m.bus_name(id(3)) == "SFX" and m.try_get_bus("Music") == id(2) and m.try_get_vca("Main") == id(4), "Mixer IDs and names resolve")
	test.check(m.try_get_bus("absent") == "", "Unknown lookup has empty sentinel")
	f.commands.clear()
	m.update(0.5)
	test.check(f.commands.is_empty(), "Steady mixer emits nothing")
	m.set_vca_volume("Main", -6.0)
	m.set_bus_volume("Music", -4.0)
	m.update(0)
	test.check(m.effective_bus_volume_db(id(2)) == -10.0 and m.effective_bus_volume_db(id(3)) == -9.0 and m.effective_bus_volume_db(id(1)) == 0.0, "VCA sums and runtime offsets affect only members, not hierarchy")
	test.check(m.effective_vca_volume_db(id(4)) == -6.0, "VCA effective value includes offset")
	f.commands.clear()
	m.set_bus_mute("SFX", true)
	m.set_bus_solo("Music", true)
	m.update(0)
	test.check(f.commands.size() == 2, "Mute and solo emit changes only")
	f.commands.clear()
	p.mixer.buses[1].bypassEffects = true
	p.mixer.buses[1].effects[0].enabled = false
	m.update(0)
	test.check(f.commands == [{"type": "BusSetBypass", "bus": "Music", "bypass": true}, {"type": "EffectSetEnabled", "bus": "Music", "effectIndex": 0, "enabled": false}], "Live bypass and enabled flags retain model effect index across internal fader")
	p.mixer.buses[2].mute = true
	m.set_bus_mute("SFX", false)
	f.commands.clear()
	m.update(0)
	test.check(f.commands.is_empty(), "Runtime false cannot clear authored mute")

func _snapshots(test: Object) -> void:
	var p: Dictionary = project()
	p.snapshots = [snapshot("Low", 1, "Blending", [value(target("BusVolumeDb", 2), -20.0)]), snapshot("High", 5, "Overriding", [value(target("BusVolumeDb", 2), -6.0)]), snapshot("Filter", 0, "Blending", [value(target("EffectParameter", 2, 0, "cutoff_hz"), 500.0), value(target("BusVolumeDb", 999), 100.0)])]
	var f: Dictionary = build(p)
	var m = f.mixer
	var high = m.start_snapshot("High")
	high.intensity = 0.5
	m.start_snapshot("Low")
	m.update(0)
	test.check(m.effective_bus_volume_db(id(2)) == -3.0, "Higher overriding snapshot lerps from authored despite reverse start order")
	high.intensity = 0
	m.update(0)
	test.check(m.effective_bus_volume_db(id(2)) == -20.0, "Zero overriding intensity does not erase lower scope")
	high.intensity = 2
	m.update(0)
	test.check(high.intensity == 1 and m.effective_bus_volume_db(id(2)) == -6.0, "Snapshot intensity clamps high")
	high.intensity = -1
	test.check(high.intensity == 0, "Snapshot intensity clamps low")
	f.commands.clear()
	m.start_snapshot("Filter")
	m.update(0)
	test.check(f.commands.has({"type": "EffectSetParameter", "bus": "Music", "effectIndex": 0, "parameter": "cutoff_hz", "value": 500.0}), "Scoped effect snapshot ignores unknown target and emits correct property")
	p.snapshots = [snapshot("A", 1, "Blending", [value(target("BusVolumeDb", 2), -12.0)]), snapshot("B", 1, "Blending", [value(target("BusVolumeDb", 2), -6.0)])]
	f = build(p)
	f.mixer.start_snapshot("A")
	f.mixer.start_snapshot("B").intensity = 0.5
	f.mixer.update(0)
	test.check(f.mixer.effective_bus_volume_db(id(2)) == -9.0, "Equal-priority snapshots retain start order")

func _envelopes(test: Object) -> void:
	var p: Dictionary = project()
	p.snapshots = [snapshot("Envelope", 0, "Blending", [value(target("BusVolumeDb", 2), -12.0)], {"attack": 1.0, "hold": 0.5, "decay": 1.0, "sustain": 0.5, "release": 1.0})]
	var f: Dictionary = build(p)
	var s = f.mixer.start_snapshot("Envelope")
	f.mixer.update(0.5)
	test.check(is_equal_approx(s.effective_intensity, 0.5), "Snapshot half attack")
	f.mixer.update(0.75)
	test.check(s.effective_intensity == 1.0, "Snapshot hold")
	f.mixer.update(0.75)
	test.check(is_equal_approx(s.effective_intensity, 0.75), "Snapshot decay")
	f.mixer.update(0.5)
	test.check(s.effective_intensity == 0.5, "Snapshot sustain")
	s.stop()
	f.mixer.update(0.5)
	s.stop()
	test.check(is_equal_approx(s.effective_intensity, 0.25), "Release starts at actual sustain; repeated stop is idempotent")
	f.mixer.update(0.5)
	test.check(not s.is_active and f.mixer.active_snapshot_count == 0 and f.mixer.effective_bus_volume_db(id(2)) == 0, "Finished snapshot removes and restores baseline")
	var recycled = f.mixer.start_snapshot("Envelope")
	test.check(recycled == s and recycled.intensity == 1.0, "Snapshot pooling resets intensity and state")
	recycled.snapshot.intensity.release = 0.0
	recycled.stop()
	f.mixer.update(0)
	test.check(not recycled.is_active, "Zero release retires at next update")

func _automation_platform(test: Object) -> void:
	var p: Dictionary = project()
	var lane: Dictionary = {"id": id(20), "target": target("BusVolumeDb", 2), "parameter": id(5), "curve": {"points": [[0.0, 0.0], [1.0, -10.0]]}}
	p.mixerAutomation = [lane, {"target": target("BusVolumeDb", 999), "parameter": id(5)}, {"target": target("BusVolumeDb", 2), "parameter": id(999)}]
	p.platformOverrides = [{"platform": "LiNuX", "values": [value(target("BusVolumeDb", 2), -2), value(target("VcaVolumeDb", 4), -1), value(target("EffectParameter", 2, 0, "cutoff_hz"), 1000)]}]
	var f: Dictionary = build(p, 1, "linux")
	f.globals.set_value("Intensity", 0.5, true)
	f.mixer.set_bus_volume("Music", -4)
	f.mixer.update(0)
	test.check(f.mixer.effective_bus_volume_db(id(2)) == -12, "Platform baseline then automation then VCA and runtime offset")
	test.check(f.commands.has({"type": "EffectSetParameter", "bus": "Music", "effectIndex": 0, "parameter": "cutoff_hz", "value": 1000.0}), "Platform overrides effect parameter")
	test.check(f.mixer.remove_automation(id(20)) and not f.mixer.remove_automation(id(20)), "Automation removal reports membership")
	f.mixer.update(0)
	test.check(f.mixer.effective_bus_volume_db(id(2)) == -7, "Removing automation restores evaluated baseline")
	f.mixer.add_automation(lane)
	f.mixer.update(0)
	test.check(f.mixer.effective_bus_volume_db(id(2)) == -12, "Runtime automation registration")

func _pitch(test: Object) -> void:
	for explicit in [false, true]:
		var p: Dictionary = project()
		p.mixer.buses[2].effects = [{"typeName": "AudioEffectPitchShift", "parameters": {"pitch_scale": 2.0} if explicit else {}}]
		p.snapshots = [snapshot("Slow", 0, "Blending", [value(target("BusPitch", 3), 0.5), value(target("BusPitch", 2), 0.5)])]
		var f: Dictionary = build(p)
		f.mixer.update(0)
		f.commands.clear()
		f.mixer.start_snapshot("Slow")
		f.mixer.update(0)
		test.check(f.commands == [{"type": "EffectSetParameter", "bus": "SFX", "effectIndex": 0, "parameter": "pitch_scale", "value": 1.0 if explicit else 0.5}], "Pitch emits once, merges explicit parameter, skips bus without pitch effect")
		test.check(f.mixer.effective_bus_pitch(id(2)) == 0.5, "Pitch retained even without engine effect")

func _modulation(test: Object) -> void:
	var p: Dictionary = project()
	p.mixer.buses[1].volumeDb = -6.0
	var lfo: Dictionary = {"id": id(30), "$type": "Lfo", "target": target("BusVolumeDb", 2), "amount": 6.0, "rateHz": 1.0, "shape": "Sine"}
	p.mixerModulators = [lfo, {"$type": "Ahdsr", "target": target("BusVolumeDb", 2), "amount": -80.0}]
	var f: Dictionary = build(p)
	f.mixer.update(0)
	test.check(is_equal_approx(f.mixer.effective_bus_volume_db(id(2)), -6), "LFO initial phase; unsupported mixer modulator skipped")
	for expected in [0.0, -6.0, -12.0, -6.0]:
		f.mixer.update(0.25)
		test.check(is_equal_approx(f.mixer.effective_bus_volume_db(id(2)), expected), "Mixer LFO quarter-period waveform")
	test.check(f.mixer.remove_modulator(id(30)) and not f.mixer.remove_modulator(id(30)), "Modulator removal reports membership")
	f.mixer.add_modulator({"id": id(31), "$type": "Lfo", "target": target("BusPitch", 2), "amount": 2.0, "shape": "Square", "phase": 0.5})
	f.mixer.update(0)
	test.check(is_equal_approx(f.mixer.effective_bus_pitch(id(2)), 0.01), "Pitch modulation is multiplicative and bounded positive")
	p.mixerModulators = [{"$type": "Random", "target": target("BusVolumeDb", 2), "amount": 3.0}]
	var a: Dictionary = build(p, 11)
	var b: Dictionary = build(p, 11)
	var c: Dictionary = build(p, 12)
	for fixture in [a, b, c]: fixture.mixer.update(0)
	test.check(a.mixer.effective_bus_volume_db(id(2)) == b.mixer.effective_bus_volume_db(id(2)) and a.mixer.effective_bus_volume_db(id(2)) != c.mixer.effective_bus_volume_db(id(2)), "Random mixer modulation is seeded")
	a.commands.clear()
	a.mixer.update(10)
	test.check(a.commands.is_empty(), "Random mixer offset remains fixed")
	p.mixerModulators = [
		{"$type": "Lfo", "target": target("EffectParameter", 2, 0, "cutoff_hz"), "amount": 500.0, "shape": "Square", "rateHz": 1.0},
		{"$type": "Lfo", "target": target("VcaVolumeDb", 4), "amount": 2.0, "shape": "Square", "rateHz": 1.0}]
	f = build(p)
	f.mixer.update(0)
	test.check(f.commands.has({"type": "EffectSetParameter", "bus": "Music", "effectIndex": 0, "parameter": "cutoff_hz", "value": 2500.0}), "Effect LFO adds parameter units")
	test.check(f.mixer.effective_vca_volume_db(id(4)) == 2 and f.mixer.effective_bus_volume_db(id(2)) == -4, "VCA modulation sums into member buses")
	f.commands.clear()
	f.mixer.update(0.5)
	test.check(f.commands.has({"type": "EffectSetParameter", "bus": "Music", "effectIndex": 0, "parameter": "cutoff_hz", "value": 1500.0}), "Effect LFO square negative half")
	p.mixerAutomation = [{"id": id(40), "target": target("BusVolumeDb", 2), "parameter": id(5), "curve": {"points": [[0.0, 0.0], [1.0, -4.0]]}}]
	p.mixerModulators = [{"$type": "Lfo", "target": target("BusVolumeDb", 2), "amount": 2.0, "shape": "Square"}]
	f = build(p)
	f.globals.set_value("Intensity", 1.0, true)
	f.mixer.set_bus_volume("Music", 1.0)
	f.mixer.update(0)
	test.check(f.mixer.effective_bus_volume_db(id(2)) == -7, "Mixer chain is baseline, automation, modulation, runtime offset")
	p.mixerAutomation = []
	p.mixerModulators = [{"$type": "Lfo", "target": target("BusVolumeDb", 2), "amount": 6.0, "shape": "Sine", "rateHz": 4.0, "tempoSync": true}]
	f = build(p)
	f.mixer.update(0.0625)
	test.check(is_equal_approx(f.mixer.effective_bus_volume_db(id(2)), 0), "Mixer tempo-sync flag still interprets rate as Hz")
	p.mixerModulators = [{"$type": "Lfo", "target": target("BusVolumeDb", 2), "amount": 3.0, "shape": "Noise", "rateHz": 1.0}]
	a = build(p, 21)
	b = build(p, 21)
	for dt in [0.0, 0.25, 0.9, 0.5, 0.8]:
		a.mixer.update(dt)
		b.mixer.update(dt)
		test.check(a.mixer.effective_bus_volume_db(id(2)) == b.mixer.effective_bus_volume_db(id(2)), "Noise LFO consumes shared RNG deterministically")

func _ownership(test: Object) -> void:
	var p: Dictionary = project()
	p.snapshots = [snapshot("Owned", 0, "Blending", [])]
	var fixture: Dictionary = build(p)
	var mixer_ref: WeakRef = weakref(fixture.mixer)
	var running = fixture.mixer.start_snapshot("Owned")
	fixture.clear()
	test.check(mixer_ref.get_ref() == null, "Snapshot handle does not retain owning mixer")
	running.stop()
	running.intensity = 0.5
	test.check(running.intensity == 0.5, "Detached snapshot handle remains safe to stop or adjust")
