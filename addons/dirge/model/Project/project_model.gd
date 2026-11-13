@tool
extends RefCounted
## Authored dictionaries keep the original JSON names. Unknown fields survive editing.
const NONE = "00000000000000000000000000000000"
const PROPERTY_KINDS = ["BusVolumeDb", "BusPitch", "EffectParameter", "VcaVolumeDb", "EventVolumeDb", "EventPitch", "InstrumentVolumeDb", "InstrumentPitch"]
const INSTRUMENT_TYPES = ["Clip", "Multi", "Scatterer", "Silence", "Event", "Programmer", "Command", "Snapshot"]
const MODULATOR_TYPES = ["Random", "Ahdsr", "Lfo", "Autopitch"]
const MARKER_TYPES = ["Destination", "Loop", "Transition", "TransitionRegion", "SustainPoint", "Magnet"]

static func new_id() -> String:
	return Crypto.new().generate_random_bytes(16).hex_encode()

static func is_none(id: Variant) -> bool:
	return id == NONE or id == "" or id == null

static func property_ref(kind: String = "BusVolumeDb", owner: String = NONE, effect_index: int = -1, name: Variant = null) -> Dictionary:
	var result = {"kind": kind, "owner": owner}
	if effect_index != -1: result.effectIndex = effect_index
	if name != null: result.name = name
	return result

static func defaults(kind: String = "AudioProject") -> Dictionary:
	var d: Dictionary = {}
	match kind:
		"AudioProject": d = {"name":"", "mixer":defaults("MixerGraph"), "globalParameters":[], "snapshots":[], "events":[], "banks":[], "audioTables":[], "platformOverrides":[], "mixerAutomation":[], "mixerModulators":[], "assetLengths":{}}
		"MixerGraph": d = {"buses":[], "vcas":[]}
		"Bus": d = {"id":new_id(), "name":"", "parent":NONE, "volumeDb":0.0, "mute":false, "solo":false, "bypassEffects":false, "effects":[], "faderIndex":2147483647, "vcas":[], "maxVoices":0}
		"BusEffect": d = {"id":new_id(), "typeName":"", "enabled":true, "parameters":{}, "stringParameters":{}}
		"Vca": d = {"id":new_id(), "name":"", "volumeDb":0.0}
		"Snapshot": d = {"id":new_id(), "name":"", "priority":0, "mode":"Blending", "intensity":defaults("Ahdsr"), "values":[]}
		"SnapshotValue": d = {"target":property_ref("BusVolumeDb", NONE, 0), "value":0.0}
		"AudioBank": d = {"id":new_id(), "name":"", "events":[]}
		"AudioTable": d = {"id":new_id(), "name":"", "entries":{}}
		"PlatformOverride": d = {"platform":"", "values":[]}
		"ParameterDefinition": d = {"id":new_id(), "name":"", "kind":"Continuous", "builtIn":"None", "minimum":0.0, "maximum":1.0, "default":0.0, "seekSpeedUp":0.0, "seekSpeedDown":0.0, "hold":true, "labels":[]}
		"Curve": d = {"points":[]}
		"AutomationLane": d = {"id":new_id(), "target":property_ref("BusVolumeDb", NONE, 0), "parameter":NONE, "curve":defaults("Curve")}
		"Ahdsr": d = {"attack":0.0, "hold":0.0, "decay":0.0, "sustain":1.0, "release":0.0}
		"Modulator": d = {"id":new_id(), "target":property_ref("BusVolumeDb", NONE, 0), "amount":0.0}
		"Random", "AhdsrModulator", "Lfo", "Autopitch":
			d = defaults("Modulator")
			d["$type"] = "Ahdsr" if kind == "AhdsrModulator" else kind
			match kind:
				"AhdsrModulator": d.envelope = defaults("Ahdsr")
				"Lfo": d.merge({"shape":"Sine", "rateHz":1.0, "phase":0.0, "tempoSync":false})
				"Autopitch": d.merge({"parameter":NONE, "referenceValue":0.0, "pitchAtMinimum":0.5, "pitchAtMaximum":2.0})
		"EventMacros": d = {"volumeDb":0.0, "pitch":1.0, "priority":128, "maxInstances":0, "stealing":"Oldest", "cooldown":0.0, "persistent":false, "is3D":false, "minDistance":1.0, "maxDistance":20.0, "rolloff":"Inverse", "doppler":false, "dopplerScale":1.0, "scheduleLookahead":0.0, "startQuantization":"None"}
		"AudioEvent": d = {"id":new_id(), "path":"", "bus":NONE, "isConductor":false, "tags":[], "macros":defaults("EventMacros"), "localParameters":[], "tracks":[], "automation":[], "modulators":[], "markers":[], "tempo":[]}
		"Track": d = {"id":new_id(), "name":"", "volumeDb":0.0, "instruments":[]}
		"Placement": d = {"parameter":NONE, "start":0.0, "length":0.0}
		"TriggerCondition": d = {"parameter":NONE, "minimum":0.0, "maximum":0.0}
		"Instrument": d = {"id":new_id(), "name":"", "placement":defaults("Placement"), "volumeDb":0.0, "pitch":1.0, "volumeRandomDb":0.0, "pitchRandom":0.0, "startOffset":0.0, "fadeIn":0.0, "fadeOut":0.0, "loop":false, "async":false, "cutMode":"Cut", "polyphony":0, "triggerDelay":0.0, "probability":1.0, "syncGroup":0, "conditions":[], "modulators":[]}
		"Clip", "Multi", "Scatterer", "Silence", "Event", "Programmer", "Command", "SnapshotInstrument":
			d = defaults("Instrument")
			d["$type"] = "Snapshot" if kind == "SnapshotInstrument" else kind
			match kind:
				"Clip": d.asset = ""
				"Multi": d.merge({"mode":"Random", "avoidRepeat":1, "entries":[]})
				"Scatterer": d.merge({"minInterval":0.5, "maxInterval":2.0, "minScatterDistance":0.0, "maxScatterDistance":10.0, "mode":"Random", "entries":[]})
				"Event": d.event = NONE
				"Programmer": d.key = ""
				"Command": d.merge({"action":"StartEvent", "targetEvent":NONE, "targetParameter":NONE, "value":0.0})
				"SnapshotInstrument": d.snapshot = NONE
		"MultiEntry": d = {"asset":"", "weight":1.0}
		"LogicMarker": d = {"id":new_id(), "name":"", "position":0.0, "conditions":[], "probability":1.0}
		"Destination", "Loop", "Transition", "TransitionRegion", "SustainPoint", "Magnet":
			d = defaults("LogicMarker")
			d["$type"] = kind
			if kind in ["Loop", "TransitionRegion", "Magnet"]: d.length = 0.0
			if kind in ["Transition", "TransitionRegion"]: d.merge({"destination":NONE, "quantization":"Beat" if kind == "TransitionRegion" else "None", "transitionLength":0.0, "transitionInstruments":[]})
		"TempoMarker": d = {"position":0.0, "bpm":120.0, "beatsPerBar":4}
	return d

static func create(kind: String, values: Dictionary = {}) -> Dictionary:
	return hydrate(values, kind)

static func new_project(name: String = "") -> Dictionary:
	return create("AudioProject", {"name":name})

static func hydrate(value: Dictionary, kind: String = "AudioProject") -> Dictionary:
	if kind == "Instrument":
		kind = value.get("$type", "")
		if kind == "Snapshot": kind = "SnapshotInstrument"
	elif kind == "Modulator":
		kind = value.get("$type", "")
		if kind == "Ahdsr": kind = "AhdsrModulator"
	elif kind == "LogicMarker": kind = value.get("$type", "")
	var result = defaults(kind)
	# Restore schema numeric types: JSON parses every number as a float.
	var supplied = value.duplicate(true)
	for key in supplied:
		var item = supplied[key]
		if item is int or item is float:
			if result.get(key) is int: item = int(item)
			elif result.get(key) is float: item = float(item)
		result[key] = item
	for key in result:
		var child = child_kind(kind, key)
		if child == "": continue
		if result[key] is Dictionary:
			result[key] = hydrate(result[key], child)
		elif result[key] is Array:
			for i in result[key].size():
				if result[key][i] is Dictionary: result[key][i] = hydrate(result[key][i], child)
	if kind in ["Clip", "MultiEntry"] and result.get("asset") == null: result.asset = ""
	if kind == "AudioTable":
		for locales in result.entries.values():
			if locales is Dictionary:
				for locale in locales:
					if locales[locale] == null: locales[locale] = ""
	if kind == "Curve":
		# Compact arrays are the authored/saved shape; named points are accepted at API boundaries.
		for i in result.points.size():
			var point = result.points[i]
			if point is Dictionary:
				result.points[i] = [float(point.x), float(point.y), point.get("shape", "Linear")]
			elif point is Array and point.size() >= 2:
				point[0] = float(point[0])
				point[1] = float(point[1])
				if point.size() == 2: point.append("Linear")
	if result.get("target") is Dictionary and result.target.has("effectIndex"):
		result.target.effectIndex = int(result.target.effectIndex)
	if kind == "Placement": result.erase("onTimeline")
	return result

static func child_kind(kind: String, key: String) -> String:
	match key:
		"mixer": return "MixerGraph"
		"buses": return "Bus"
		"vcas": return "Vca" if kind == "MixerGraph" else ""
		"globalParameters", "localParameters": return "ParameterDefinition"
		"snapshots": return "Snapshot"
		"events": return "AudioEvent" if kind == "AudioProject" else ""
		"banks": return "AudioBank"
		"audioTables": return "AudioTable"
		"platformOverrides": return "PlatformOverride"
		"automation", "mixerAutomation": return "AutomationLane"
		"modulators", "mixerModulators": return "Modulator"
		"effects": return "BusEffect"
		"values": return "SnapshotValue"
		"intensity", "envelope": return "Ahdsr"
		"curve": return "Curve"
		"macros": return "EventMacros"
		"tracks": return "Track"
		"placement": return "Placement"
		"instruments", "transitionInstruments": return "Instrument"
		"conditions": return "TriggerCondition"
		"entries": return "MultiEntry" if kind in ["Multi", "Scatterer"] else ""
		"markers": return "LogicMarker"
		"tempo": return "TempoMarker"
	return ""
