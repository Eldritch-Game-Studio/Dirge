extends Node

const Project = preload("res://demo/demo_project.gd")
const Scanner = preload("res://addons/dirge/tool/Browser/asset_scanner.gd")
const ProjectFile = "res://demo/demo.dirge.json"
var _server
var _smoke = false
var _frame = 0
var _start_msec = 0
var _muffled
var _counts: Dictionary = {}
var _wind_voice = -1
var _click_voice = -1
var _click_finished = false
var _click_stopped = false

func _enter_tree() -> void:
	var error = Project.ensure_project_file(ProjectFile)
	if error != OK:
		push_error("Dirge demo: cannot generate project: %s" % error)

func _ready() -> void:
	_server = $DirgeServer
	_smoke = "--smoke" in OS.get_cmdline_user_args()
	if not _server.IsLoaded:
		_fail("project failed to load")
		return
	_server.CommandsEmitted.connect(_on_commands)
	_server.FeedbackSent.connect(_on_feedback)
	_start_msec = Time.get_ticks_msec()
	if _smoke:
		Engine.max_fps = 60
		print("DIRGE_SMOKE start")
	else:
		print("Dirge demo: any key = UI/Click, Enter = toggle Muffled snapshot.")
		_server.StartEvent(Project.WindEvent)

func _process(_delta: float) -> void:
	if not _smoke:
		return
	match _frame:
		0: _server.StartEvent(Project.WindEvent)
		10: _server.StartEvent(Project.ClickEvent)
		20: _server.StartSnapshot(Project.MuffledSnapshot)
	_frame += 1
	if _frame >= 90 and Time.get_ticks_msec() - _start_msec >= 1500:
		_smoke = false
		_evaluate()

func _unhandled_key_input(event: InputEvent) -> void:
	if _smoke or not event is InputEventKey or not event.pressed or event.echo:
		return
	if event.keycode == KEY_ENTER:
		if _muffled == null:
			_muffled = _server.System.mixer.start_snapshot(Project.MuffledSnapshot)
			print("Muffled on")
		else:
			_muffled.stop()
			_muffled = null
			print("Muffled off")
	else:
		_server.StartEvent(Project.ClickEvent)

func _on_commands(commands: Array) -> void:
	for command in commands:
		var kind: String = command.type
		_counts[kind] = _counts.get(kind, 0) + 1
		if kind == "VoicePlay":
			if command.asset == Project.WindAsset:
				_wind_voice = command.voice
			elif command.asset == Project.ClickAsset:
				_click_voice = command.voice
		elif kind == "VoiceStop" and command.voice == _click_voice:
			_click_stopped = true

func _on_feedback(feedback: Dictionary) -> void:
	if feedback.type == "VoiceFinished" and feedback.voice == _click_voice:
		_click_finished = true

func _evaluate() -> void:
	for kind in _counts:
		print("DIRGE_SMOKE %s x%d" % [kind, _counts[kind]])
	var ambience = AudioServer.get_bus_index("Ambience")
	if AudioServer.get_bus_index("Master") != 0:
		_fail("Master bus is not index 0")
	elif ambience < 0:
		_fail("Ambience bus missing")
	elif AudioServer.get_bus_effect_count(ambience) != 1:
		_fail("Ambience must have exactly one effect")
	elif not AudioServer.get_bus_effect(ambience, 0) is AudioEffectLowPassFilter:
		_fail("Ambience effect 0 is not AudioEffectLowPassFilter")
	elif _wind_voice < 0 or not _server.Driver.is_voice_playing(_wind_voice):
		_fail("wind voice is not playing")
	elif _click_voice < 0:
		_fail("no VoicePlay for click.wav")
	elif not _click_finished and not _click_stopped:
		_fail("click voice neither finished nor stopped by the model")
	elif not AudioServer.get_bus_volume_db(ambience) <= Project.MuffledAmbienceDb + 1.0:
		_fail("Ambience was not muffled to <= -17 dB")
	else:
		var scanner_error = _verify_asset_scanner()
		if not scanner_error.is_empty():
			_fail("asset scanner: " + scanner_error)
			return
		print("DIRGE_SMOKE PASS")
		_server.StopAllEvents(true)
		# Let the audio thread consume stop requests before SceneTree teardown.
		await get_tree().create_timer(0.05).timeout
		get_tree().quit(0)

func _verify_asset_scanner() -> String:
	var suffix = str(Time.get_ticks_usec())
	var resource = "res://scanner-smoke-" + suffix
	var external = "user://dirge-scanner-" + suffix
	var error = ""
	for directory in [resource, resource + "/ignored", external]:
		if DirAccess.make_dir_recursive_absolute(directory) != OK:
			error = "cannot create scanner fixture " + directory
	for path in [resource + "/visible.wav", resource + "/ignored/.gdignore", resource + "/ignored/hidden.wav", external + "/outside.wav"]:
		var file = FileAccess.open(path, FileAccess.WRITE)
		if file == null:
			error = "cannot write scanner fixture " + path
		else:
			file.close()
	if OS.get_name() != "Windows" and error.is_empty():
		var directory = DirAccess.open(resource)
		if directory.create_link(ProjectSettings.globalize_path(external), "linked") != OK or directory.create_link(ProjectSettings.globalize_path(external + "/outside.wav"), "linked.wav") != OK:
			error = "cannot create scanner symlink fixtures"
	if error.is_empty():
		var scanner = Scanner.new()
		scanner.scan()
		var found: Array = []
		for path in scanner.files:
			if path.begins_with(resource + "/"):
				found.append(path)
		if found != [resource + "/visible.wav"]:
			error = "Ignored or linked audio escaped scan boundary: " + str(found)
	for path in [resource + "/linked", resource + "/linked.wav", resource + "/visible.wav", resource + "/ignored/.gdignore", resource + "/ignored/hidden.wav", resource + "/ignored", resource, external + "/outside.wav", external]:
		if FileAccess.file_exists(path) or DirAccess.dir_exists_absolute(path):
			DirAccess.remove_absolute(path)
	return error

func _fail(reason: String) -> void:
	printerr("DIRGE_SMOKE FAIL: " + reason)
	print("DIRGE_SMOKE FAIL: " + reason)
	get_tree().quit(1)
