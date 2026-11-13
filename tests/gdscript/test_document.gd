extends RefCounted
const Document = preload("res://addons/dirge/tool/Core/project_document.gd")
const Model = preload("res://addons/dirge/model/Project/project_model.gd")
const Selection = preload("res://addons/dirge/tool/Core/selection.gd")
const Commands = preload("res://addons/dirge/tool/Core/tool_commands.gd")
const TreeModel = preload("res://addons/dirge/tool/Browser/event_tree.gd")
const Cloner = preload("res://addons/dirge/tool/Browser/event_cloner.gd")
const Browser = preload("res://addons/dirge/tool/Browser/browser_pane.gd")
const Scanner = preload("res://addons/dirge/tool/Browser/asset_scanner.gd")
const Preview = preload("res://addons/dirge/tool/Transport/preview_math.gd")
const ThemeColors = preload("res://addons/dirge/tool/Core/tool_theme.gd")
const Subjects = preload("res://addons/dirge/tool/Shell/subject_lookup.gd")

func run(test: Object) -> void:
	_document_history(test)
	_continuous_edits(test)
	_persistence(test)
	_selection_commands(test)
	_event_tree(test)
	_clone_references(test)
	_browser_workflows(test)
	_assets_spatial_themes(test)

func _document_history(test: Object) -> void:
	var doc = Document.new()
	test.check(doc.project.name == "Untitled" and doc.project.mixer.buses.size() == 1, "New document has Untitled project and one Master bus")
	test.check(doc.project.mixer.buses[0].name == "Master" and doc.project.mixer.buses[0].id.length() == 32, "Master identity is a serialized Dirge ID")
	test.check(not doc.dirty and doc.path.is_empty() and not doc.can_undo and not doc.can_redo, "New document has no history, dirty flag or path")
	var notifications = [0]
	var scopes: Array = []
	doc.changed.connect(func(): notifications[0] += 1)
	doc.scoped_changed.connect(func(scope, subject): scopes.append([scope, subject]))
	var detached = Model.new_project("Loaded")
	detached.mixer.buses.append(Model.create("Bus", {"name":"Master"}))
	doc.replace(detached, "res://loaded.dirge.json")
	detached.name = "Mutated outside document"
	test.check(doc.project.name == "Loaded", "Replace owns a deep copy, not caller mutable storage")
	test.check(not doc.dirty and notifications[0] == 1 and scopes.back()[0] == Document.ChangeScope.ALL, "Replace emits once with All scope and starts clean")
	doc.transact("Rename project", func(): doc.project.name = "Edited", Document.ChangeScope.EVENTS, "subject")
	test.check(doc.project.name == "Edited" and doc.dirty and doc.can_undo and not doc.can_redo, "Transaction applies immediately and enters undo history")
	test.check(doc.undo_label == "Rename project" and doc.changed_subject == "subject" and doc.changed_scope == Document.ChangeScope.EVENTS, "Transaction exposes scope, subject and undo label")
	test.check(notifications[0] == 2, "Transaction emits exactly once")
	test.check(doc.undo() and doc.project.name == "Loaded" and doc.can_redo and not doc.can_undo, "Undo restores previous project")
	test.check(doc.redo_label == "Rename project" and doc.dirty, "Undo preserves original dirty semantics and redo label")
	test.check(doc.redo() and doc.project.name == "Edited" and doc.can_undo and not doc.can_redo, "Redo restores applied snapshot")
	var edited = doc.project
	doc.undo()
	edited.name = "Stale reference"
	doc.redo()
	test.check(doc.project.name == "Edited", "Undo/redo snapshots cannot be mutated by stale project references")
	doc.undo()
	doc.transact("Branch", func(): doc.project.name = "Branch")
	test.check(not doc.can_redo and not doc.redo(), "New edit clears the abandoned redo branch")
	var count = notifications[0]
	doc.transact("Outer", func():
		doc.project.name = "Outer"
		doc.transact("Inner", func(): doc.project.assetLengths["res://clip.wav"] = 1.25))
	test.check(notifications[0] == count + 1 and doc.undo_label == "Outer", "Nested edits coalesce into the outer transaction with one notification")
	doc.undo()
	test.check(doc.project.name == "Branch" and not doc.project.assetLengths.has("res://clip.wav"), "Nested transaction reverts all nested changes atomically")
	doc.redo()
	test.check(doc.project.assetLengths["res://clip.wav"] == 1.25, "Nested deep dictionary edit redoes")
	doc.mark_saved("res://saved.dirge.json")
	test.check(not doc.dirty and doc.path == "res://saved.dirge.json" and doc.can_undo, "Saving clears dirty without discarding history")
	doc.undo()
	test.check(doc.dirty, "Undo after save marks document dirty")
	doc.reset()
	test.check(not doc.dirty and not doc.can_undo and not doc.can_redo and doc.path.is_empty(), "New clears both history stacks and saved path")
	count = notifications[0]
	test.check(not doc.undo() and not doc.redo() and notifications[0] == count, "Empty undo/redo are notification-free")

func _continuous_edits(test: Object) -> void:
	var doc = Document.new()
	var bus_id = doc.project.mixer.buses[0].id
	for volume in [-1.0, -2.0, -7.5]:
		doc.transact_continuous("fader", "Fader", func(): doc.project.mixer.buses[0].volumeDb = volume, Document.ChangeScope.MIXER, bus_id)
	test.check(doc.project.mixer.buses[0].volumeDb == -7.5 and doc.undo_label == "Fader", "Continuous drag tracks latest value")
	doc.undo()
	test.check(doc.project.mixer.buses[0].volumeDb == 0.0 and not doc.can_undo, "One undo reverts entire continuous drag to initial value")
	doc.redo()
	test.check(doc.project.mixer.buses[0].volumeDb == -7.5, "Continuous redo restores final drag value")
	doc.end_continuous()
	doc.transact_continuous("fader", "Second drag", func(): doc.project.mixer.buses[0].volumeDb = -12.0)
	doc.undo()
	test.check(doc.project.mixer.buses[0].volumeDb == -7.5 and doc.can_undo, "Explicit drag end separates histories even with same token")
	doc.transact_continuous("new", "New branch", func(): doc.project.mixer.buses[0].volumeDb = -3.0)
	test.check(not doc.can_redo, "Continuous edit after undo invalidates redo")
	doc.transact("Name", func(): doc.project.name = "Separate")
	doc.transact_continuous("new", "Separate drag", func(): doc.project.mixer.buses[0].volumeDb = -4.0)
	doc.undo()
	test.check(doc.project.name == "Separate" and doc.project.mixer.buses[0].volumeDb == -3.0, "Ordinary transactions break continuous merge tokens")

func _persistence(test: Object) -> void:
	var doc = Document.new()
	var event = Model.create("AudioEvent", {"path":"Music/Test", "bus":doc.project.mixer.buses[0].id})
	doc.transact("Add event", func(): doc.project.events.append(event))
	var path = "user://dirge-document-%d.dirge.json" % Time.get_ticks_usec()
	var saved = [0]
	doc.saved.connect(func(): saved[0] += 1)
	test.check(doc.save() == ERR_INVALID_PARAMETER and doc.dirty, "Unsaved document requires a path and remains dirty")
	test.check(doc.save(path) == OK and not doc.dirty and saved[0] == 1, "Save writes JSON and marks clean only on success")
	var reopened = Document.new()
	test.check(reopened.load_file(path) == OK, "Saved file reopens through public document loader")
	test.check(reopened.project == doc.project and reopened.path == path and not reopened.can_undo, "Reopening preserves full serialized data and starts fresh history")
	var original = reopened.project.duplicate(true)
	test.check(reopened.load_text("{broken", "bad") == ERR_PARSE_ERROR and reopened.project == original and reopened.path == path, "Malformed load leaves current project and path untouched")
	test.check(reopened.load_text("[]") == ERR_PARSE_ERROR, "Non-object JSON is rejected without replacing document")
	doc.transact("Rename", func(): doc.project.name = "Save existing path")
	test.check(doc.save() == OK and saved[0] == 2, "Save without explicit target reuses known path")
	DirAccess.remove_absolute(ProjectSettings.globalize_path(path))
	var messages = doc.validate()
	test.check(messages.is_empty(), "Minimal routed event project passes actual project validation")

func _selection_commands(test: Object) -> void:
	var selection = Selection.new()
	selection.select(Selection.Kind.INSTRUMENT, "instrument", "event", 3)
	test.check(selection.id == "instrument" and selection.owner == "event" and selection.index == 3, "Selection retains ownership and effect index")
	selection.select_asset("res://clip.ogg")
	test.check(selection.kind == Selection.Kind.ASSET and selection.asset_path == "res://clip.ogg" and selection.id.is_empty() and selection.owner.is_empty() and selection.index == -1, "Asset selection clears object identity")
	selection.clear()
	test.check(selection.kind == Selection.Kind.NONE and selection.asset_path.is_empty(), "Clearing selection clears asset path")
	var key = InputEventKey.new()
	key.pressed = true
	key.ctrl_pressed = true
	key.keycode = KEY_Z
	test.check(Commands.from_key(key) == Commands.Command.UNDO, "Ctrl+Z maps undo")
	key.shift_pressed = true
	test.check(Commands.from_key(key) == Commands.Command.REDO, "Ctrl+Shift+Z maps redo")
	key.ctrl_pressed = false
	key.meta_pressed = true
	key.keycode = KEY_S
	test.check(Commands.from_key(key) == Commands.Command.SAVE, "Platform Meta+S maps save")
	key.echo = true
	test.check(Commands.from_key(key) == -1, "Repeated keys do not repeat tool actions")

func _event_tree(test: Object) -> void:
	var events: Array = [{"id":"1", "path":"Zebra", "tags":["common"]}, {"id":"2", "path":"Music/Combat", "tags":["combat", "common"]}, {"id":"3", "path":"music/Calm", "tags":["calm"]}, {"id":"4", "path":"Ambience/Wind", "tags":[]}]
	var root = TreeModel.build(events, ["Empty"])
	test.check(root.children[0].name == "Ambience" and root.children.back().name == "Zebra", "Event folders sort first, alphabetically before leaves")
	var search = TreeModel.build(events, ["Empty"], " COMBAT ")
	test.check(search.children.size() == 1 and search.children[0].children[0].event_id == "2", "Case-insensitive trimmed search retains ancestor hierarchy")
	var tagged = TreeModel.build(events, ["Empty"], "", "common")
	test.check(tagged.children.size() == 2 and tagged.children[0].name == "Music", "Exact tag filter excludes empty folders")
	test.check(TreeModel.all_tags(events) == ["calm", "combat", "common"], "Tags are unique and ordinal sorted")
	test.check(TreeModel.normalize_tag("  dark\t  water\n") == "dark water" and TreeModel.normalize_tag("bad,tag").is_empty(), "Tag normalization collapses whitespace and rejects comma")
	test.check(TreeModel.is_under("Music/Combat", "Music") and not TreeModel.is_under("Musical/Combat", "Music"), "Folder membership respects slash boundaries")
	test.check(TreeModel.reparent("Music/Sub/Event", "Music", "SFX") == "SFX/Sub/Event", "Reparent retains nested suffix")
	test.check(TreeModel.unique_path(events, "Zebra") == "Zebra 2" and not TreeModel.is_valid_name("a/b"), "Unique event paths and slash-free names match original workflow")

func _clone_references(test: Object) -> void:
	var doc = Document.new()
	var bus = doc.project.mixer.buses[0].id
	var parameter = Model.create("ParameterDefinition", {"name":"Mood"})
	var destination = Model.create("Destination", {"name":"End", "position":2.0})
	var instrument = Model.create("Clip", {"asset":"res://test.ogg"})
	instrument.placement.parameter = parameter.id
	instrument.conditions.append(Model.create("TriggerCondition", {"parameter":parameter.id}))
	var track = Model.create("Track", {"name":"Track", "instruments":[instrument]})
	var event = Model.create("AudioEvent", {"path":"Source", "bus":bus, "localParameters":[parameter], "tracks":[track], "markers":[destination, Model.create("Transition", {"destination":destination.id})]})
	var lane = Model.create("AutomationLane", {"parameter":parameter.id, "target":Model.property_ref("InstrumentVolumeDb", instrument.id)})
	event.automation.append(lane)
	var command = Model.create("Command", {"targetEvent":event.id, "targetParameter":parameter.id})
	event.tracks[0].instruments.append(command)
	event.tags.append(event.id)
	doc.project.events.append(event)
	var clone = Cloner.clone(doc.project, event, "Copy")
	test.check(clone.id != event.id and clone.path == "Copy" and clone.bus == bus, "Event clone creates fresh identity and preserves external bus")
	test.check(clone.localParameters[0].id != parameter.id and clone.tracks[0].id != track.id and clone.tracks[0].instruments[0].id != instrument.id, "Clone recursively reidentifies all owned objects")
	test.check(clone.tracks[0].instruments[0].placement.parameter == clone.localParameters[0].id and clone.tracks[0].instruments[0].conditions[0].parameter == clone.localParameters[0].id, "Clone remaps parameter sheet and trigger references")
	test.check(clone.markers[1].destination == clone.markers[0].id, "Clone remaps transition destination")
	test.check(clone.automation[0].target.owner == clone.tracks[0].instruments[0].id and clone.automation[0].parameter == clone.localParameters[0].id, "Clone remaps automation owner and parameter")
	test.check(clone.tracks[0].instruments[1].targetEvent == clone.id and clone.tracks[0].instruments[1].targetParameter == clone.localParameters[0].id, "Clone remaps self event and command parameter")
	test.check(clone.tags[0] == event.id, "Clone never rewrites arbitrary authored strings that happen to equal IDs")
	clone.tracks[0].name = "Changed copy"
	test.check(event.tracks[0].name == "Track", "Cloned mutable structure is independent")

func _browser_workflows(test: Object) -> void:
	var doc = Document.new()
	var browser = Browser.new()
	browser.document = doc
	browser.selection = Selection.new()
	var first = browser.new_event("Music")
	test.check(doc.project.events[0].path == "Music/New Event" and doc.project.events[0].tracks.size() == 1, "Browser creates routed event with initial authored track")
	browser.rename_event(first, "Calm")
	browser.add_tag(first, "  exploration  ")
	browser.add_tag(first, "exploration")
	test.check(browser.find_event(first).path == "Music/Calm" and browser.find_event(first).tags == ["exploration"], "Browser event rename and normalized idempotent tagging")
	browser.remove_tag(first, "exploration")
	doc.undo()
	test.check(browser.find_event(first).tags == ["exploration"], "Tag removal is reversible")
	var copy = browser.duplicate_event(first)
	test.check(copy != first and browser.find_event(copy).path == "Music/Calm copy", "Browser duplicate preserves original and uniquely names deep copy")
	browser.move_event_to_folder(copy, "SFX")
	test.check(browser.find_event(copy).path == "SFX/Calm copy", "Browser drag reparent preserves basename")
	browser.rename_folder("Music", "Score")
	test.check(browser.find_event(first).path == "Score/Calm", "Folder rename changes descendant paths atomically")
	var bank = Model.create("AudioBank", {"name":"Main", "events":[first,copy]})
	doc.transact("Bank", func(): doc.project.banks.append(bank))
	browser.toggle_bank_membership(bank.id, copy)
	test.check(doc.project.banks[0].events == [first], "Bank member toggle removes without deleting event")
	doc.undo()
	browser.remove_events([first])
	test.check(doc.project.events.size() == 1 and not doc.project.banks[0].events.has(first), "Deleting events removes bank memberships")
	doc.undo()
	test.check(doc.project.events[0].id == first and doc.project.banks[0].events == [first,copy], "Undo deletion restores event order and exact bank membership order")
	var subject = Subjects.resolve(doc.project, browser.find_event(first).tracks[0].id)
	test.check(subject.kind == Selection.Kind.TRACK and subject.owner == first, "Validation subjects resolve track owning event")
	browser.free()

func _assets_spatial_themes(test: Object) -> void:
	test.check(Scanner.is_audio_file("res://A.WAV") and Scanner.is_audio_file("x.mp3") and not Scanner.is_audio_file("x.wav.import"), "Asset extensions match case-insensitively without accepting import sidecars")
	var root = {"name":"root", "path":"res://", "is_folder":true, "children":[{"name":"audio", "path":"res://audio", "is_folder":true, "children":[{"name":"Wind.ogg", "path":"res://audio/Wind.ogg", "is_folder":false, "children":[]}]}, {"name":"empty", "path":"res://empty", "is_folder":true, "children":[]}]}
	var filtered = Scanner.filter(root, "WIND")
	test.check(filtered.children.size() == 1 and filtered.children[0].children.size() == 1, "Asset filter keeps matching ancestors and excludes empty branches")
	var origin = {"position":Vector3.ZERO, "velocity":Vector3.ZERO, "forward":Vector3.FORWARD, "up":Vector3.UP}
	var emitter = origin.duplicate()
	emitter.position = Vector3(3,0,-4)
	emitter.velocity = Vector3(3,0,-4)
	var values = Preview.compute(emitter, origin, 1, 9)
	test.check(is_equal_approx(values.Distance, 5.0) and is_equal_approx(values.DistanceNormalized, 0.5), "Preview computes distance and min/max normalization")
	test.check(values.Direction > 0 and is_equal_approx(values.Speed, 5.0), "Preview uses Godot negative-Z forward, right-positive yaw and radial speed")
	test.check(Preview.rotate_yaw(Vector3.FORWARD, 90).is_equal_approx(Vector3.LEFT), "Preview wheel uses right-handed rotation")
	test.check(Preview.to_transform({"forward":Vector3.ZERO, "up":Vector3.ZERO, "position":Vector3.ONE}).basis == Basis.IDENTITY, "Degenerate preview vectors use identity basis")
	for background in [Color.BLACK, Color.WHITE, Color("777777")]:
		var palette = ThemeColors.derive(background, background, background)
		test.check(ThemeColors.contrast(palette.fg, palette.bg) >= 4.5 and ThemeColors.contrast(palette.accent, palette.bg) >= 3.0, "Derived theme enforces text and accent contrast")
	test.check(ThemeColors.dark_palette().has("marker-magnet") and ThemeColors.dark_palette().has("type-scatterer"), "Shared theme includes all authored canvas semantic colors")
