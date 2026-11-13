@tool
extends EditorPlugin

var failures: Array[String] = []
var observations: Dictionary = {}
var output: String = ""
var screen

func _enter_tree() -> void:
	for argument in OS.get_cmdline_user_args():
		if argument.begins_with("--dirge-proof-out="):
			output = argument.trim_prefix("--dirge-proof-out=")
	if not output.is_empty():
		_run.call_deferred()

func check(condition: bool, message: String) -> void:
	observations[message] = condition
	if not condition:
		failures.append(message)
		push_error("Dirge editor proof: " + message)

func _find_screen(node: Node):
	var script = node.get_script()
	if script != null and script.resource_path == "res://addons/dirge/tool/Shell/dirge_main_screen.gd":
		return node
	for child in node.get_children():
		var result = _find_screen(child)
		if result != null:
			return result
	return null

func _settle(frames: int = 4) -> void:
	for frame in frames:
		await get_tree().process_frame

func _key(code: int, unicode: int = 0, control: bool = false) -> void:
	var event = InputEventKey.new()
	event.keycode = code
	event.physical_keycode = code
	event.unicode = unicode
	event.ctrl_pressed = control
	event.pressed = true
	Input.parse_input_event(event)
	event = event.duplicate()
	event.pressed = false
	Input.parse_input_event(event)

func _event(id: String) -> Dictionary:
	for item in screen.document.project.get("events", []):
		if item.id == id:
			return item
	return {}

func _capture(name: String) -> void:
	await RenderingServer.frame_post_draw
	var image = get_viewport().get_texture().get_image()
	check(image.get_width() >= 1000 and image.get_height() >= 600, name + " real editor viewport dimensions")
	check(image.save_png(output.path_join(name + ".png")) == OK, name + " screenshot saved")

func _run() -> void:
	check(not OS.has_feature("mono"), "standard Godot host without Mono")
	var scene_server = preload("res://addons/dirge/runtime/dirge_server.gd").new()
	scene_server.ProjectPath = "res://demo/sandbox/sandbox.dirge.json"
	add_child(scene_server)
	check(scene_server.Driver == null and scene_server.System == null, "ordinary editor scene server does not take audio ownership")
	remove_child(scene_server)
	scene_server.free()
	for frame in 600:
		screen = _find_screen(get_tree().root)
		if screen != null and screen.document != null and screen.events != null:
			break
		await get_tree().process_frame
	check(screen != null, "installed editor plugin mounted real Dirge main screen")
	if screen == null:
		_finish()
		return
	get_editor_interface().set_main_screen_editor("Dirge")
	check(screen.load_project_file("res://demo/sandbox/sandbox.dirge.json"), "normal main screen project open")
	var id = "5a4db0c000000000000000000000000b"
	screen.select_event(id)
	await _settle(20)
	check(screen.is_visible_in_tree(), "actual Dirge main screen visible")
	var host = screen.events.header.host
	check(host != null and host.get_class() == "DocumentHost", "event authoring uses real native NecronomiDOM")
	var field = "#event-header-form-toolbar-4"
	check(host.has_element(field), "event path authoring control exists")
	if not host.has_element(field):
		_finish()
		return
	var original = str(_event(id).path)
	var edited = "Music/InstalledProof"
	host.grab_focus()
	host.focus_element(field)
	_key(KEY_A, 0, true)
	for index in edited.length():
		_key(0, edited.unicode_at(index))
	_key(KEY_TAB)
	await _settle()
	check(_event(id).path == edited, "native keyboard edits actual authored event path")
	check(screen.document.dirty, "authoring change marks document dirty")
	screen.undo()
	await _settle()
	check(_event(id).path == original, "main screen undo restores authored path")
	screen.redo()
	await _settle()
	check(_event(id).path == edited, "main screen redo reapplies authored path")
	var project_path = output.path_join("authored.dirge.json")
	check(screen.write_project_file(project_path), "normal Save As writes project")
	check(not screen.document.dirty, "successful save clears dirty state")
	check(screen.load_project_file(project_path), "normal reopen reads saved project")
	check(_event(id).path == edited, "save/reopen retains actual authored edit")
	screen.select_event(id)
	await _settle()
	var handle = screen.transport.play()
	check(handle > 0, "editor transport starts real event preview")
	await get_tree().create_timer(0.3).timeout
	check(screen.audition.IsLoaded and screen.audition.GetStats().real_voices > 0, "editor audition produces real runtime voices")
	screen.validate_project()
	await _capture("editor-events")
	screen.transport.stop_all()
	check(screen.mixer != null and screen.profiler != null, "mixer and profiler mounted in real editor shell")
	screen.bottom_tabs.current_tab = 0
	await _settle()
	await _capture("editor-authoring")
	screen.bottom_tabs.current_tab = 1
	await _settle()
	await _capture("editor-profiler")
	_finish()

func _finish() -> void:
	var file = FileAccess.open(output.path_join("editor-behavior.json"), FileAccess.WRITE)
	if file != null:
		file.store_string(JSON.stringify({"status": "PASS" if failures.is_empty() else "FAIL", "observations": observations, "failures": failures}, "\t") + "\n")
	print("DIRGE_EDITOR_PROOF ", "PASS" if failures.is_empty() else "FAIL")
	get_tree().quit(0 if failures.is_empty() else 1)
