extends SceneTree

var failures: Array[String] = []
var observations: Dictionary = {}
var output: String = ""
var sandbox
var server
var overlay

func _initialize() -> void:
	for argument in OS.get_cmdline_user_args():
		if argument.begins_with("--dirge-proof-out="):
			output = argument.trim_prefix("--dirge-proof-out=")
	_run.call_deferred()

func check(condition: bool, message: String) -> void:
	observations[message] = condition
	if not condition:
		failures.append(message)
		push_error("Dirge overlay proof: " + message)

func _settle(frames: int = 5) -> void:
	for frame in frames:
		await process_frame

func _key(code: int) -> void:
	var event = InputEventKey.new()
	event.keycode = code
	event.physical_keycode = code
	event.pressed = true
	Input.parse_input_event(event)
	event = event.duplicate()
	event.pressed = false
	Input.parse_input_event(event)

func _capture(name: String) -> void:
	await RenderingServer.frame_post_draw
	var image = root.get_texture().get_image()
	check(image.get_width() >= 1000, name + " real rendered viewport")
	check(image.save_png(output.path_join(name + ".png")) == OK, name + " screenshot saved")

func _run() -> void:
	check(not OS.has_feature("mono"), "standard Godot runtime without Mono")
	root.size = Vector2i(1600, 1000)
	sandbox = load("res://demo/sandbox/sandbox.tscn").instantiate()
	root.add_child(sandbox)
	current_scene = sandbox
	server = sandbox.get_node("DirgeServer")
	await create_timer(0.5).timeout
	check(server.IsLoaded, "actual installed sandbox project loaded")
	check(server.GetStats().real_voices > 0, "actual sandbox audio voices playing")
	overlay = root.get_node_or_null("DirgeOverlay")
	if overlay == null:
		overlay = load("res://addons/dirge/runtime/dirge_overlay.gd").new()
		overlay.name = "DirgeOverlay"
		overlay.server_path = server.get_path()
		root.add_child(overlay)
	else:
		overlay.server_path = server.get_path()
	await _settle()
	_key(KEY_F9)
	await _settle(15)
	check(overlay.IsShown, "actual F9 input opens runtime overlay")
	if not overlay.IsShown:
		_finish()
		return
	check(overlay.server == server, "overlay bound to running sandbox audio server")
	check(overlay.mixer.host.get_class() == "DocumentHost", "mixer uses actual native HTML/CSS renderer")
	check(overlay.mixer.host.has_element("#bus-add"), "real mixer authoring controls mounted")
	await _capture("runtime-mixer")
	overlay.tabs.current_tab = 1
	await _settle()
	var profiler = overlay.profiler
	check(profiler.host.get_class() == "DocumentHost", "profiler uses actual native HTML/CSS renderer")
	profiler.host.click("#rec-start")
	await _settle()
	check(profiler.is_recording, "real profiler record button starts capture")
	check(sandbox._birds.system == server.System and sandbox._music.system == server.System, "sandbox reacquires model instances after recorder resets the system")
	check(server.GetStats().real_voices > 0, "sandbox ambience resumes on the recorded system")
	server.StartEvent("UI/Click")
	_key(KEY_1)
	await create_timer(0.3).timeout
	server.StartEvent("Dialogue/Line")
	await create_timer(0.3).timeout
	profiler.host.click("#rec-stop")
	await _settle()
	check(not profiler.is_recording and not profiler.recording.is_empty(), "real profiler stop retains recording")
	check(float(profiler.host.get_text("#c-time")) > 0, "live profiler time is updated")
	await _capture("runtime-profiler")
	_key(KEY_F9)
	await _settle()
	check(not overlay.IsShown, "F9 closes actual overlay")
	_key(KEY_F9)
	await _settle()
	check(overlay.IsShown and overlay.profiler == profiler, "F9 reopens retained profiler and recording")
	overlay.HideOverlay()
	server.StopAllEvents(true)
	await _settle()
	_finish()

func _finish() -> void:
	var file = FileAccess.open(output.path_join("overlay-behavior.json"), FileAccess.WRITE)
	if file != null:
		file.store_string(JSON.stringify({"status": "PASS" if failures.is_empty() else "FAIL", "observations": observations, "failures": failures}, "\t") + "\n")
	if is_instance_valid(overlay):
		overlay.queue_free()
	if is_instance_valid(sandbox):
		sandbox.queue_free()
	await _settle()
	print("DIRGE_OVERLAY_PROOF ", "PASS" if failures.is_empty() else "FAIL")
	quit(0 if failures.is_empty() else 1)
