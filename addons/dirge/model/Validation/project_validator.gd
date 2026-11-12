@tool
extends RefCounted
const Model = preload("../Project/project_model.gd")

static func validate(project: Dictionary) -> Array[Dictionary]:
	var context = Context.new(Model.hydrate(project))
	context.validate_all()
	return context.diagnostics

class Context:
	extends RefCounted
	var project: Dictionary
	var diagnostics: Array[Dictionary] = []
	var buses: Dictionary = {}
	var vcas: Dictionary = {}
	var snapshots: Dictionary = {}
	var events: Dictionary = {}
	var globals: Dictionary = {}
	func _init(p: Dictionary) -> void:
		project = p
		_index(p.mixer.buses, buses)
		_index(p.mixer.vcas, vcas)
		_index(p.snapshots, snapshots)
		_index(p.events, events)
		_index(p.globalParameters, globals)
	func _index(items: Array, into: Dictionary) -> void:
		for item in items:
			if not into.has(item.id): into[item.id] = item
	func error(code: String, subject: String, message: String) -> void:
		diagnostics.append({"severity":"Error", "code":code, "message":message, "subject":subject})
	func warning(code: String, subject: String, message: String) -> void:
		diagnostics.append({"severity":"Warning", "code":code, "message":message, "subject":subject})
	func duplicates(items: Array, key: String, code: String, what: String) -> void:
		var counts = {}
		for item in items: counts[item[key]] = counts.get(item[key], 0) + 1
		for item in items:
			if counts[item[key]] > 1: error(code, item.id, 'Duplicate %s "%s".' % [what, item[key]])
	func mixer_target_exists(target: Dictionary) -> bool:
		match target.get("kind", "BusVolumeDb"):
			"BusVolumeDb", "BusPitch": return buses.has(target.get("owner", Model.NONE))
			"VcaVolumeDb": return vcas.has(target.get("owner", Model.NONE))
			"EffectParameter":
				var bus: Dictionary = buses.get(target.get("owner", Model.NONE), {})
				var index: int = target.get("effectIndex", -1)
				return not bus.is_empty() and index >= 0 and index < bus.effects.size()
		return false
	func find_parameter(scope: Dictionary, id: String) -> Dictionary:
		for p in scope.get("localParameters", []):
			if p.id == id: return p
		return globals.get(id, {})
	func require_parameter(scope: Dictionary, id: String, subject: String, where: String) -> void:
		if find_parameter(scope, id).is_empty(): error("parameter-ref-missing", subject, '%s references a parameter that is neither local to "%s" nor global.' % [where, scope.path])
	func validate_all() -> void:
		validate_mixer()
		duplicates(project.globalParameters, "name", "duplicate-parameter-name", "global parameter name")
		for p in project.globalParameters: validate_parameter(p)
		for snapshot in project.snapshots:
			for value in snapshot.values:
				if not mixer_target_exists(value.target): error("snapshot-target-missing", snapshot.id, 'Snapshot "%s" targets a %s property whose owner does not exist.' % [snapshot.name, value.target.kind])
		for platform in project.platformOverrides:
			if platform.platform.strip_edges() == "": error("platform-override-empty", Model.NONE, "A platform override has no platform name.")
			for value in platform.values:
				if not mixer_target_exists(value.target): error("platform-override-target-missing", Model.NONE, 'Platform override "%s" targets a %s property whose owner does not exist.' % [platform.platform, value.target.kind])
		for lane in project.mixerAutomation:
			if not mixer_target_exists(lane.target): error("mixer-automation-invalid", lane.id, "Mixer automation lane must target an existing mixer property.")
			if not globals.has(lane.parameter): error("mixer-automation-invalid", lane.id, "Mixer automation lane must be driven by a global parameter.")
			validate_curve(lane.curve, lane.id, "Mixer automation lane")
		for m in project.mixerModulators:
			if not m["$type"] in ["Lfo", "Random"]: error("mixer-modulator-invalid", m.id, "Only LFO and Random modulators can drive mixer properties.")
			if not mixer_target_exists(m.target): error("mixer-modulator-invalid", m.id, "Mixer modulator must target an existing mixer property.")
		duplicates(project.audioTables, "name", "duplicate-audio-table-name", "audio table name")
		for table in project.audioTables:
			for key in table.entries:
				var locales: Dictionary = table.entries[key]
				if locales.is_empty(): error("audio-table-empty-locales", table.id, 'Audio table "%s" key "%s" has no locales.' % [table.name, key])
				for locale in locales:
					if _empty_asset(locales[locale]): warning("empty-asset-ref", table.id, 'Audio table "%s" key "%s" locale "%s" has no asset.' % [table.name, key, locale])
		duplicates(project.events, "path", "duplicate-event-path", "event path")
		for ev in project.events: validate_event(ev)
		var state = {}
		var reported = {}
		for ev in project.events: visit_event(ev, state, reported)
	func validate_mixer() -> void:
		var masters = []
		for bus in project.mixer.buses:
			if Model.is_none(bus.parent): masters.append(bus)
		if masters.is_empty(): error("master-bus-missing", Model.NONE, 'The mixer has no master bus (a bus with no parent named "Master").')
		elif masters.size() > 1:
			for bus in masters: error("master-bus-multiple", bus.id, 'Bus "%s" has no parent but only one master bus is allowed.' % bus.name)
		for bus in masters:
			if bus.name != "Master": error("master-bus-name", bus.id, 'Master bus must be named "Master", found "%s".' % bus.name)
		var reported = {}
		for bus in project.mixer.buses:
			if not Model.is_none(bus.parent) and not buses.has(bus.parent): error("bus-parent-missing", bus.id, 'Bus "%s" routes to a parent bus that does not exist.' % bus.name)
			if not reported.has(bus.id):
				var chain = {}
				var current: Dictionary = bus
				while not chain.has(current.id):
					chain[current.id] = true
					if Model.is_none(current.parent) or not buses.has(current.parent): break
					var parent: Dictionary = buses[current.parent]
					if chain.has(parent.id):
						reported.merge(chain)
						error("bus-cycle", bus.id, 'Bus "%s" is part of a routing cycle.' % bus.name)
						break
					current = parent
			for vca in bus.vcas:
				if not vcas.has(vca): error("vca-ref-missing", bus.id, 'Bus "%s" references a VCA that does not exist.' % bus.name)
			if bus.faderIndex < 0: error("bus-fader-index-negative", bus.id, 'Bus "%s" has a negative FaderIndex; use 0 (all effects post-fader) or larger.' % bus.name)
			for effect in bus.effects:
				if effect.typeName == null or effect.typeName.strip_edges() == "": error("effect-type-name-empty", effect.id, 'An effect on bus "%s" has no TypeName.' % bus.name)
		duplicates(project.mixer.buses, "name", "duplicate-bus-name", "bus name")
		duplicates(project.mixer.vcas, "name", "duplicate-vca-name", "VCA name")
	func validate_parameter(p: Dictionary) -> void:
		if p.kind == "Labeled" and p.labels.is_empty(): error("labeled-parameter-no-labels", p.id, 'Labeled parameter "%s" has no labels.' % p.name)
		if not (p.minimum <= p.default and p.default <= p.maximum): error("parameter-range-invalid", p.id, 'Parameter "%s" must satisfy Minimum ≤ Default ≤ Maximum (%s ≤ %s ≤ %s).' % [p.name, p.minimum, p.default, p.maximum])
	func validate_curve(curve: Dictionary, subject: String, where: String) -> void:
		for i in range(1, curve.points.size()):
			if curve.points[i][0] < curve.points[i - 1][0]:
				error("curve-unsorted", subject, "%s has curve points out of X order (point %d)." % [where, i])
				break
	func validate_modulators(ev: Dictionary, modulators: Array, pitch_kind: String, where: String) -> void:
		for m in modulators:
			if m["$type"] != "Autopitch": continue
			if m.target.kind != pitch_kind: error("autopitch-invalid", m.id, "%s: autopitch must target %s, not %s." % [where, pitch_kind, m.target.kind])
			var p = find_parameter(ev, m.parameter)
			if p.is_empty(): error("parameter-ref-missing", m.id, "%s (autopitch) references a parameter that is neither local nor global." % where)
			elif not (p.minimum <= m.referenceValue and m.referenceValue <= p.maximum): error("autopitch-invalid", m.id, "%s: autopitch reference %s lies outside parameter range [%s, %s]." % [where, m.referenceValue, p.minimum, p.maximum])
			if not (m.pitchAtMinimum > 0) or not (m.pitchAtMaximum > 0): error("autopitch-invalid", m.id, "%s: autopitch pitch ratios must be > 0." % where)
	func validate_event(ev: Dictionary) -> void:
		if not buses.has(ev.bus): error("event-bus-missing", ev.id, 'Event "%s" routes to a bus that does not exist.' % ev.path)
		duplicates(ev.localParameters, "name", "duplicate-parameter-name", 'local parameter name in "%s"' % ev.path)
		for p in ev.localParameters: validate_parameter(p)
		for lane in ev.automation:
			require_parameter(ev, lane.parameter, lane.id, "Automation lane")
			validate_curve(lane.curve, lane.id, 'Automation lane in "%s"' % ev.path)
		validate_modulators(ev, ev.modulators, "EventPitch", 'Event modulator in "%s"' % ev.path)
		for marker in ev.markers: validate_marker(ev, marker)
		for tempo in ev.tempo:
			if not tempo.bpm > 0: error("tempo-bpm-invalid", ev.id, 'Event "%s" has a tempo marker at %ss with Bpm %s; it must be > 0.' % [ev.path, tempo.position, tempo.bpm])
		for track in ev.tracks:
			for instrument in track.instruments: validate_instrument(ev, instrument)
	func validate_marker(ev: Dictionary, marker: Dictionary) -> void:
		var where = 'Marker "%s" in "%s"' % [marker.name, ev.path]
		for condition in marker.conditions: require_parameter(ev, condition.parameter, marker.id, where + " (condition)")
		var kind: String = marker["$type"]
		if kind in ["Loop", "Magnet", "TransitionRegion"] and not marker.length > 0: error("region-length-invalid", marker.id, "%s: %s region length must be > 0." % [where, kind.to_lower()])
		if kind in ["Transition", "TransitionRegion"]:
			var found = false
			for candidate in ev.markers:
				if candidate.id == marker.destination and candidate["$type"] == "Destination": found = true; break
			if not found: error("transition-destination-invalid", marker.id, "%s: destination must be a destination marker in the same event." % where)
			var length: float = marker.transitionLength
			if marker.transitionInstruments.is_empty():
				if length < 0: error("transition-instrument-invalid", marker.id, "%s: transition length must be ≥ 0." % where)
			else:
				if not length > 0: error("transition-instrument-invalid", marker.id, "%s has transition instruments but no transition length." % where)
				for instrument in marker.transitionInstruments:
					if not Model.is_none(instrument.placement.parameter): error("transition-instrument-invalid", instrument.id, 'Transition instrument "%s" must be placed on the transition timeline, not a parameter sheet.' % instrument.name)
					elif length > 0 and (instrument.placement.start < 0 or instrument.placement.start >= length): error("transition-instrument-invalid", instrument.id, 'Transition instrument "%s" starts outside the transition timeline [0, %s).' % [instrument.name, length])
					validate_instrument(ev, instrument)
	func _empty_asset(asset: Variant) -> bool:
		return asset == null or asset == ""
	func validate_instrument(ev: Dictionary, instrument: Dictionary) -> void:
		var where = 'Instrument "%s" in "%s"' % [instrument.name, ev.path]
		if not Model.is_none(instrument.placement.parameter): require_parameter(ev, instrument.placement.parameter, instrument.id, where + " (parameter sheet)")
		for condition in instrument.conditions: require_parameter(ev, condition.parameter, instrument.id, where + " (condition)")
		validate_modulators(ev, instrument.modulators, "InstrumentPitch", where)
		match instrument["$type"]:
			"Clip":
				if _empty_asset(instrument.asset): warning("empty-asset-ref", instrument.id, where + " has no asset.")
			"Multi", "Scatterer":
				if instrument.entries.is_empty(): error("multi-instrument-empty", instrument.id, where + " has no playlist entries.")
				for i in instrument.entries.size():
					if _empty_asset(instrument.entries[i].asset): warning("empty-asset-ref", instrument.id, "%s entry %d has no asset." % [where, i])
			"Event":
				if not events.has(instrument.event): error("event-instrument-target-missing", instrument.id, where + " plays an event that does not exist.")
			"Snapshot":
				if not snapshots.has(instrument.snapshot): error("snapshot-target-missing", instrument.id, where + " starts a snapshot that does not exist.")
			"Command":
				if instrument.action in ["StartEvent", "StopEvent"]:
					if not events.has(instrument.targetEvent): error("command-target-missing", instrument.id, where + " targets an event that does not exist.")
				elif instrument.action == "SetParameter":
					var scope: Dictionary = ev
					if not Model.is_none(instrument.targetEvent):
						scope = events.get(instrument.targetEvent, {})
						if scope.is_empty(): error("command-target-missing", instrument.id, where + " targets an event that does not exist.")
					if find_parameter(scope, instrument.targetParameter).is_empty(): error("command-target-missing", instrument.id, where + " sets a parameter that does not exist in its target scope.")
	func visit_event(ev: Dictionary, state: Dictionary, reported: Dictionary) -> void:
		if state.get(ev.id, 0) != 0: return
		state[ev.id] = 1
		for track in ev.tracks:
			for instrument in track.instruments:
				if instrument["$type"] != "Event" or not events.has(instrument.event): continue
				var target: Dictionary = events[instrument.event]
				if state.get(target.id, 0) == 1:
					if not reported.has(instrument.id):
						reported[instrument.id] = true
						error("event-instrument-cycle", instrument.id, 'Instrument "%s" in "%s" nests "%s", which (transitively) nests "%s" again.' % [instrument.name, ev.path, target.path, ev.path])
					continue
				visit_event(target, state, reported)
		state[ev.id] = 2
