extends Node3D

const Project = preload("res://demo/demo_project.gd")
const Recorder = preload("res://addons/dirge/model/Profiling/recorder.gd")
const RecordingJson = preload("res://addons/dirge/model/Profiling/recording_json.gd")
const Replayer = preload("res://addons/dirge/model/Profiling/replayer.gd")
const ProjectFile = "res://demo/sandbox/sandbox.dirge.json"
const RecordingFile = "user://sandbox-recording.json"
const BeatPhaseTolerance = 0.02
var _server
var _player: Camera3D
var _bird_emitter: Node3D
var _hud: Label
var _music
var _birds
var _yaw = 0.0
var _pitch = 0.0
var _bird_angle = 0.0
var _threat_high = false
var _german_locale = false
var _hud_timer = 0.0
var _screenshot_path = ""
var _screenshot_frame = 0
var _screenshot_taken = false
var _smoke = false
var _smoke_step = 0
var _recorder
var _counts: Dictionary = {}
var _combat_plays = 0
var _stinger_plays = 0
var _bird_plays = 0
var _line_en_plays = 0
var _line_de_plays = 0
var _worst_combat_phase = 0.0
var _worst_stinger_phase = 0.0
var _music_min_db = INF
var _music_last_db = 0.0
var _dialogue_started = false
var _quitting = false

func _enter_tree() -> void:
	var error = Project.ensure_project_file(ProjectFile, true)
	if error != OK:
		push_error("Dirge sandbox: cannot generate project: %s" % error)

func _ready() -> void:
	_server = $DirgeServer
	_player = $Player
	_bird_emitter = $BirdEmitter
	_hud = $Hud/Label
	var args = OS.get_cmdline_user_args()
	_smoke = "--smoke" in args
	for argument in args:
		if argument.begins_with("--screenshot="):
			_screenshot_path = argument.trim_prefix("--screenshot=")
	if not _server.IsLoaded:
		_fail("project failed to load")
		return
	_server.System.locale = "en"
	_server.CommandsEmitted.connect(_on_commands)
	_server.Loaded.connect(_on_system_loaded)
	_yaw = _player.rotation.y
	_pitch = _player.rotation.x
	if _smoke:
		Engine.max_fps = 60
		_recorder = Recorder.new(_server.System)
		print("SANDBOX_SMOKE start")
	else:
		print("Dirge sandbox: 1 Threat 0/1 · 2 stinger · 3 dialogue · L locale en/de · Space click · F9 HUD · click to capture mouse, Esc to release.")
	_push_listener()
	_start_ambience()
	_update_hud()

func _on_system_loaded(_system) -> void:
	# Model instances belong to their system. Recording/live reload replaces that system.
	_music = null
	_birds = null
	_start_ambience.call_deferred()

func _start_ambience() -> void:
	_music = _server.System.get_event(Project.MusicEvent).create_instance()
	_music.start()
	_birds = _server.System.get_event(Project.BirdsEvent).create_instance()
	_birds.set_3d_attributes(_attributes(_bird_emitter.global_transform))
	_birds.start()

func _process(delta: float) -> void:
	if _quitting or not _server.IsLoaded:
		return
	if not _smoke:
		_move(delta)
	_orbit_birds(delta)
	_push_listener()
	if _smoke:
		_smoke_tick()
	elif not _screenshot_path.is_empty():
		_screenshot_tick()
	_hud_timer += delta
	if _hud_timer >= 0.1:
		_hud_timer = 0.0
		_update_hud()

func _unhandled_input(event: InputEvent) -> void:
	if _smoke or _quitting:
		return
	if event is InputEventMouseMotion and Input.mouse_mode == Input.MOUSE_MODE_CAPTURED:
		_yaw -= event.relative.x * 0.0025
		_pitch = clampf(_pitch - event.relative.y * 0.0025, -1.4, 1.4)
		_player.rotation = Vector3(_pitch, _yaw, 0.0)
	elif event is InputEventMouseButton and event.pressed and event.button_index == MOUSE_BUTTON_LEFT:
		Input.mouse_mode = Input.MOUSE_MODE_CAPTURED
	elif event is InputEventKey and event.pressed and not event.echo:
		match event.keycode:
			KEY_ESCAPE: Input.mouse_mode = Input.MOUSE_MODE_VISIBLE
			KEY_1: _toggle_threat()
			KEY_2: _server.StartEvent(Project.StingerEvent)
			KEY_3: _server.StartEvent(Project.DialogueEvent)
			KEY_L: _toggle_locale()
			KEY_SPACE: _server.StartEvent(Project.ClickEvent)
			KEY_F9: _hud.visible = not _hud.visible

func _toggle_threat() -> void:
	_threat_high = not _threat_high
	_server.SetGlobalParameter(Project.ThreatParameter, 1.0 if _threat_high else 0.0)

func _toggle_locale() -> void:
	_german_locale = not _german_locale
	_server.System.locale = "de" if _german_locale else "en"

func _move(dt: float) -> void:
	var direction = Vector3.ZERO
	if Input.is_key_pressed(KEY_W): direction.z -= 1.0
	if Input.is_key_pressed(KEY_S): direction.z += 1.0
	if Input.is_key_pressed(KEY_A): direction.x -= 1.0
	if Input.is_key_pressed(KEY_D): direction.x += 1.0
	if direction != Vector3.ZERO:
		var world = Basis.from_euler(Vector3(0.0, _yaw, 0.0)) * direction.normalized() * 4.0 * dt
		_player.position += Vector3(world.x, 0.0, world.z)

func _orbit_birds(dt: float) -> void:
	_bird_angle += 0.35 * dt
	var center = _player.global_position
	var previous = _bird_emitter.global_position
	var position = Vector3(center.x + cos(_bird_angle) * 6.0, 2.5, center.z + sin(_bird_angle) * 6.0)
	_bird_emitter.global_position = position
	var velocity = (position - previous) / dt if dt > 0.0 else Vector3.ZERO
	if _birds != null:
		_birds.set_3d_attributes(_attributes(_bird_emitter.global_transform, velocity))

func _push_listener() -> void:
	_server.System.set_listener(0, _attributes(_player.global_transform))

static func _attributes(transform: Transform3D, velocity: Vector3 = Vector3.ZERO) -> Dictionary:
	return {"position": transform.origin, "velocity": velocity, "forward": -transform.basis.z, "up": transform.basis.y}

func _update_hud() -> void:
	if not _hud.visible or not _server.IsLoaded:
		return
	var system = _server.System
	var position = _music.timeline_position if _music != null else 0.0
	var section = "Combat" if position >= Project.CombatSectionStart else "Calm"
	_hud.text = ("Dirge sandbox%s\n" % (" — smoke" if _smoke else "")
		+ "[1] Threat %.2f → target %d   section %s @ %.2fs\n" % [system.get_global_parameter(Project.ThreatParameter), int(_threat_high), section, position]
		+ "[2] stinger (beat-quantized)   [3] dialogue   [L] locale %s   [Space] click   [F9] hide HUD\n" % system.locale
		+ "WASD move · mouse look (click to capture, Esc releases)\n"
		+ "Music bus %.1f dB · instances %d · voices %d · t=%.2fs" % [_music_last_db, system.instance_count, system.real_voice_count, system.time])

func _smoke_tick() -> void:
	var time = _server.System.time
	match _smoke_step:
		0:
			if time >= 0.5:
				_server.StartEvent(Project.ClickEvent)
				_smoke_step += 1
		1:
			if time >= 1.0:
				_toggle_threat()
				_smoke_step += 1
		2:
			if time >= 2.0:
				_server.StartEvent(Project.StingerEvent)
				_smoke_step += 1
		3:
			if time >= 3.0:
				_dialogue_started = true
				_server.StartEvent(Project.DialogueEvent)
				_smoke_step += 1
		4:
			if time >= 4.0:
				_toggle_locale()
				_server.StartEvent(Project.DialogueEvent)
				_smoke_step += 1
		5:
			if time >= 6.0:
				_smoke_step += 1
				_smoke = false
				_evaluate()

func _on_commands(commands: Array) -> void:
	var phase = fmod(_music.timeline_position, Project.BeatSeconds) if _music != null else 0.0
	for command in commands:
		if command.type == "BusSetVolume" and command.bus == "Music":
			_music_last_db = command.volumeDb
		if not _smoke:
			continue
		_counts[command.type] = _counts.get(command.type, 0) + 1
		if command.type == "VoicePlay":
			var asset: String = command.asset
			match asset.get_file():
				"combat_drums.wav", "combat_lead.wav":
					_combat_plays += 1
					_worst_combat_phase = maxf(_worst_combat_phase, phase)
					print("SANDBOX_SMOKE combat layer %s at t=%.3f musicPos=%.4f phase=%.4f start=%.3f" % [asset, _server.System.time, _music.timeline_position, phase, command.startSeconds])
				"stinger.wav":
					_stinger_plays += 1
					_worst_stinger_phase = maxf(_worst_stinger_phase, phase)
					print("SANDBOX_SMOKE stinger at t=%.3f musicPos=%.4f phase=%.4f" % [_server.System.time, _music.timeline_position, phase])
				"line_en.wav": _line_en_plays += 1
				"line_de.wav": _line_de_plays += 1
				_:
					if command.spatial: _bird_plays += 1
		elif command.type == "BusSetVolume" and command.bus == "Music" and _dialogue_started:
			_music_min_db = minf(_music_min_db, command.volumeDb)

func _evaluate() -> void:
	_server.CommandsEmitted.disconnect(_on_commands)
	var recording: Dictionary = _recorder.stop()
	_recorder = null
	for kind in _counts:
		print("SANDBOX_SMOKE %s x%d" % [kind, _counts[kind]])
	var total_commands = 0
	for tick in recording.ticks:
		total_commands += tick.commands.size()
	print("SANDBOX_SMOKE recording ticks=%d commands=%d time=%.3fs" % [recording.ticks.size(), total_commands, _server.System.time])
	print("SANDBOX_SMOKE combatPlays=%d worstCombatPhase=%.4f stingerPlays=%d worstStingerPhase=%.4f" % [_combat_plays, _worst_combat_phase, _stinger_plays, _worst_stinger_phase])
	print("SANDBOX_SMOKE birdPlays=%d lineEn=%d lineDe=%d musicMinDbDuringDialogue=%.2f" % [_bird_plays, _line_en_plays, _line_de_plays, _music_min_db])
	var json = RecordingJson.serialize(recording)
	var file = FileAccess.open(RecordingFile, FileAccess.WRITE)
	if file == null:
		_fail("cannot write recording: %s" % FileAccess.get_open_error())
		return
	file.store_string(json)
	file.close()
	var loaded = RecordingJson.deserialize(json)
	var replayed = Replayer.replay(loaded, _server.System.project, _server.Seed)
	var divergence = Replayer.compare(recording, replayed)
	print("SANDBOX_SMOKE replay ticks=%d compare=%s json=%d chars → %s" % [replayed.ticks.size(), "identical" if divergence == null else str(divergence), json.length(), ProjectSettings.globalize_path(RecordingFile)])
	if divergence != null:
		_fail("replay diverged: " + str(divergence))
	elif _combat_plays < 2:
		_fail("expected both Combat layers, saw %d VoicePlays" % _combat_plays)
	elif _worst_combat_phase >= BeatPhaseTolerance:
		_fail("Combat layers started %.4fs past beat (tolerance %.2f)" % [_worst_combat_phase, BeatPhaseTolerance])
	elif _stinger_plays < 1:
		_fail("stinger never played")
	elif _worst_stinger_phase >= BeatPhaseTolerance:
		_fail("stinger started %.4fs past beat (tolerance %.2f)" % [_worst_stinger_phase, BeatPhaseTolerance])
	elif _line_en_plays != 1 or _line_de_plays != 1:
		_fail("expected one en and one de line; en=%d de=%d" % [_line_en_plays, _line_de_plays])
	elif not _music_min_db <= -10.0:
		_fail("Music bus only dipped to %.2f dB during dialogue, expected <= -10 dB" % _music_min_db)
	elif _bird_plays < 1:
		_fail("no spatial bird voice played")
	elif AudioServer.get_bus_index("Music") < 0 or AudioServer.get_bus_index("Dialogue") < 0:
		_fail("engine Music or Dialogue bus missing")
	else:
		print("SANDBOX_SMOKE PASS")
		_quit(0)

func _screenshot_tick() -> void:
	if _screenshot_taken:
		return
	match _screenshot_frame:
		30: _toggle_threat()
		150: _server.StartEvent(Project.DialogueEvent)
		200:
			_screenshot_taken = true
			_update_hud()
			_save_screenshot.call_deferred()
	_screenshot_frame += 1

func _save_screenshot() -> void:
	await RenderingServer.frame_post_draw
	var image = get_viewport().get_texture().get_image()
	var error = image.save_png(_screenshot_path)
	print("SANDBOX_SCREENSHOT %s %s %dx%d" % [_screenshot_path, error, image.get_width(), image.get_height()])
	_quit(0 if error == OK else 1)

func _fail(reason: String) -> void:
	printerr("SANDBOX_SMOKE FAIL: " + reason)
	print("SANDBOX_SMOKE FAIL: " + reason)
	_quit(1)

func _quit(code: int) -> void:
	if _quitting:
		return
	_quitting = true
	if _server.CommandsEmitted.is_connected(_on_commands):
		_server.CommandsEmitted.disconnect(_on_commands)
	_server.StopAllEvents(true)
	await get_tree().process_frame
	await get_tree().create_timer(0.2).timeout
	get_tree().quit(code)
