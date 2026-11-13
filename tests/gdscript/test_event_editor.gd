extends RefCounted

const Model = preload("res://addons/dirge/model/Project/project_model.gd")
const Document = preload("res://addons/dirge/tool/Core/project_document.gd")
const Editor = preload("res://addons/dirge/tool/Events/event_editor_pane.gd")
const Edits = preload("res://addons/dirge/tool/Events/event_edits.gd")
const CurveEditor = preload("res://addons/dirge/tool/Events/curve_canvas.gd")

func run(test: Object) -> void:
	var document = Document.new()
	var project = document.project.duplicate(true)
	var first = Model.create("AudioEvent", {"path": "Editor/First", "bus": project.mixer.buses[0].id})
	var second = Model.create("AudioEvent", {"path": "Editor/Second", "bus": project.mixer.buses[0].id})
	var parameter = Model.create("ParameterDefinition", {"name": "Intensity", "minimum": -2.0, "maximum": 2.0, "default": 0.0})
	var global = Model.create("ParameterDefinition", {"name": "Weather", "kind": "Labeled", "labels": ["Dry", "Rain", "Storm"], "maximum": 2.0})
	first.localParameters.append(parameter)
	project.globalParameters.append(global)
	project.events.append(first)
	project.events.append(second)
	project.assetLengths["res://fixture.ogg"] = 2.5
	document.replace(project)
	var pane = Editor.new()
	pane.size = Vector2(1280, 900)
	var tree = Engine.get_main_loop() as SceneTree
	tree.root.add_child(pane)
	pane.setup(document)
	pane.set_event(first.id)
	await tree.process_frame
	var state = pane.state
	var edits = pane.edits
	test.check(pane.timeline.format_unit(0.125) == "0.125 s" and pane.timeline.format_unit(65.25) == "1:05.25", "Timeline labels preserve fractional seconds and minute precision")
	var track = edits.add_track("Layer A")
	var other = edits.add_track("Layer B")
	var clip = Edits.create_instrument("Clip", Model.NONE, 1.0, 0.0)
	clip.asset = "res://fixture.ogg"
	edits.add_instrument(track, clip)
	var multi = Edits.create_instrument("Multi", Model.NONE, 4.0, 1.0)
	edits.add_instrument(other, multi)
	test.check(is_equal_approx(pane.lengths.natural_length(clip), 2.5), "Natural clip duration uses project asset catalog")
	test.check(is_equal_approx(pane.lengths.event_length(state.event), 5.0), "Timeline extent includes all tracks")
	for kind in Edits.INSTRUMENT_KINDS:
		var instrument = Edits.create_instrument(kind, Model.NONE, 0.0, 1.0)
		test.check(instrument.get("$type") == kind and instrument.has("conditions") and instrument.has("modulators") and instrument.pitch == 1.0, "Instrument factory preserves " + kind + " JSON/defaults")
	for kind in Edits.MARKER_KINDS:
		var marker = Edits.create_marker(kind, 2.0, 3.0)
		test.check(marker.get("$type") == kind and marker.position == 2.0 and marker.probability == 1.0, "Marker factory preserves " + kind)
		if Edits.is_region(marker): test.check(marker.length == 3.0, "Region has authored length")
	state.select_instrument(clip.id)
	state.select_instrument(multi.id, true)
	test.check(state.selected_instruments.size() == 2 and state.primary_instrument.is_empty(), "Additive selection enters multi-edit mode")
	edits.set_all("volumeDb", -9.0)
	test.check(state.find_instrument(clip.id).volumeDb == -9.0 and state.find_instrument(multi.id).volumeDb == -9.0, "Multi-edit applies one value to every selected instrument")
	document.undo()
	test.check(state.find_instrument(clip.id).volumeDb == 0.0 and state.find_instrument(multi.id).volumeDb == 0.0, "Multi-edit undo restores every instrument")
	document.redo()
	test.check(state.find_instrument(clip.id).volumeDb == -9.0, "Redo restores multi-edit")
	# Undo replaces dictionaries; every following operation resolves through state IDs.
	test.check(pane.copy_selection(), "Copy captures multitrack selection")
	pane.set_event(second.id)
	state.cursor = 10.0
	test.check(pane.paste(), "Clipboard survives event switches")
	test.check(state.event.tracks.size() == 1 and state.event.tracks[0].instruments.size() == 2, "Paste creates destination track and clamps source track offsets")
	var copies: Array = state.event.tracks[0].instruments
	test.check(copies[0].id != clip.id and copies[1].id != multi.id, "Paste regenerates instrument identity")
	test.check(copies[0].placement.start == 10.0 and copies[1].placement.start == 13.0, "Paste preserves relative placements at edit cursor")
	document.undo()
	test.check(state.event.tracks.is_empty(), "One paste undo removes pasted instruments and implicitly created track")
	document.redo()
	test.check(state.event.tracks[0].instruments.size() == 2, "Paste redo restores authored contents")
	pane.set_event(first.id)
	state.select_instrument(clip.id)
	pane.timeline.size = Vector2(900, 400)
	pane.timeline.zoom = 120.0
	pane.timeline.ensure_layout()
	# Real timeline input path: preview is non-mutating; release commits one undo entry.
	var block: Dictionary = {}
	for candidate in pane.timeline.blocks:
		if candidate.instrument.id == clip.id: block = candidate
	var press = InputEventMouseButton.new()
	press.button_index = MOUSE_BUTTON_LEFT
	press.pressed = true
	press.position = block.rect.position + Vector2(20, 20)
	pane.timeline._gui_input(press)
	var motion = InputEventMouseMotion.new()
	motion.position = press.position + Vector2(120, 0)
	pane.timeline._gui_input(motion)
	test.check(state.find_instrument(clip.id).placement.start == 1.0, "Pointer movement previews without dirtying authored placement")
	var release = InputEventMouseButton.new()
	release.button_index = MOUSE_BUTTON_LEFT
	release.position = motion.position
	pane.timeline._gui_input(release)
	test.check(state.find_instrument(clip.id).placement.start == 2.0, "Timeline drag commits snapped placement")
	document.undo()
	test.check(state.find_instrument(clip.id).placement.start == 1.0, "One undo restores entire drag")
	edits.add_tempo(0.0)
	test.check(is_equal_approx(pane.timeline.snap(0.74), 0.5), "Timeline snaps to tempo beat grid")
	edits.add_tempo(3.0)
	var tempo = state.event.tempo.back()
	edits.set_tempo_position(tempo, 0.25)
	test.check(state.event.tempo[1].position == 0.25, "Tempo edits preserve sorted map")
	state.set_sheet(parameter.id)
	test.check(pane.timeline.format_unit(-1.25) == "-1.25", "Continuous sheet labels preserve signed fractional units")
	test.check(is_equal_approx(pane.timeline.snap(-0.74), -0.7), "Continuous sheets snap in parameter units")
	test.check(pane.timeline.clamp_unit(-20) == -2.0, "Parameter sheet permits negative authored minimum")
	state.set_sheet(global.id)
	test.check(pane.timeline.format_unit(1.0) == "Rain", "Labeled sheet labels retain authored names")
	test.check(pane.timeline.snap(0.74) == 1.0, "Labeled sheets use integer snapping")
	state.set_sheet(Model.NONE)
	pane.timeline.ensure_layout()
	var multiblock: Dictionary = {}
	for candidate in pane.timeline.blocks:
		if candidate.instrument.id == multi.id: multiblock = candidate
	pane.timeline._drop_data(multiblock.rect.get_center(), {"type": "asset", "path": "res://fixture.ogg"})
	test.check(state.find_instrument(multi.id).entries.size() == 1 and state.find_instrument(multi.id).entries[0].weight == 1.0, "Dropping asset on Multi adds weighted entry instead of another clip")
	test.check(not pane.timeline._can_drop_data(Vector2.ZERO, {"type": "event", "id": first.id}), "Timeline rejects direct self-nesting")
	var destination = Edits.create_marker("Destination", 6.0)
	edits.add_marker(destination)
	var transition = Edits.create_marker("TransitionRegion", 2.0, 1.0)
	transition.destination = destination.id
	transition.transitionLength = 0.5
	transition.transitionInstruments.append(Edits.create_instrument("Clip", Model.NONE, 0, 0))
	edits.add_marker(transition)
	state.select_marker(transition.id)
	pane.inspector.render()
	pane.inspector.host.set_value("#event-inspector-form-content-7", "0.75")
	pane.inspector.host.emit_signal("dom_event", "change", "event-inspector-form-content-7")
	test.check(state.find_marker(transition.id).transitionLength == 0.75, "Transition timeline length is editable through the real inspector DOM")
	edits.set_marker_positions([{"id": transition.id, "position": 3.0, "length": 2.0}])
	test.check(state.find_marker(transition.id).position == 3.0 and state.find_marker(transition.id).length == 2.0, "Transition regions move and resize transactionally")
	document.undo()
	test.check(state.find_marker(transition.id).position == 2.0 and state.find_marker(transition.id).length == 1.0, "Undo restores marker position and region length together")
	state.select_instrument(clip.id)
	var lane = pane.automation.add_lane(Model.property_ref("InstrumentVolumeDb", clip.id))
	test.check(lane.parameter == parameter.id and lane.curve.points[0] == [-2.0, 0.0, "Linear"], "Automation seeds parameter range and neutral property value")
	pane.curve.add_point(Vector2(0, -12))
	test.check(state.find_lane(lane.id).curve.points.size() == 3 and state.find_lane(lane.id).curve.points[1][0] == 0.0, "Curve insertion sorts real serialized point arrays")
	pane.curve.set_shape(0, "Smooth")
	test.check(state.find_lane(lane.id).curve.points[0][2] == "Smooth", "Curve segment shape is authored")
	test.check(is_equal_approx(CurveEditor.evaluate([[0, 0, "Smooth"], [1, 8, "Linear"]], 0.25), 1.25), "Smooth curve display follows smoothstep semantics")
	test.check(CurveEditor.evaluate([[0, 3, "Stepped"], [1, 8, "Linear"]], 0.75) == 3.0, "Stepped curves hold preceding value")
	pane.curve.selected = 1
	pane.curve.delete_selected_point()
	test.check(state.find_lane(lane.id).curve.points.size() == 2, "Curve delete removes selected point")
	document.undo()
	test.check(state.find_lane(lane.id).curve.points.size() == 3, "Curve deletion undo restores point")
	var current_clip = state.find_instrument(clip.id)
	for kind in ["Random", "Ahdsr", "Lfo", "Autopitch"]:
		var modulator = pane.automation.add_modulator(current_clip, true, kind)
		test.check(modulator.get("$type") == kind and modulator.target.owner == clip.id, "Modulator authored with owning instrument: " + kind)
	var cloned = Edits.clone_item(current_clip)
	test.check(cloned.id != current_clip.id and cloned.modulators[0].id != current_clip.modulators[0].id and cloned.modulators[0].target.owner == cloned.id, "Instrument clone deep-copies and remaps modulator owner identities")
	# Native DOM form value commits, invalid input, and retained focused node behavior.
	state.select_instrument(clip.id)
	pane.inspector.render()
	var volume = "#event-inspector-form-content-8"
	pane.inspector.host.set_value(volume, "-15.5")
	pane.inspector.host.emit_signal("dom_event", "change", volume.trim_prefix("#"))
	test.check(state.find_instrument(clip.id).volumeDb == -15.5, "DOM inspector number change authors instrument value")
	pane.inspector.render()
	var before_invalid = document.project.duplicate(true)
	var undo_count = document._done.size()
	pane.inspector.host.set_value(volume, "not a number")
	pane.inspector.host.emit_signal("dom_event", "change", volume.trim_prefix("#"))
	test.check(state.find_instrument(clip.id).volumeDb == -15.5 and pane.inspector.host.get_value(volume) == "-15.5", "Invalid numeric edit restores control without mutating project")
	test.check(document.project == before_invalid and document._done.size() == undo_count, "Invalid numeric edit leaves the entire project and undo history unchanged")
	# Newly inserted inspector inputs need published native layout before they can focus.
	await tree.process_frame
	await tree.process_frame
	pane.inspector.host.set_attribute(volume, "data-retained", "volume")
	pane.inspector.host.focus_element(volume)
	test.check(pane.inspector.host.get_active_element_id() == volume.trim_prefix("#"), "Inspector number input receives native focus before retained render")
	pane.inspector.render()
	test.check(pane.inspector.host.get_active_element_id() == volume.trim_prefix("#"), "Unchanged inspector keeps focused DOM input during render")
	test.check(pane.inspector.host.get_attribute(volume, "data-retained") == "volume", "Unchanged inspector retains the existing input node")
	pane.inspector.host.set_value(volume, "-15.")
	pane.inspector.render()
	test.check(pane.inspector.host.get_value(volume) == "-15." and pane.inspector.host.get_active_element_id() == volume.trim_prefix("#"), "Unchanged inspector preserves an in-progress focused edit")
	pane.inspector.host.set_value(volume, "-15.5")
	pane.select_subject(9, global.id)
	test.check(state.sheet == global.id and pane.header.show_parameters, "Browser global parameter selection opens sheet and definition controls")
	pane.set_event(first.id)
	state.select_instrument(clip.id)
	state.select_marker(destination.id, true)
	test.check(pane.delete_selection(), "Mixed selection deletes instruments and markers")
	test.check(state.find_instrument(clip.id).is_empty() and state.find_marker(destination.id).is_empty(), "Deletion removes both selected kinds")
	document.undo()
	test.check(not state.find_instrument(clip.id).is_empty() and not state.find_marker(destination.id).is_empty(), "One undo restores mixed deletion")
	pane.queue_free()
	await tree.process_frame
