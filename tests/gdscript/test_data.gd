extends RefCounted
const M = preload("res://addons/dirge/model/Project/project_model.gd")
const Codec = preload("res://addons/dirge/model/Serialization/project_json.gd")
const Validator = preload("res://addons/dirge/model/Validation/project_validator.gd")

func run(test: Object) -> void:
	_json(test)
	_curve_inputs(test)
	_validation(test)
	_scope_and_graph_validation(test)

func _minimal() -> Dictionary:
	var p = M.new_project("Fixture")
	var bus = M.create("Bus", {"name":"Master"})
	p.mixer.buses.append(bus)
	var ev = M.create("AudioEvent", {"path":"Music/Main", "bus":bus.id})
	var track = M.create("Track", {"name":"Track"})
	track.instruments.append(M.create("Clip", {"asset":"res://a.ogg"}))
	ev.tracks.append(track)
	p.events.append(ev)
	return p

func _json(test: Object) -> void:
	var sparse = Codec.deserialize('{"name":"Sparse","events":[{"path":"X","tracks":[{"instruments":[{"$type":"Clip","asset":null}]}]}]}')
	var clip: Dictionary = sparse.events[0].tracks[0].instruments[0]
	test.check(clip.pitch == 1 and clip.probability == 1 and clip.asset == "" and clip.id.length() == 32, "Sparse clip defaults, generated ID, null asset")
	test.check(sparse.events[0].macros.priority == 128 and sparse.events[0].macros.maxDistance == 20 and sparse.events[0].macros.dopplerScale == 1, "Sparse event macro defaults")
	test.check(sparse.mixer.buses.is_empty() and sparse.mixerModulators.is_empty() and sparse.audioTables.is_empty() and sparse.events[0].tags.is_empty(), "Old project omitted lists hydrate")
	var p = _minimal()
	var bus: Dictionary = p.mixer.buses[0]
	var ev: Dictionary = p.events[0]
	var t: Dictionary = ev.tracks[0]
	var param = M.create("ParameterDefinition", {"name":"Rpm", "maximum":4000, "default":1000, "seekSpeedUp":2, "seekSpeedDown":0.5})
	p.globalParameters.append(param)
	ev.isConductor = true
	ev.tags = ["music", "Adaptive"]
	ev.macros.startQuantization = "Bar"
	ev.macros.scheduleLookahead = 0.25
	for kind in M.INSTRUMENT_TYPES:
		var item = M.create("Instrument", {"$type":kind, "syncGroup":2})
		if kind == "Clip": item.asset = "res://clip.wav"
		if kind in ["Multi", "Scatterer"]: item.entries.append(M.create("MultiEntry", {"asset":"res://multi.ogg", "weight":2}))
		t.instruments.append(item)
	for kind in M.MODULATOR_TYPES:
		ev.modulators.append(M.create("Modulator", {"$type":kind, "target":M.property_ref("EventPitch", ev.id), "parameter":param.id, "amount":1, "referenceValue":1000}))
	for kind in M.MARKER_TYPES:
		var marker = M.create(kind)
		if kind in ["Transition", "TransitionRegion"]:
			marker.transitionLength = 0.5
			marker.transitionInstruments.append(M.create("Silence", {"placement":{"length":0.5}}))
		ev.markers.append(marker)
	ev.tempo.append(M.create("TempoMarker", {"bpm":96, "beatsPerBar":3}))
	var effect = M.create("BusEffect", {"typeName":"AudioEffectCompressor", "parameters":{"threshold":-12, "ratio":4}, "stringParameters":{"sidechain":"SFX"}})
	bus.effects.append(effect)
	var vca = M.create("Vca", {"name":"Music", "volumeDb":-1.5})
	p.mixer.vcas.append(vca)
	bus.vcas.append(vca.id)
	var snap = M.create("Snapshot", {"mode":"Overriding", "intensity":{"attack":0.1, "hold":0.2, "decay":0.3, "sustain":0.8, "release":0.4}})
	snap.values.append(M.create("SnapshotValue", {"target":M.property_ref("EffectParameter", bus.id, 0, "threshold"), "value":-20}))
	p.snapshots.append(snap)
	p.banks.append(M.create("AudioBank", {"name":"Main", "events":[ev.id]}))
	p.audioTables.append(M.create("AudioTable", {"name":"Dialogue", "entries":{"hello":{"fr":"res://fr.ogg", "en":"res://en.ogg"}, "blank":{"en":null}}}))
	p.platformOverrides.append(M.create("PlatformOverride", {"platform":"Android", "values":[{"target":M.property_ref("BusVolumeDb", bus.id), "value":-6}]}))
	p.mixerAutomation.append(M.create("AutomationLane", {"parameter":param.id, "target":M.property_ref("BusVolumeDb", bus.id), "curve":{"points":[[0, -20, "Smooth"], [0.5, -6, "Stepped"], [1, 0]]}}))
	p.mixerModulators.append(M.create("Lfo", {"target":M.property_ref("BusVolumeDb", bus.id), "shape":"Triangle", "rateHz":4, "phase":0.25}))
	p.assetLengths = {"res://z.ogg":1, "res://a.ogg":2}
	p.extension = {"future":42}
	var text = Codec.serialize(p)
	var compact = Codec.serialize(p, false)
	var copy = Codec.deserialize(text)
	test.check(not copy.is_empty(), "Full JSON parses: " + Codec.last_error)
	if copy.is_empty(): return
	test.check(Codec.serialize(copy) == text and Codec.serialize(Codec.deserialize(compact)) == text, "Full JSON stable compact and indented roundtrip")
	test.check(not compact.contains("\n") and not compact.contains("onTimeline") and compact.begins_with('{"name":"Fixture","assetLengths":'), "Canonical property order, no computed properties")
	test.check(compact.find('"res://a.ogg":2') < compact.find('"res://z.ogg":1'), "Dictionary keys sorted")
	test.check(compact.find('"en":"res://en.ogg"') < compact.find('"fr":"res://fr.ogg"'), "Audio locales ordinally sorted")
	test.check(copy.events[0].tags == ["music", "Adaptive"] and copy.extension.future == 42, "Tags preserve order and unknown extensions survive")
	test.check(copy.events[0].tracks[0].instruments.size() == 9 and copy.events[0].modulators.size() == 4 and copy.events[0].markers.size() == 6, "All polymorphic kinds survive")
	test.check(copy.events[0].markers[2].transitionInstruments[0]["$type"] == "Silence" and copy.events[0].isConductor, "Transition timeline and conductor survive")
	test.check(copy.mixer.buses[0].faderIndex == 2147483647 and copy.snapshots[0].intensity.sustain == 0.8, "Fader and nested envelope defaults survive")
	test.check(copy.mixerAutomation[0].curve.points[2] == [1.0, 0.0, "Linear"], "Two-value curve point defaults Linear")
	for bad in ["null", "[]", "{", '{"events":[{"id":"bad"}]}', '{"events":[{"tracks":[{"instruments":[{"$type":"Granular"}]}]}]}', '{"events":[{"tracks":[{"instruments":[{}]}]}]}', '{"events":[{"markers":[{"$type":"Marker"}]}]}', '{"mixerModulators":[{"$type":"Wobble"}]}', '{"snapshots":[{"values":[{"target":{}}]}]}', '{"snapshots":[{"values":[{"target":{"kind":"BusVolumeDb","extra":1}}]}]}', '{"mixerAutomation":[{"curve":{"points":[[0,0,"Bogus"]]}}]}']:
		test.check(Codec.deserialize(bad).is_empty() and Codec.last_error != "", "Reject malformed format: " + bad)
	var normalized = Codec.deserialize('{"events":[{"id":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","macros":{"stealing":"quietest"}}]}')
	test.check(normalized.events[0].id == "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" and normalized.events[0].macros.stealing == "Quietest", "Guid and enum normalization match converters")

func _curve_inputs(test: Object) -> void:
	var source = {"points":[[0, -20], {"x":0.5, "y":-6, "shape":"Smooth"}, {"x":1, "y":0}]}
	var original = source.duplicate(true)
	var curve = M.create("Curve", source)
	var expected = [[0.0, -20.0, "Linear"], [0.5, -6.0, "Smooth"], [1.0, 0.0, "Linear"]]
	test.check(curve.points == expected and source == original, "Hydration canonicalizes compact and named curve points without changing input")
	var p = _minimal()
	p.events[0].automation.append(M.create("AutomationLane", {"curve":source}))
	p.assetLengths = {&"res://z.ogg":1.0, "res://a.ogg":2}
	p.extension = {&"z":1.0, "a":2, "fraction":0.125}
	for indented in [false, true]:
		var text = Codec.serialize(p, indented)
		var decoded = Codec.deserialize(text)
		test.check(not decoded.is_empty(), "Mixed curve input saves compatible JSON")
		if decoded.is_empty(): continue
		test.check(decoded.events[0].automation[0].curve.points == expected, "Compact serialized points retain coordinates and shapes")
		test.check(Codec.serialize(decoded, indented) == text, "Mixed StringName keys and numeric types serialize deterministically")
		var raw = JSON.parse_string(text)
		test.check(raw.events[0].automation[0].curve.points == expected, "Saved curve points remain compact three-value arrays")
	var reopened = Codec.deserialize(Codec.serialize(_minimal()))
	test.check(reopened.events[0].macros.priority is int and reopened.events[0].macros.pitch is float, "JSON restores integer and floating schema fields")
	var authored = _minimal()
	test.check(Codec.deserialize(Codec.serialize(authored)) == authored, "Hydrated project compares equal after save and reopen")

func _only(test: Object, p: Dictionary, code: String, subject: String, severity: String = "Error") -> void:
	var d = Validator.validate(p)
	test.check(d.size() == 1 and d[0].code == code and d[0].subject == subject and d[0].severity == severity and d[0].message != "", "Diagnostic %s on %s: %s" % [code, subject, str(d)])

func _validation(test: Object) -> void:
	test.check(Validator.validate(_minimal()).is_empty(), "Minimal valid project")
	_only(test, M.new_project(), "master-bus-missing", M.NONE)
	var p = _minimal()
	p.mixer.buses[0].name = "Main"
	_only(test, p, "master-bus-name", p.mixer.buses[0].id)
	for row in [["faderIndex", -1, "bus-fader-index-negative"], ["vcas", [M.new_id()], "vca-ref-missing"]]:
		p = _minimal()
		p.mixer.buses[0][row[0]] = row[1]
		_only(test, p, row[2], p.mixer.buses[0].id)
	p = _minimal()
	var orphan = M.create("Bus", {"name":"Orphan", "parent":M.new_id()})
	p.mixer.buses.append(orphan)
	_only(test, p, "bus-parent-missing", orphan.id)
	p = _minimal()
	var a = M.create("Bus", {"name":"A"})
	var b = M.create("Bus", {"name":"B", "parent":a.id})
	a.parent = b.id
	p.mixer.buses.append_array([a, b])
	_only(test, p, "bus-cycle", a.id)
	p = _minimal()
	p.mixer.buses.append(M.create("Bus", {"name":"Second"}))
	var diagnostics = Validator.validate(p)
	test.check(diagnostics.size() == 3 and diagnostics[0].code == "master-bus-multiple" and diagnostics[1].code == "master-bus-multiple" and diagnostics[2].code == "master-bus-name", "All master candidates diagnosed")
	for row in [["buses", "Bus", "duplicate-bus-name"], ["vcas", "Vca", "duplicate-vca-name"], ["globalParameters", "ParameterDefinition", "duplicate-parameter-name"], ["audioTables", "AudioTable", "duplicate-audio-table-name"], ["events", "AudioEvent", "duplicate-event-path"]]:
		p = _minimal()
		var values = {"name":"Same", "path":"Same", "parent":p.mixer.buses[0].id, "bus":p.mixer.buses[0].id}
		var list: Array = p.mixer[row[0]] if row[0] in ["buses", "vcas"] else p[row[0]]
		list.append_array([M.create(row[1], values), M.create(row[1], values)])
		diagnostics = Validator.validate(p)
		test.check(diagnostics.size() == 2 and diagnostics[0].code == row[2] and diagnostics[1].code == row[2], "Report both duplicate objects: " + row[2])
	p = _minimal()
	var effect = M.create("BusEffect", {"typeName":"  "})
	p.mixer.buses[0].effects.append(effect)
	_only(test, p, "effect-type-name-empty", effect.id)
	for row in [[{"kind":"Labeled"}, "labeled-parameter-no-labels"], [{"default":2}, "parameter-range-invalid"], [{"minimum":5, "maximum":1, "default":3}, "parameter-range-invalid"]]:
		p = _minimal()
		var param = M.create("ParameterDefinition", row[0])
		p.globalParameters.append(param)
		_only(test, p, row[1], param.id)
	p = _minimal()
	p.events[0].bus = M.new_id()
	_only(test, p, "event-bus-missing", p.events[0].id)
	for mode in ["placement", "condition", "automation", "marker"]:
		p = _minimal()
		var ev: Dictionary = p.events[0]
		var clip: Dictionary = ev.tracks[0].instruments[0]
		var subject: String = clip.id
		match mode:
			"placement": clip.placement.parameter = M.new_id()
			"condition": clip.conditions.append(M.create("TriggerCondition"))
			"automation":
				var lane = M.create("AutomationLane")
				ev.automation.append(lane)
				subject = lane.id
			"marker":
				var marker = M.create("SustainPoint", {"conditions":[M.create("TriggerCondition")]})
				ev.markers.append(marker)
				subject = marker.id
		_only(test, p, "parameter-ref-missing", subject)
	for row in [["Event", "event-instrument-target-missing"], ["Snapshot", "snapshot-target-missing"], ["Command", "command-target-missing"], ["Multi", "multi-instrument-empty"], ["Scatterer", "multi-instrument-empty"], ["Clip", "empty-asset-ref"]]:
		p = _minimal()
		var item = M.create("Instrument", {"$type":row[0]})
		p.events[0].tracks[0].instruments.append(item)
		_only(test, p, row[1], item.id, "Warning" if row[0] == "Clip" else "Error")
	for kind in ["Loop", "Magnet", "TransitionRegion"]:
		p = _minimal()
		var dest = M.create("Destination")
		var marker = M.create(kind, {"destination":dest.id})
		p.events[0].markers.append_array([dest, marker])
		_only(test, p, "region-length-invalid", marker.id)
	p = _minimal()
	var transition = M.create("Transition")
	p.events[0].markers.append(transition)
	_only(test, p, "transition-destination-invalid", transition.id)
	p = _minimal()
	p.events[0].tempo.append(M.create("TempoMarker", {"bpm":0}))
	_only(test, p, "tempo-bpm-invalid", p.events[0].id)
	p = _minimal()
	var self_reference = M.create("Event", {"event":p.events[0].id})
	p.events[0].tracks[0].instruments.append(self_reference)
	_only(test, p, "event-instrument-cycle", self_reference.id)
	_extended_validation(test)

func _extended_validation(test: Object) -> void:
	var p = _minimal()
	var ev: Dictionary = p.events[0]
	var param = M.create("ParameterDefinition", {"name":"Rpm", "maximum":4000, "default":1000})
	ev.localParameters.append(param)
	var mod = M.create("Autopitch", {"target":M.property_ref("EventPitch", ev.id), "parameter":param.id, "referenceValue":1000, "amount":1})
	ev.modulators.append(mod)
	test.check(Validator.validate(p).is_empty(), "Valid local autopitch")
	for changes in [{"referenceValue":5000}, {"pitchAtMinimum":0}, {"pitchAtMaximum":-1}, {"pitchAtMinimum":NAN}, {"target":M.property_ref("EventVolumeDb", ev.id)}]:
		var copy = p.duplicate(true)
		copy.events[0].modulators[0].merge(changes, true)
		_only(test, copy, "autopitch-invalid", mod.id)
	var copy = p.duplicate(true)
	copy.events[0].modulators[0].parameter = M.new_id()
	_only(test, copy, "parameter-ref-missing", mod.id)
	for kind in ["Lfo", "Random", "AhdsrModulator", "Autopitch"]:
		p = _minimal()
		var mixer_mod = M.create(kind, {"target":M.property_ref("BusVolumeDb", p.mixer.buses[0].id)})
		p.mixerModulators.append(mixer_mod)
		if kind in ["Lfo", "Random"]: test.check(Validator.validate(p).is_empty(), "Valid mixer " + kind)
		else: _only(test, p, "mixer-modulator-invalid", mixer_mod.id)
	p = _minimal()
	var table = M.create("AudioTable", {"entries":{"empty":{}, "blank":{"en":""}}})
	p.audioTables.append(table)
	var d = Validator.validate(p)
	test.check(d.size() == 2 and d[0].code == "audio-table-empty-locales" and d[1].code == "empty-asset-ref" and d[1].severity == "Warning", "Table locale and asset diagnostics")
	p = _minimal()
	p.platformOverrides.append(M.create("PlatformOverride", {"values":[{"target":M.property_ref("BusVolumeDb", M.new_id())}]}))
	d = Validator.validate(p)
	test.check(d.size() == 2 and d[0].code == "platform-override-empty" and d[1].code == "platform-override-target-missing", "Platform override diagnostics")
	p = _minimal()
	var lane = M.create("AutomationLane", {"target":M.property_ref("EventVolumeDb", p.events[0].id), "curve":{"points":[[1, 0], [0, 0]]}})
	p.mixerAutomation.append(lane)
	d = Validator.validate(p)
	test.check(d.size() == 3 and d[0].code == "mixer-automation-invalid" and d[1].code == "mixer-automation-invalid" and d[2].code == "curve-unsorted", "Mixer automation checks target, global scope and sorted curve")
	p = _minimal()
	var dest = M.create("Destination")
	var marker = M.create("Transition", {"destination":dest.id, "transitionInstruments":[M.create("Silence")]})
	p.events[0].markers.append_array([dest, marker])
	_only(test, p, "transition-instrument-invalid", marker.id)
	marker.transitionLength = 1
	marker.transitionInstruments[0].placement.start = 1
	_only(test, p, "transition-instrument-invalid", marker.transitionInstruments[0].id)
	marker.transitionInstruments[0].placement.start = 0
	test.check(Validator.validate(p).is_empty(), "Valid transition timeline")
	p = _minimal()
	var snap = M.create("Snapshot", {"values":[{"target":M.property_ref("EffectParameter", p.mixer.buses[0].id, 0, "x")} ]})
	p.snapshots.append(snap)
	_only(test, p, "snapshot-target-missing", snap.id)

func _scope_and_graph_validation(test: Object) -> void:
	var p = _minimal()
	var root: Dictionary = p.events[0]
	var left = M.create("AudioEvent", {"path":"Left", "bus":root.bus, "tracks":[M.create("Track")]})
	var right = M.create("AudioEvent", {"path":"Right", "bus":root.bus, "tracks":[M.create("Track")]})
	var leaf = M.create("AudioEvent", {"path":"Leaf", "bus":root.bus, "tracks":[M.create("Track")]})
	p.events.append_array([left, right, leaf])
	root.tracks[0].instruments.append_array([M.create("Event", {"event":left.id}), M.create("Event", {"event":right.id})])
	left.tracks[0].instruments.append(M.create("Event", {"event":leaf.id}))
	right.tracks[0].instruments.append(M.create("Event", {"event":leaf.id}))
	test.check(Validator.validate(p).is_empty(), "Diamond nested event graph is not a cycle")
	var back = M.create("Event", {"event":root.id})
	leaf.tracks[0].instruments.append(back)
	_only(test, p, "event-instrument-cycle", back.id)
	leaf.tracks[0].instruments.clear()
	var foreign = M.create("ParameterDefinition", {"name":"Foreign"})
	left.localParameters.append(foreign)
	root.tracks[0].instruments[0].placement.parameter = foreign.id
	_only(test, p, "parameter-ref-missing", root.tracks[0].instruments[0].id)
	root.tracks[0].instruments[0].placement.parameter = M.NONE
	var command = M.create("Command", {"action":"SetParameter", "targetParameter":foreign.id})
	root.tracks[0].instruments.append(command)
	_only(test, p, "command-target-missing", command.id)
	command.targetEvent = left.id
	test.check(Validator.validate(p).is_empty(), "Command resolves target event locals")
	command.targetEvent = M.NONE
	p.globalParameters.append(foreign)
	test.check(Validator.validate(p).is_empty(), "Global parameters resolve for command and event scopes")
	var destination = M.create("Destination")
	left.markers.append(destination)
	var transition = M.create("Transition", {"destination":destination.id})
	root.markers.append(transition)
	_only(test, p, "transition-destination-invalid", transition.id)
	root.markers.append(destination)
	transition.transitionLength = 1
	var sheet = M.create("Silence", {"placement":{"parameter":foreign.id}})
	transition.transitionInstruments.append(sheet)
	_only(test, p, "transition-instrument-invalid", sheet.id)
	transition.transitionInstruments.clear()
	transition.transitionLength = -1
	_only(test, p, "transition-instrument-invalid", transition.id)
	p = _minimal()
	for target in [M.property_ref("BusVolumeDb", M.new_id()), M.property_ref("EffectParameter", p.mixer.buses[0].id, 3, "x"), M.property_ref("EventVolumeDb", p.events[0].id)]:
		var invalid = M.create("Lfo", {"target":target})
		p.mixerModulators = [invalid]
		_only(test, p, "mixer-modulator-invalid", invalid.id)
