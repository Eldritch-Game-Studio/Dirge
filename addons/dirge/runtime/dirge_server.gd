@tool
extends Node

const Version = "1.1.0"
const ProjectPathSetting = "dirge/project_path"
const SignalMask = 1023 & ~32
const Catalog = preload("res://addons/dirge/runtime/asset_catalog.gd")
const BankLoader = preload("res://addons/dirge/runtime/banks.gd")
const DriverType = preload("res://addons/dirge/runtime/audio_driver.gd")
const CaptureType = preload("res://addons/dirge/runtime/loudness_capture.gd")
const SystemType = preload("res://addons/dirge/model/Runtime/dirge_system.gd")
const ProjectJson = preload("res://addons/dirge/model/Serialization/project_json.gd")
const LiveHost = preload("res://addons/dirge/runtime/live_update_host.gd")

signal EventStarted(handle: int)
signal EventStopped(handle: int)
signal TimelineMarker(handle: int, marker_name: String, position: float)
signal TimelineBeat(handle: int, bar: int, beat: int, bpm: float, position: float)
signal SoundPlayed(handle: int, asset: String)
signal EventVirtualized(handle: int)
signal EventUnvirtualized(handle: int)
signal EventStolen(handle: int)
signal ProgrammerSound(handle: int, key: String)
signal CommandsEmitted(commands: Array)
signal FeedbackSent(message: Dictionary)
signal Loaded(system)

@export_file("*.json") var ProjectPath: String = ""
@export var Seed: int = 0
@export var LiveUpdatePort: int = 0
@export var MeterLoudness: bool = true
# Only the authoring shell opts into editor audio ownership; scene/autoload previews stay inert.
var editor_audition: bool = false
var Assets = Catalog.new()
var Banks = BankLoader.new(Assets)
var Driver
var System
var Live
var Loudness
var LastUpdateMs: float = 0.0
var Platform: String = map_platform(OS.get_name())
var Locale: String:
	get: return TranslationServer.get_locale()
var IsLoaded: bool:
	get: return System != null
var Handles: Dictionary = {}
var Stats: Dictionary:
	get: return GetStats()
var _snapshots: Dictionary = {}
var _handle_of: Dictionary = {}
var _next_handle: int = 1
var _pending_live_port: int = 0
var _last_update_usec: int = 0
var _programmer_request: Dictionary = {}
var _programmer_request_handle: int = 0

func _ready() -> void:
	if Engine.is_editor_hint() and not editor_audition:
		set_process(false)
		return
	Driver = DriverType.new(Assets)
	add_child(Driver)
	Driver.feedback.connect(_on_feedback)
	if MeterLoudness:
		Loudness = CaptureType.new()
		Driver.capture = Loudness
	_pending_live_port = LiveUpdatePort
	for argument in OS.get_cmdline_user_args():
		if argument == "--dirge-verbose":
			DriverType.verbose = true
		elif argument == "--dirge-live":
			_pending_live_port = 7447
		elif argument.begins_with("--dirge-live="):
			var value = argument.trim_prefix("--dirge-live=")
			if value.is_valid_int() and int(value) > 0 and int(value) <= 65535:
				_pending_live_port = int(value)
			else:
				push_error("Dirge: invalid --dirge-live port '%s'; using 7447." % value)
				_pending_live_port = 7447
	var path = ProjectPath
	if path.is_empty():
		path = str(ProjectSettings.get_setting(ProjectPathSetting, ""))
	if not path.is_empty():
		LoadFile(path)

func _enter_tree() -> void:
	if Driver != null and MeterLoudness and Loudness == null:
		Loudness = CaptureType.new()
		Driver.capture = Loudness

func _exit_tree() -> void:
	Unload()
	DisableLiveUpdate()
	if Driver != null:
		Driver.shutdown()
	if Loudness != null:
		Loudness.dispose()
	Loudness = null

func _notification(what: int) -> void:
	if what == NOTIFICATION_PAUSED or what == NOTIFICATION_DISABLED:
		_last_update_usec = 0

func _elapsed_since_last_update() -> float:
	return 0.0 if _last_update_usec == 0 else (Time.get_ticks_usec() - _last_update_usec) / 1000000.0

func _process(_delta: float) -> void:
	Banks.poll()
	if Loudness != null:
		Loudness.poll()
	if System == null:
		return
	var now = Time.get_ticks_usec()
	var elapsed = 0.0 if _last_update_usec == 0 else (now - _last_update_usec) / 1000000.0
	_last_update_usec = now
	System.update(elapsed)
	LastUpdateMs = (Time.get_ticks_usec() - now) / 1000.0
	flush()
	_sweep()

func flush() -> void:
	if System == null or Driver == null:
		return
	CommandsEmitted.emit(System.commands)
	Driver.apply(System.commands)
	System.commands.clear()

func _on_feedback(message: Dictionary) -> void:
	if System != null:
		FeedbackSent.emit(message)
		System.feedback(message)

func EnableLiveUpdate(port: int) -> void:
	_pending_live_port = 0
	if Live == null:
		Live = LiveHost.new(self)
		add_child(Live)
	Live.listen(port)

func DisableLiveUpdate() -> void:
	if Live == null:
		return
	Live.close()
	remove_child(Live)
	Live.queue_free()
	Live = null

func Unload() -> void:
	if System != null:
		System.stop_all("Immediate")
		System.update(0.0)
		flush()
		System = null
	if Driver != null:
		Driver.stop_all()
	for handle in Handles.keys():
		_untrack(handle)
	_snapshots.clear()
	_last_update_usec = 0

func LoadFile(path: String) -> void:
	if not FileAccess.file_exists(path):
		push_error("Dirge: project file '%s' does not exist." % path)
		return
	var project = ProjectJson.load_file(path)
	if not project.is_empty():
		Load(project)

func Load(project: Dictionary) -> void:
	if Driver == null:
		push_error("Dirge: Load requires the server to be inside the scene tree.")
		return
	Unload()
	if not Driver.load(project):
		return
	System = SystemType.new(project, Seed, Assets, Platform, _elapsed_since_last_update)
	System.locale = Locale
	_last_update_usec = 0
	if Loudness != null:
		Loudness.reset()
	if _pending_live_port > 0:
		EnableLiveUpdate(_pending_live_port)
	Loaded.emit(System)

func HotReload(project: Dictionary) -> void:
	if System == null:
		Load(project)
		return
	var globals: Dictionary = {}
	for parameter in System.project.get("globalParameters", []):
		globals[parameter.name] = System.get_global_parameter(parameter.name)
	var running: Array = []
	for handle in Handles:
		var instance = Handles[handle]
		if instance.state not in ["Stopped", "Stopping"]:
			running.append({"handle": handle, "path": instance.description.path, "position": instance.timeline_position})
	var snapshots: Array = []
	for snapshot_name in _snapshots:
		for snapshot in _snapshots[snapshot_name]:
			if snapshot.is_active:
				snapshots.append(snapshot_name)
	var next_handle = _next_handle
	Load(project)
	_next_handle = next_handle
	if System == null:
		return
	for parameter in project.get("globalParameters", []):
		if globals.has(parameter.name):
			System.set_global_parameter(parameter.name, globals[parameter.name], true)
	for snapshot_name in snapshots:
		if System.mixer.has_snapshot(snapshot_name):
			StartSnapshot(snapshot_name)
	for item in running:
		var description = System.get_event(item.path)
		if description == null:
			continue
		var instance = description.create_instance()
		_track(item.handle, instance)
		instance.start()
		instance.release()
		if item.position > 0 and item.position < description.length:
			instance.timeline_position = item.position

func LiveSetBusVolume(bus: String, volume_db: float) -> bool:
	if System != null:
		for authored in System.project.get("mixer", {}).get("buses", []):
			if authored.name == bus:
				System.mixer.set_bus_volume(bus, volume_db - authored.get("volumeDb", 0.0))
				return true
	return false

func LiveSetEffectParameter(bus: String, effect_index: int, parameter: String, value: float) -> bool:
	return Driver != null and Driver.set_effect_parameter(bus, effect_index, parameter, value)

func StartEvent(path: String) -> int:
	var description = System.get_event(path) if System != null else null
	if description == null:
		push_error("Dirge: unknown event '%s'." % path)
		return 0
	var instance = description.create_instance()
	var handle = _next_handle
	_next_handle += 1
	_track(handle, instance)
	instance.start()
	instance.release()
	return handle

func StopEvent(handle: int, immediate: bool = false) -> void:
	if Handles.has(handle):
		Handles[handle].stop("Immediate" if immediate else "AllowFadeout")

func StopAllEvents(immediate: bool = false) -> void:
	if System != null:
		System.stop_all("Immediate" if immediate else "AllowFadeout")

func SetParameter(handle: int, parameter: String, value: float) -> void:
	if Handles.has(handle):
		Handles[handle].set_parameter(parameter, value)

func GetParameter(handle: int, parameter: String) -> float:
	return Handles[handle].get_parameter(parameter) if Handles.has(handle) else 0.0

func SetGlobalParameter(parameter: String, value: float) -> void:
	if System != null:
		System.set_global_parameter(parameter, value)

func GetGlobalParameter(parameter: String) -> float:
	return System.get_global_parameter(parameter) if System != null else 0.0

func StartSnapshot(snapshot_name: String) -> void:
	if System == null:
		return
	var instance = System.mixer.start_snapshot(snapshot_name)
	if instance == null:
		return
	if not _snapshots.has(snapshot_name):
		_snapshots[snapshot_name] = []
	_snapshots[snapshot_name].append(instance)

func StopSnapshot(snapshot_name: String) -> void:
	for instance in _snapshots.get(snapshot_name, []):
		instance.stop()
	_snapshots.erase(snapshot_name)

func IsSnapshotActive(snapshot_name: String) -> bool:
	return _snapshots.has(snapshot_name) and not _snapshots[snapshot_name].is_empty()

func IsEventPlaying(handle: int) -> bool:
	return Handles.has(handle) and Handles[handle].state != "Stopped"

func GetInstance(handle: int):
	return Handles.get(handle)

func SetCallbackMask(handle: int, mask: int) -> void:
	if Handles.has(handle):
		Handles[handle].callback_mask = mask & SignalMask

func SetProgrammerSound(handle: int, asset_path: String) -> void:
	if not Handles.has(handle):
		return
	if not _programmer_request.is_empty() and _programmer_request_handle == handle:
		_programmer_request["asset"] = asset_path
	Handles[handle].set_programmer_sound(asset_path)

func LoadBank(bank_name: String) -> bool:
	return System != null and Banks.load_bank(System.project, bank_name)

func UnloadBank(bank_name: String) -> void:
	Banks.unload(bank_name)

func IsBankLoaded(bank_name: String) -> bool:
	return Banks.is_loaded(bank_name)

func GetPlatform() -> String:
	return Platform

func GetLocale() -> String:
	return Locale

func GetStats() -> Dictionary:
	return {"time": System.time if System != null else 0.0,
		"instances": System.instance_count if System != null else 0,
		"real_voices": System.real_voice_count if System != null else 0,
		"update_ms": LastUpdateMs, "handles": Handles.size(),
		"momentary_lufs": Loudness.momentary_lufs if Loudness != null else -INF,
		"short_term_lufs": Loudness.short_term_lufs if Loudness != null else -INF,
		"integrated_lufs": Loudness.integrated_lufs if Loudness != null else -INF,
		"true_peak_dbtp": Loudness.true_peak_dbtp if Loudness != null else -INF}

func _track(handle: int, instance) -> void:
	Handles[handle] = instance
	_handle_of[instance] = handle
	instance.callback_mask = SignalMask
	instance.callback.connect(_on_instance_callback)

func _untrack(handle: int) -> void:
	var instance = Handles.get(handle)
	if instance == null:
		return
	if instance.callback.is_connected(_on_instance_callback):
		instance.callback.disconnect(_on_instance_callback)
	_handle_of.erase(instance)
	Handles.erase(handle)

func _sweep() -> void:
	for handle in Handles.keys():
		if Handles[handle].state == "Stopped":
			_untrack(handle)
	for snapshot_name in _snapshots.keys():
		var list: Array = _snapshots[snapshot_name]
		for index in range(list.size() - 1, -1, -1):
			if not list[index].is_active:
				list.remove_at(index)
		if list.is_empty():
			_snapshots.erase(snapshot_name)

func _on_instance_callback(instance, event: Dictionary) -> void:
	if not _handle_of.has(instance):
		return
	var handle: int = _handle_of[instance]
	match event.get("type", ""):
		"StartedCallback": EventStarted.emit(handle)
		"StoppedCallback": EventStopped.emit(handle)
		"MarkerCallback": TimelineMarker.emit(handle, event.name, event.position)
		"BeatCallback": TimelineBeat.emit(handle, event.bar, event.beat, event.bpm, event.position)
		"SoundPlayedCallback": SoundPlayed.emit(handle, Catalog.asset_path(event.asset))
		"VirtualizedCallback": EventVirtualized.emit(handle)
		"UnvirtualizedCallback": EventUnvirtualized.emit(handle)
		"StolenCallback": EventStolen.emit(handle)
		"ProgrammerSoundCallback":
			_programmer_request = event
			_programmer_request_handle = handle
			ProgrammerSound.emit(handle, event.key)
			_programmer_request = {}

static func map_platform(os_name: String) -> String:
	match os_name:
		"Windows", "UWP": return "windows"
		"macOS": return "macos"
		"Linux", "FreeBSD", "NetBSD", "OpenBSD", "BSD": return "linux"
		"Android": return "android"
		"iOS", "visionOS": return "ios"
		"Web": return "web"
		_: return os_name.to_lower()
